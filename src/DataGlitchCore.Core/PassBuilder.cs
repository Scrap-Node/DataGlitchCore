using System.Buffers;

namespace DataGlitchCore.Core;

/// <summary>
/// Une passe en construction : ses plages s'écrivent en flux dans un segment,
/// dans <c>tmp/</c>, sans rien changer à l'état courant. <see cref="Build"/>
/// la rend prête à appliquer ; la disposer sans l'avoir construite supprime
/// ses fichiers.
///
/// À la main, une passe se construit à partir d'un avant et d'un après, par
/// <see cref="WriteDiff"/>. <see cref="DestructionEngine"/> s'en sert aussi,
/// par des membres internes.
/// </summary>
public sealed class PassBuilder : IDisposable
{
    private readonly FileBuffer _buffer;
    private readonly IBlob _blob;
    private readonly SegmentWriter _writer;
    private readonly int _shift;

    // l'état de fusion de WriteDiff, gardé d'un appel à l'autre
    private bool _diffInRun;
    private long _diffRunStart;
    private long _diffLastDiff;
    private long _diffSame;
    private long _diffNext = -1;
    private byte[] _pendingSame = new byte[64];
    private int _pendingCount;

    private long _editCount;
    private long _bytesChanged;
    private long _first = -1;
    private long _last;
    private bool _built;
    private bool _disposed;

    internal PassBuilder(FileBuffer buffer, BufferState baseState, IBlob blob)
    {
        _buffer = buffer;
        BaseState = baseState;
        _blob = blob;
        _shift = buffer.Options.ChunkShift;
        _writer = new SegmentWriter(blob, _shift, buffer.Options.PassBufferBytes);
    }

    internal BufferState BaseState { get; }
    internal FileBuffer Buffer => _buffer;
    internal SegmentWriter Writer => _writer;

    /// <summary>L'état sur lequel la passe se construit.</summary>
    public ByteView Before => _buffer.ViewOf(BaseState.Layers);

    /// <summary>
    /// Écrit la différence entre <paramref name="before"/> et <paramref name="after"/>,
    /// à partir de <paramref name="offset"/>, en plages réellement modifiées. On
    /// l'appelle tranche par tranche, avec des offsets croissants : appelée sur
    /// des tranches contiguës, elle donne exactement les plages qu'aurait
    /// données une comparaison de toute l'étendue d'un coup (l'ancien
    /// <c>ToRuns</c>). Deux plages séparées par au plus
    /// <paramref name="mergeGap"/> octets inchangés n'en font qu'une.
    /// </summary>
    /// <param name="offset">Offset, dans le fichier, du premier octet de <paramref name="before"/>.</param>
    /// <param name="before">Les octets de l'état d'avant la passe (<see cref="Before"/>) sur la tranche.</param>
    /// <param name="after">Les octets voulus, de même longueur.</param>
    /// <param name="mergeGap">Écart toléré, en octets inchangés, à l'intérieur d'une plage.</param>
    public void WriteDiff(long offset, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int mergeGap = 64)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_built) throw new InvalidOperationException("La passe est déjà construite.");
        if (before.Length != after.Length) throw new ArgumentException("Avant et après doivent avoir la même longueur.", nameof(after));
        ArgumentOutOfRangeException.ThrowIfNegative(mergeGap);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + before.Length > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset), "La tranche déborde du fichier.");
        if (_diffNext >= 0 && offset < _diffNext) throw new ArgumentException("Les tranches doivent arriver dans l'ordre, sans se recouvrir.", nameof(offset));
        if (before.IsEmpty) return;

        // une tranche qui ne suit pas la précédente ferme la plage en cours : les octets entre les deux sont inconnus
        if (_diffNext >= 0 && offset != _diffNext) CloseDiffRun();
        _diffNext = offset + before.Length;

        var original = ArrayPool<byte>.Shared.Rent(before.Length);
        try
        {
            _buffer.Original.Read(offset, original.AsSpan(0, before.Length));
            for (int i = 0; i < before.Length; i++)
            {
                long position = offset + i;
                bool same = before[i] == after[i];
                if (!_diffInRun)
                {
                    if (same) continue;
                    _diffInRun = true;
                    _diffRunStart = position;
                    _diffSame = 0;
                    _pendingCount = 0;
                    _writer.StartRun(position);
                }

                if (same)
                {
                    // un octet inchangé n'entre dans la plage que si un octet modifié le suit avant la fin de l'écart
                    _diffSame++;
                    if (_diffSame > mergeGap)
                    {
                        CloseDiffRun();
                        continue;
                    }
                    if (_pendingCount == _pendingSame.Length) Array.Resize(ref _pendingSame, _pendingSame.Length * 2);
                    _pendingSame[_pendingCount++] = after[i];
                }
                else
                {
                    if (_pendingCount > 0) _writer.Append(_pendingSame.AsSpan(0, _pendingCount));
                    _pendingCount = 0;
                    _diffSame = 0;
                    _diffLastDiff = position;
                    _writer.Append(after.Slice(i, 1));
                    int delta = (after[i] != original[i] ? 1 : 0) - (before[i] != original[i] ? 1 : 0);
                    if (delta != 0) _writer.AddDelta(position >> _shift, delta);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(original);
        }
    }

    private void CloseDiffRun()
    {
        if (!_diffInRun) return;
        _diffInRun = false;
        _pendingCount = 0;
        _writer.EndRun();
        CountEdit(_diffRunStart, _diffLastDiff + 1);
    }

    /// <summary>Compte une écriture de la passe, telle que la voit l'historique.</summary>
    internal void CountEdit(long start, long end)
    {
        _editCount++;
        _bytesChanged += end - start;
        if (_first < 0 || start < _first) _first = start;
        if (end > _last) _last = end;
    }

    /// <summary>
    /// Termine la passe : le segment est complet, prêt à appliquer. Elle ne
    /// change toujours rien tant que le journal ne l'a pas appliquée.
    /// </summary>
    /// <param name="label">Le libellé de la passe dans l'historique.</param>
    public DestructionBatch Build(string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_built) throw new InvalidOperationException("La passe est déjà construite.");
        CloseDiffRun();
        try
        {
            _writer.Finish();
            var segment = Segment.Open(_blob);
            _built = true;
            return new DestructionBatch(_buffer, BaseState, segment, label, _editCount, _bytesChanged,
                                        _first < 0 ? 0 : _first, _last);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Sans <see cref="Build"/>, supprime ce que la passe a déjà écrit.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_built) _buffer.Store.Delete(_blob);
    }
}

