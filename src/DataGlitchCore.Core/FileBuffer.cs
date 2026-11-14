namespace DataGlitchCore.Core;

/// <summary>
/// Un fichier à détruire, sans jamais le tenir en entier en mémoire
/// (spec dataglitchcore-buffer, §3). L'original reste là où l'application l'a
/// copié, en lecture seule. L'état courant n'existe pas en entier : c'est
/// l'original, plus une pile de segments, un par passe, écrits sur disque.
/// Une lecture le recompose fenêtre par fenêtre (<see cref="Current"/>).
///
/// Le processus ne garde que l'index et les compteurs de chaque segment
/// (12 octets par Mio), et la passe en construction, plafonnée à 32 Mio.
///
/// Les écritures (passes, annulations) ne sont pas concurrentes : l'appelant
/// les sérialise. Les lectures concurrentes sont sûres.
/// </summary>
public sealed class FileBuffer : IDisposable
{
    private readonly IBlobStore _store;
    private BufferState _state;
    private readonly int[] _changed;
    private long _builds;
    private bool _disposed;

    private FileBuffer(string fileName, IBlobStore store, FileBufferOptions options)
    {
        FileName = fileName;
        _store = store;
        Options = options;
        Length = store.Source.Length;
        _state = new BufferState(0, []);
        _changed = new int[Length == 0 ? 0 : ((Length - 1) >> options.ChunkShift) + 1];
        Original = new ByteView(new BlobSource(store.Source));
        Current = new ByteView(new CurrentSource(this));
    }

    /// <summary>
    /// Ouvre un fichier déjà copié par l'application (<see cref="SourceFile.CopyAsync"/>).
    /// Les segments s'écrivent dans <paramref name="workDirectory"/>, sous
    /// <c>segments/</c> et <c>tmp/</c>.
    /// </summary>
    /// <param name="sourcePath">L'original, qui ne sera jamais modifié.</param>
    /// <param name="workDirectory">Le dossier de la session.</param>
    public static FileBuffer Open(string sourcePath, string workDirectory) => Open(sourcePath, workDirectory, null);

    internal static FileBuffer Open(string sourcePath, string workDirectory, FileBufferOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        options ??= new FileBufferOptions();
        return new FileBuffer(Path.GetFileName(sourcePath), new FileBlobStore(sourcePath, workDirectory, options.Durable), options);
    }

    /// <summary>Tout en mémoire, original et segments : pour les tests et les petits fichiers construits à la main.</summary>
    public static FileBuffer FromBytes(string fileName, byte[] data) => FromBytes(fileName, data, null);

