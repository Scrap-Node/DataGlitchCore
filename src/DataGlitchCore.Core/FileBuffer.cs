namespace DataGlitchCore.Core;

/// <summary>
/// Charge un fichier entièrement en mémoire (dans la limite du cap RAM) et garde
/// deux copies : l'originale (immuable) pour la comparaison/reset, et la courante
/// que le moteur de destruction modifie. Les offsets sont des <see cref="int"/> :
/// le cap (200 Mo par défaut) reste très en dessous de 2 Go, donc l'indexation
/// tableau est sûre. Au-delà, il faudra passer en memory-map (prévu pour la vidéo).
/// </summary>
public sealed class FileBuffer
{
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    private readonly byte[] _original;
    private readonly byte[] _current;

    private FileBuffer(string fileName, byte[] data, bool truncated)
    {
        FileName = fileName;
        _original = data;
        _current = (byte[])data.Clone();
        Truncated = truncated;
    }

    public string FileName { get; }

    /// <summary>Le fichier dépassait le cap RAM et a été tronqué au chargement.</summary>
    public bool Truncated { get; }

    public int Length => _current.Length;

    /// <summary>Octets d'origine, jamais modifiés (base de la diff et du reset).</summary>
    public ReadOnlySpan<byte> Original => _original;

    /// <summary>Octets courants, tels que modifiés par les passes de destruction.</summary>
    public ReadOnlySpan<byte> Current => _current;

    public static FileBuffer FromBytes(string fileName, byte[] data) => new(fileName, data, truncated: false);

    public static FileBuffer Load(string path, long maxBytes = DefaultMaxBytes)
    {
        using var fs = File.OpenRead(path);
        return Load(Path.GetFileName(path), fs, maxBytes);
    }

    public static FileBuffer Load(string fileName, Stream stream, long maxBytes = DefaultMaxBytes)
    {
        var (data, truncated) = ReadCapped(stream, maxBytes);
        return new FileBuffer(fileName, data, truncated);
    }

    public static async Task<FileBuffer> LoadAsync(string fileName, Stream stream, long maxBytes = DefaultMaxBytes, CancellationToken ct = default)
    {
        var (data, truncated) = await ReadCappedAsync(stream, maxBytes, ct).ConfigureAwait(false);
        return new FileBuffer(fileName, data, truncated);
    }

    // --- accès bas niveau utilisés par le journal (apply/undo) ---

    internal byte[] Read(int offset, int length)
    {
        var slice = new byte[length];
        Array.Copy(_current, offset, slice, 0, length);
        return slice;
    }

    /// <summary>Lit dans l'état COURANT sans muter — utilisé par le moteur pour construire une passe.</summary>
    internal void ReadInto(int offset, Span<byte> destination) => _current.AsSpan(offset, destination.Length).CopyTo(destination);

    internal void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_current.AsSpan(offset, bytes.Length));

    public byte[] SnapshotCurrent() => (byte[])_current.Clone();

    public void ResetToOriginal() => Array.Copy(_original, _current, _current.Length);

    private static (byte[] data, bool truncated) ReadCapped(Stream stream, long maxBytes)
    {
        if (maxBytes <= 0 || maxBytes > int.MaxValue) maxBytes = Math.Min(DefaultMaxBytes, int.MaxValue);
        using var ms = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        bool truncated = false;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (total + read > maxBytes)
            {
                ms.Write(chunk, 0, (int)(maxBytes - total));
                truncated = true;
                break;
            }
            ms.Write(chunk, 0, read);
            total += read;
        }
        return (ms.ToArray(), truncated);
    }

    private static async Task<(byte[] data, bool truncated)> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        if (maxBytes <= 0 || maxBytes > int.MaxValue) maxBytes = Math.Min(DefaultMaxBytes, int.MaxValue);
        using var ms = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        bool truncated = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (total + read > maxBytes)
            {
                await ms.WriteAsync(chunk.AsMemory(0, (int)(maxBytes - total)), ct).ConfigureAwait(false);
                truncated = true;
                break;
            }
            await ms.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
        }
        return (ms.ToArray(), truncated);
    }
}
