using Microsoft.Win32.SafeHandles;

namespace DataGlitchCore.Core;

/// <summary>
/// Un fichier du buffer : l'original, un segment, une passe en construction.
/// Lu par positions, écrit par positions. Les lectures concurrentes sont
/// sûres ; l'écriture n'a lieu que pendant la construction d'une passe, sous
/// le verrou de l'application.
/// </summary>
internal interface IBlob
{
    /// <summary>Nom du fichier, relatif au dossier de travail (<c>tmp/…</c>, <c>segments/…</c>).</summary>
    string Name { get; }

    long Length { get; }

    /// <summary>Lit exactement <paramref name="destination"/>.Length octets à <paramref name="offset"/>.</summary>
    void Read(long offset, Span<byte> destination);

    void Write(long offset, ReadOnlySpan<byte> bytes);

    /// <summary>Force le contenu sur le disque (écriture sûre, §9.2).</summary>
    void Flush();
}

/// <summary>
/// Où vivent les fichiers d'un buffer : de vrais fichiers dans le dossier de
/// travail, ou la mémoire (tests, injection de pannes). Spec §4.2.
/// </summary>
internal interface IBlobStore
{
    /// <summary>L'original, en lecture seule.</summary>
    IBlob Source { get; }

    /// <summary>Un fichier neuf dans <c>tmp/</c>.</summary>
    IBlob CreateTemp(string name);

    /// <summary>Force le fichier sur le disque, puis le renomme dans <c>segments/</c>, puis force le dossier.</summary>
    IBlob Publish(IBlob blob, string name);

    /// <summary>Supprime un fichier ; sans effet s'il n'existe plus.</summary>
    void Delete(IBlob blob);

    /// <summary>Lâche ce que le store tient ouvert.</summary>
    void Close();
}

/// <summary>Tout en mémoire : <see cref="FileBuffer.FromBytes(string, byte[])"/>, les tests, l'injection de pannes.</summary>
internal sealed class MemoryBlobStore(byte[] source) : IBlobStore
{
    private readonly Dictionary<string, MemoryBlob> _files = new();

    /// <summary>Pour les tests : appelée avant chaque écriture ou publication, elle peut lever pour simuler une panne.</summary>
    public Action<string, string>? Fault { get; set; }

    public IBlob Source { get; } = new MemoryBlob("source", source, readOnly: true);

    /// <summary>Les fichiers présents, pour vérifier qu'il ne reste rien après une passe abandonnée.</summary>
    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_files) return _files.Keys.ToList();
        }
    }

    public IBlob CreateTemp(string name)
    {
        var blob = new MemoryBlob("tmp/" + name, [], readOnly: false) { Store = this };
        lock (_files) _files[blob.Name] = blob;
        return blob;
    }

    public IBlob Publish(IBlob blob, string name)
    {
        Fault?.Invoke("publier", blob.Name);
        var memory = (MemoryBlob)blob;
        lock (_files)
        {
            _files.Remove(memory.Name);
            memory.Name = "segments/" + name;
            _files[memory.Name] = memory;
        }
        return memory;
    }

    public void Delete(IBlob blob)
    {
        lock (_files) _files.Remove(blob.Name);
    }

    public void Close()
    {
    }

    private sealed class MemoryBlob(string name, byte[] data, bool readOnly) : IBlob
    {
        private byte[] _data = data;
        private long _length = data.LongLength;

        public string Name { get; set; } = name;
        public MemoryBlobStore? Store { get; init; }
        public long Length => Volatile.Read(ref _length);

        public void Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset + destination.Length > Length)
                throw new IOException($"Lecture hors de {Name} : [{offset}, {offset + destination.Length}) pour {Length} octets.");
            _data.AsSpan((int)offset, destination.Length).CopyTo(destination);
        }

        public void Write(long offset, ReadOnlySpan<byte> bytes)
        {
            if (readOnly) throw new InvalidOperationException($"{Name} est en lecture seule.");
            Store?.Fault?.Invoke("écrire", Name);
            long end = offset + bytes.Length;
            if (end > _data.Length)
            {
                long capacity = Math.Max(end, Math.Max(256, (long)_data.Length * 2));
                Array.Resize(ref _data, (int)Math.Min(capacity, Array.MaxLength));
            }
            bytes.CopyTo(_data.AsSpan((int)offset));
            if (end > _length) Volatile.Write(ref _length, end);
        }

        public void Flush() => Store?.Fault?.Invoke("forcer", Name);
    }
}

/// <summary>
/// De vrais fichiers : l'original là où l'application l'a copié, les segments
/// dans <c>segments/</c>, les passes en construction dans <c>tmp/</c> (spec §5.3).
/// Les lectures passent par <see cref="FileHandles"/>, la réserve de
/// descripteurs du processus.
/// </summary>
internal sealed class FileBlobStore : IBlobStore
{
    private readonly string _directory;
    private readonly bool _durable;
    private readonly List<FileBlob> _open = new();

    public FileBlobStore(string sourcePath, string workDirectory, bool durable = true)
    {
        _durable = durable;
        _directory = Path.GetFullPath(workDirectory);
        Directory.CreateDirectory(Path.Combine(_directory, "tmp"));
        Directory.CreateDirectory(Path.Combine(_directory, "segments"));
        string source = Path.GetFullPath(sourcePath);
        Source = new FileBlob(Path.GetFileName(source), source, new FileInfo(source).Length, writable: false);
    }

    public IBlob Source { get; }

    public IBlob CreateTemp(string name)
    {
        string path = Path.Combine(_directory, "tmp", name);
        var blob = new FileBlob("tmp/" + name, path, 0, writable: true);
        blob.CreateEmpty();
        lock (_open) _open.Add(blob);
        return blob;
    }