    internal static FileBuffer FromBytes(string fileName, byte[] data, FileBufferOptions? options)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new FileBuffer(fileName, new MemoryBlobStore(data), options ?? new FileBufferOptions());
    }

    public string FileName { get; }

    public long Length { get; }

    /// <summary>Les octets d'origine, lus par fenêtres. Ils ne changent jamais.</summary>
    public ByteView Original { get; }

    /// <summary>
    /// L'état courant, lu par fenêtres. La vue suit le journal : ce qu'une
    /// passe, une annulation ou un retour à l'original change s'y lit aussitôt.
    /// </summary>
    public ByteView Current { get; }

    /// <summary>L'ancien nom de <see cref="Original"/>.</summary>
    [Obsolete("Original est maintenant la vue elle-même.")]
    public ByteView OriginalView => Original;

    /// <summary>L'ancien nom de <see cref="Current"/>.</summary>
    [Obsolete("Current est maintenant la vue elle-même.")]
    public ByteView CurrentView => Current;

    internal FileBufferOptions Options { get; }
    internal IBlobStore Store => _store;
    internal BufferState State => Volatile.Read(ref _state);

    /// <summary>
    /// Commence une passe sur l'état courant. La passe s'écrit dans
    /// <c>tmp/</c> à mesure qu'on la construit ; elle ne change rien tant
    /// qu'elle n'est pas appliquée par <see cref="OperationJournal.Apply"/>.
    /// </summary>
    public PassBuilder BeginPass()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long build = Interlocked.Increment(ref _builds);
        return new PassBuilder(this, State, _store.CreateTemp($"{build:D8}.seg"));
    }

    /// <summary>
    /// Écrit l'état courant dans un flux, par morceaux d'1 Mio. Le buffer
    /// n'est pas verrouillé : à l'appelant d'empêcher toute passe pendant la copie.
    /// </summary>
    public Task WriteCurrentToAsync(Stream destination, CancellationToken ct = default) =>
        WriteCurrentToAsync(destination, 0, Length, ct);

    /// <summary>Écrit une plage de l'état courant dans un flux.</summary>
    public async Task WriteCurrentToAsync(Stream destination, long offset, long count, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count), "La plage dépasse la fin du buffer.");

        var chunk = new byte[(int)Math.Min(WriteChunkSize, Math.Max(count, 1))];
        for (long done = 0; done < count;)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk.Length, count - done);
            Current.Read(offset + done, chunk.AsSpan(0, n));
            await destination.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
        }
    }

    internal const int WriteChunkSize = 1024 * 1024;

    /// <summary>Lâche les fichiers ouverts. Les segments restent sur disque.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Close();
    }

    // ---------- état, pour le journal ----------

    /// <summary>Passe à une nouvelle pile de segments : un nouvel état, que rien de construit avant ne peut plus viser.</summary>
    internal void SetLayers(Segment[] layers) =>
        Volatile.Write(ref _state, new BufferState(_state.Number + 1, layers));

    /// <summary>Ajoute (+1) ou retire (-1) les compteurs d'un segment.</summary>
    internal void AddDiffs(Segment segment, int sign)
    {
        for (int i = 0; i < segment.Diffs.Length; i++)
            _changed[segment.FirstChunk + i] += sign * segment.Diffs[i];
    }

    internal void ClearDiffs() => Array.Clear(_changed);

    /// <summary>Octets différents de l'original, tranche par tranche.</summary>
    internal ReadOnlySpan<int> ChangedPerChunk => _changed;

    /// <summary>Octets de <c>[start, start + length)</c> qui diffèrent de l'original : tranches entières par les compteurs, bords comparés.</summary>
    internal long ChangedBytes(long start, long length)
    {
        long end = Math.Clamp(start + Math.Max(0, length), 0, Length);
        start = Math.Clamp(start, 0, Length);
        if (end <= start) return 0;

        int shift = Options.ChunkShift;
        long chunk = 1L << shift;
        long firstFull = (start + chunk - 1) >> shift, lastFull = end >> shift; // tranches [firstFull, lastFull) entières
        if (firstFull >= lastFull) return Compare(start, end);

        long total = Compare(start, firstFull << shift) + Compare(lastFull << shift, end);
        for (long c = firstFull; c < lastFull; c++) total += _changed[c];
        return total;
    }

    private long Compare(long start, long end)
    {
        if (end <= start) return 0;
        return DiffMap.CountChanged(Original, Current, start, end - start);
    }

    /// <summary>L'original et une pile de segments, lus ensemble.</summary>
    internal ByteView ViewOf(Segment[] layers) => new(new LayeredSource(_store.Source, layers));

    private sealed class CurrentSource(FileBuffer buffer) : ByteSource
    {
        public override long Length => buffer.Length;

        public override void Read(long offset, Span<byte> destination) =>
            LayeredSource.ReadLayers(buffer._store.Source, buffer.State.Layers, offset, destination);
    }
}

/// <summary>Un état du buffer : son numéro, qui change à chaque passe, annulation ou retour, et sa pile de segments.</summary>
internal sealed record BufferState(long Number, Segment[] Layers);

/// <summary>
/// Les réglages internes d'un buffer. Les tests les réduisent pour forcer
/// tous les chemins (spec §13.1) ; l'application garde les défauts.
/// </summary>
internal sealed class FileBufferOptions
{
    /// <summary>Taille d'une tranche d'index et de compteurs, en puissance de deux : 1 Mio.</summary>
    public int ChunkShift { get; init; } = 20;

    /// <summary>Plafond d'une passe en construction avant de se vider sur disque (D10) : 32 Mio.</summary>
    public int PassBufferBytes { get; init; } = 32 << 20;

    /// <summary>Fenêtre de lecture de l'état d'avant une passe : 1 Mio.</summary>
    public int ReadWindowBytes { get; init; } = 1 << 20;

    /// <summary>En deçà de cette taille de case, la carte des différences compare les octets ; au-delà, elle lit les compteurs (§6.3).</summary>
    public long DirectDiffBucketBytes { get; init; } = 4L << 20;

    /// <summary>Forcer les segments sur le disque à leur application (§9.2). Les tests de l'oracle s'en passent, pour aller vite.</summary>
    public bool Durable { get; init; } = true;
}

/// <summary>Un fichier du buffer, lu tel quel.</summary>
internal sealed class BlobSource(IBlob blob) : ByteSource
{
    public override long Length => blob.Length;

    public override void Read(long offset, Span<byte> destination) => blob.Read(offset, destination);
}

/// <summary>
/// L'original, plus des segments par-dessus, du plus ancien au plus récent
/// (spec §6.1). Une lecture coûte 1 + K accès disque, K étant le nombre de
/// segments qui touchent la plage.
/// </summary>
internal sealed class LayeredSource(IBlob original, Segment[] layers) : ByteSource
{
    public override long Length => original.Length;

    public override void Read(long offset, Span<byte> destination) => ReadLayers(original, layers, offset, destination);

    public static void ReadLayers(IBlob original, Segment[] layers, long offset, Span<byte> destination)
    {
        original.Read(offset, destination);
        foreach (var layer in layers) layer.CopyInto(offset, destination);
    }
}