/// <summary>
/// Une passe construite, pas encore appliquée, ou appliquée par le journal.
/// Ses plages sont dans un segment, sur disque. Disposer une passe non
/// appliquée supprime ses fichiers ; une passe appliquée appartient au journal.
/// </summary>
public sealed class DestructionBatch : IDisposable
{
    private readonly FileBuffer _buffer;
    private bool _disposed;

    internal DestructionBatch(FileBuffer buffer, BufferState baseState, Segment segment, string label,
                              long editCount, long bytesChanged, long firstOffset, long lastOffset)
    {
        _buffer = buffer;
        BaseState = baseState;
        Segment = segment;
        Label = label;
        EditCount = editCount;
        BytesChanged = bytesChanged;
        FirstOffset = firstOffset;
        LastOffset = lastOffset;
    }

    /// <summary>Numéro donné par le journal à l'application, croissant dans la session ; 0 avant.</summary>
    public long Id { get; internal set; }

    public string Label { get; }

    /// <summary>Nombre d'écritures : les blocs modifiés d'une passe du moteur, les plages d'un <see cref="PassBuilder.WriteDiff"/>.</summary>
    public long EditCount { get; }

    /// <summary>Total des octets de ces écritures.</summary>
    public long BytesChanged { get; }

    /// <summary>Offset du premier octet détruit — le « début de la destruction » ; 0 sans écriture.</summary>
    public long FirstOffset { get; }

    /// <summary>Offset juste après le dernier octet détruit ; 0 sans écriture.</summary>
    public long LastOffset { get; }

    /// <summary>L'état sur lequel la passe a été construite.</summary>
    public ByteView Before => _buffer.ViewOf(BaseState.Layers);

    /// <summary>Cet état, la passe appliquée.</summary>
    public ByteView After => _buffer.ViewOf([.. BaseState.Layers, Segment]);

    /// <summary>
    /// Les plages que la passe écrit, triées. Deux écritures d'une même fenêtre
    /// du masque, à moins de 12 octets l'une de l'autre, n'en font qu'une : les
    /// octets entre elles gardent leur valeur d'avant.
    /// </summary>
    public IEnumerable<ByteRange> Runs() => Segment.Runs();

    internal FileBuffer Buffer => _buffer;
    internal BufferState BaseState { get; }
    internal Segment Segment { get; }
    internal bool Applied { get; set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!Applied) _buffer.Store.Delete(Segment.Blob);
    }

    internal bool Disposed => _disposed;
}