    public IBlob Publish(IBlob blob, string name)
    {
        var file = (FileBlob)blob;
        file.CloseWriter();
        string target = Path.Combine(_directory, "segments", name);
        if (_durable) DurableFile.FlushFile(file.Path);
        FileHandles.Forget(file.Path);
        DurableFile.Move(file.Path, target);
        if (_durable) DurableFile.FlushDirectory(Path.GetDirectoryName(target)!);
        var published = new FileBlob("segments/" + name, target, file.Length, writable: false);
        lock (_open)
        {
            _open.Remove(file);
            _open.Add(published);
        }
        return published;
    }

    public void Delete(IBlob blob)
    {
        var file = (FileBlob)blob;
        file.CloseWriter();
        FileHandles.Forget(file.Path);
        try
        {
            File.Delete(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // un fichier qui reste n'est qu'un orphelin, que le ménage du lot 5 supprimera
        }
        lock (_open) _open.Remove(file);
    }

    public void Close()
    {
        List<FileBlob> open;
        lock (_open)
        {
            open = [.. _open];
            _open.Clear();
        }
        foreach (var file in open)
        {
            file.CloseWriter();
            FileHandles.Forget(file.Path);
        }
        FileHandles.Forget(((FileBlob)Source).Path);
    }

    private sealed class FileBlob(string name, string path, long length, bool writable) : IBlob
    {
        private SafeFileHandle? _writer;
        private long _length = length;

        public string Name { get; } = name;
        public string Path { get; } = path;
        public long Length => Volatile.Read(ref _length);

        public void CreateEmpty() =>
            // un temporaire du même nom, laissé par une session interrompue, est écrasé
            _writer = File.OpenHandle(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);

        public void Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset + destination.Length > Length)
                throw new IOException($"Lecture hors de {Name} : [{offset}, {offset + destination.Length}) pour {Length} octets.");
            var writer = _writer;
            if (writer is not null)
            {
                ReadExactly(writer, offset, destination);
                return;
            }
            using var lease = FileHandles.Rent(Path);
            ReadExactly(lease.Handle, offset, destination);
        }

        public void Write(long offset, ReadOnlySpan<byte> bytes)
        {
            if (!writable || _writer is null) throw new InvalidOperationException($"{Name} est en lecture seule.");
            RandomAccess.Write(_writer, bytes, offset);
            long end = offset + bytes.Length;
            if (end > _length) Volatile.Write(ref _length, end);
        }

        public void Flush()
        {
            if (_writer is not null) RandomAccess.FlushToDisk(_writer);
            else DurableFile.FlushFile(Path);
        }

        public void CloseWriter()
        {
            _writer?.Dispose();
            _writer = null;
        }

        private void ReadExactly(SafeFileHandle handle, long offset, Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                int n = RandomAccess.Read(handle, destination, offset);
                if (n <= 0) throw new EndOfStreamException($"{Name} est plus court que prévu : fin à {offset}.");
                destination = destination[n..];
                offset += n;
            }
        }
    }
}

/// <summary>
/// La réserve de descripteurs du processus (spec §6.1, risque R3) : un
/// descripteur en lecture par fichier, gardé ouvert entre deux lectures, 128
/// au plus, en fermant d'abord les moins récemment lus. macOS en accorde 256
/// par défaut à une app lancée depuis le Finder.
/// </summary>
internal static class FileHandles
{
    internal static int Limit { get; set; } = 128;

    private static readonly Dictionary<string, Entry> Entries = new();
    private static readonly LinkedList<string> Recent = new();

    /// <summary>Nombre de descripteurs ouverts, pour les tests.</summary>
    internal static int OpenCount
    {
        get
        {
            lock (Entries) return Entries.Count;
        }
    }

    public static Lease Rent(string path)
    {
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(path, out entry!))
            {
                entry = new Entry(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                entry.Node = Recent.AddFirst(path);
                Entries[path] = entry;
                Trim();
            }
            else
            {
                Recent.Remove(entry.Node!);
                Recent.AddFirst(entry.Node!);
            }
            entry.Leases++;
        }
        return new Lease(path, entry);
    }

    /// <summary>Ferme le descripteur d'un fichier qui va être renommé ou supprimé, dès qu'il n'est plus lu.</summary>
    public static void Forget(string path)
    {
        lock (Entries)
        {
            if (!Entries.TryGetValue(path, out var entry)) return;
            Entries.Remove(path);
            Recent.Remove(entry.Node!);
            entry.Forgotten = true;
            if (entry.Leases == 0) entry.Handle.Dispose();
        }
    }

    private static void Trim()
    {
        // les moins récemment lus d'abord ; un descripteur en cours de lecture attend la fin de sa lecture
        var node = Recent.Last;
        while (Entries.Count > Limit && node is not null)
        {
            var previous = node.Previous;
            var entry = Entries[node.Value];
            Entries.Remove(node.Value);
            Recent.Remove(node);
            entry.Forgotten = true;
            if (entry.Leases == 0) entry.Handle.Dispose();
            node = previous;
        }
    }

    private static void Release(Entry entry)
    {
        lock (Entries)
        {
            entry.Leases--;
            if (entry.Leases == 0 && entry.Forgotten) entry.Handle.Dispose();
        }
    }

    internal sealed class Entry(SafeFileHandle handle)
    {
        public SafeFileHandle Handle { get; } = handle;
        public LinkedListNode<string>? Node { get; set; }
        public int Leases { get; set; }
        public bool Forgotten { get; set; }
    }

    public readonly struct Lease(string path, Entry entry) : IDisposable
    {
        public string Path { get; } = path;
        public SafeFileHandle Handle => entry.Handle;
        public void Dispose() => Release(entry);
    }
}
