using System.Buffers;
using System.Buffers.Binary;

namespace DataGlitchCore.Core;

// Le format d'un segment (spec dataglitchcore-buffer, §5.1) : une passe sur
// disque, ses seules plages écrites, avec leurs nouveaux octets.
//
//   en-tête  64 o  "DGCS" · version u16 · drapeaux u16 · id i64 · runCount i64
//                  dataBytes i64 · firstOffset i64 · lastOffset i64 · tailOffset i64
//                  tranche (log2) u8 · réservé
//   plages         [offset i64][longueur i32][octets] …   triées, disjointes
//   queue          firstChunk i64 · lastChunk i64
//                  index : une position i64 par tranche de [firstChunk, lastChunk]
//                  différences : un delta i32 par tranche de [firstChunk, lastChunk]
//   pied     12 o  longueur totale i64 · "DGCS"
//
// La tranche vaut 1 Mio, réglable en interne pour les tests. L'index donne,
// pour chaque tranche, la position de la première plage qui la touche ou qui
// la suit ; complémentée (~position) si aucune plage ne touche la tranche, ce
// qui permet à une lecture de passer un segment sans aucun accès disque.

internal static class SegmentFormat
{
    public const int HeaderSize = 64;
    public const int RunHeaderSize = 12;
    public const int FooterSize = 12;
    public const ushort Version = 1;
    public static ReadOnlySpan<byte> Magic => "DGCS"u8;

    /// <summary>Une plage ne dépasse pas 1 Gio : sa longueur tient sur 32 bits.</summary>
    public const int MaxRunLength = 1 << 30;

    /// <summary>Taille des lectures en avance d'un curseur sur les plages.</summary>
    public const int CursorBytes = 4 << 20;
}

/// <summary>
/// Écrit un segment en flux, plages triées d'abord, puis la queue. Les plages
/// restent en mémoire jusqu'au plafond d'une passe (D10, 32 Mio) ; au-delà, le
/// tampon se vide dans le fichier de la passe, puis se remplit de nouveau.
/// </summary>
internal sealed class SegmentWriter
{
    private readonly IBlob _blob;
    private readonly int _shift;
    private byte[] _buffer;
    private int _count;
    private long _flushed;

    private bool _inRun;
    private long _runStart;
    private long _runLength;
    private long _runHeaderAt;
    private long _lastEnd = long.MinValue;

    private long _firstChunk = -1;
    private long _nextChunk;
    private readonly List<long> _index = new();
    private int[] _diffs = new int[16];

    public SegmentWriter(IBlob blob, int chunkShift, int bufferBytes)
    {
        _blob = blob;
        _shift = chunkShift;
        _buffer = new byte[Math.Max(bufferBytes, SegmentFormat.HeaderSize + SegmentFormat.RunHeaderSize)];
        // l'en-tête, réécrit à la fin, une fois tout connu
        _count = SegmentFormat.HeaderSize;
    }

    public long RunCount { get; private set; }
    public long DataBytes { get; private set; }
    public long FirstOffset { get; private set; }
    public long LastOffset { get; private set; }
    public bool InRun => _inRun;

    /// <summary>Fin de la dernière plage, ou de celle en cours.</summary>
    public long End => _inRun ? _runStart + _runLength : _lastEnd;

    public void StartRun(long offset)
    {
        if (_inRun) EndRun();
        if (offset < _lastEnd) throw new InvalidOperationException($"Plage à {offset} avant la fin de la précédente ({_lastEnd}) : les plages doivent arriver triées.");
        _inRun = true;
        _runStart = offset;
        _runLength = 0;
        _runHeaderAt = _flushed + _count;
        Span<byte> header = stackalloc byte[SegmentFormat.RunHeaderSize];
        BinaryPrimitives.WriteInt64LittleEndian(header, offset);
        Put(header);
        if (RunCount == 0) FirstOffset = offset;
    }

    /// <summary>Ajoute des octets à la plage en cours.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (!_inRun) throw new InvalidOperationException("Aucune plage en cours.");
        while (!bytes.IsEmpty)
        {
            if (_runLength == SegmentFormat.MaxRunLength)
            {
                // au-delà d'1 Gio, la plage continue dans une plage contiguë
                long next = _runStart + _runLength;
                EndRun();
                StartRun(next);
            }
            int n = (int)Math.Min(bytes.Length, SegmentFormat.MaxRunLength - _runLength);
            Put(bytes[..n]);
            _runLength += n;
            bytes = bytes[n..];
        }
    }

    public void EndRun()
    {
        if (!_inRun) return;
        _inRun = false;
        long end = _runStart + _runLength;
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, (int)_runLength);
        Patch(_runHeaderAt + 8, length);

        RunCount++;
        DataBytes += _runLength;
        LastOffset = end;
        _lastEnd = end;
        if (_runLength == 0) return;

        // l'index : chaque tranche reçoit la première plage qui la touche, ou qui la suit
        long first = _runStart >> _shift, last = (end - 1) >> _shift;
        if (_firstChunk < 0)
        {
            _firstChunk = first;
            _nextChunk = first;
        }
        for (long c = _nextChunk; c <= last; c++)
            _index.Add(c >= first ? _runHeaderAt : ~_runHeaderAt);
        _nextChunk = Math.Max(_nextChunk, last + 1);
    }

    /// <summary>Ajoute à une tranche les octets que la passe fait passer de « égal à l'original » à « différent », moins l'inverse.</summary>
    public void AddDelta(long chunk, int delta)
    {
        if (delta == 0) return;
        if (_firstChunk < 0)
        {
            // la plage qui porte ce delta est en cours : sa tranche de départ ouvre l'index
            _firstChunk = Math.Min(chunk, _runStart >> _shift);
            _nextChunk = _firstChunk;
        }
        long at = chunk - _firstChunk;
        if (at < 0) throw new InvalidOperationException("Delta avant la première tranche du segment.");
        if (at >= _diffs.Length) Array.Resize(ref _diffs, (int)Math.Max(at + 1, _diffs.Length * 2L));
        _diffs[at] += delta;
    }

    /// <summary>Écrit la queue, le pied et l'en-tête : le segment est complet, prêt à relire.</summary>
    public void Finish()
    {
        EndRun();
        Flush();
        long tail = _flushed;
        long chunks = _index.Count;
        long firstChunk = _firstChunk < 0 ? 0 : _firstChunk;
        long lastChunk = firstChunk + chunks - 1;

        var bytes = new byte[16 + chunks * 12 + SegmentFormat.FooterSize];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, firstChunk);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), lastChunk);
        for (int c = 0; c < chunks; c++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16 + c * 8), _index[c]);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan((int)(16 + chunks * 8 + c * 4)), c < _diffs.Length ? _diffs[c] : 0);
        }
        long total = tail + bytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - 12), total);
        SegmentFormat.Magic.CopyTo(bytes.AsSpan(bytes.Length - 4));
        _blob.Write(tail, bytes);

        var header = new byte[SegmentFormat.HeaderSize];
        SegmentFormat.Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), SegmentFormat.Version);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), RunCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), DataBytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), FirstOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), LastOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), tail);
        header[56] = (byte)_shift;
        _blob.Write(0, header);
        _buffer = [];
    }

    /// <summary>
    /// Réécrit des octets déjà écrits : dans le fichier pour ce qui y est déjà
    /// parti, dans le tampon pour le reste. Un champ à cheval sur les deux est
    /// coupé en deux, sinon le tampon, vidé plus tard, écraserait la correction.
    /// </summary>
    private void Patch(long position, ReadOnlySpan<byte> bytes)
    {
        int inFile = (int)Math.Clamp(_flushed - position, 0, bytes.Length);
        if (inFile > 0) _blob.Write(position, bytes[..inFile]);
        if (inFile < bytes.Length) bytes[inFile..].CopyTo(_buffer.AsSpan((int)(position + inFile - _flushed)));
    }

    private void Put(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            if (_count == _buffer.Length) Flush();
            int n = Math.Min(bytes.Length, _buffer.Length - _count);
            bytes[..n].CopyTo(_buffer.AsSpan(_count));
            _count += n;
            bytes = bytes[n..];
        }
    }

    private void Flush()
    {
        if (_count == 0) return;
        _blob.Write(_flushed, _buffer.AsSpan(0, _count));
        _flushed += _count;
        _count = 0;
    }
}

/// <summary>
/// Un segment relu : son en-tête, son index et ses compteurs en mémoire
/// (12 octets par tranche), ses plages sur disque, lues à la demande.
/// </summary>
internal sealed class Segment
{
    private readonly long[] _index;

    private Segment(IBlob blob, long runCount, long dataBytes, long firstOffset, long lastOffset, long tailOffset,
                    int shift, long firstChunk, long[] index, int[] diffs)
    {
        Blob = blob;
        RunCount = runCount;
        DataBytes = dataBytes;
        FirstOffset = firstOffset;
        LastOffset = lastOffset;
        TailOffset = tailOffset;
        Shift = shift;
        FirstChunk = firstChunk;
        _index = index;
        Diffs = diffs;
    }

    public IBlob Blob { get; private set; }
    public long RunCount { get; }
    public long DataBytes { get; }
    public long FirstOffset { get; }
    public long LastOffset { get; }
    public long TailOffset { get; }
    public int Shift { get; }
    public long FirstChunk { get; }
    public long LastChunk => FirstChunk + _index.Length - 1;

    /// <summary>Un delta par tranche de [<see cref="FirstChunk"/>, <see cref="LastChunk"/>].</summary>
    public int[] Diffs { get; }

    /// <summary>Le segment après son renommage dans <c>segments/</c>.</summary>
    public void MovedTo(IBlob blob) => Blob = blob;

    /// <summary>Relit un segment : en-tête, queue, pied. Un fichier tronqué ou qui n'est pas un segment est refusé.</summary>
    public static Segment Open(IBlob blob)
    {
        long length = blob.Length;
        if (length < SegmentFormat.HeaderSize + SegmentFormat.FooterSize)
            throw new InvalidDataException($"{blob.Name} : trop court pour un segment ({length} octets).");

        Span<byte> footer = stackalloc byte[SegmentFormat.FooterSize];
        blob.Read(length - SegmentFormat.FooterSize, footer);
        if (!footer[8..].SequenceEqual(SegmentFormat.Magic) || BinaryPrimitives.ReadInt64LittleEndian(footer) != length)
            throw new InvalidDataException($"{blob.Name} : pied absent ou longueur fausse, segment tronqué.");

        Span<byte> header = stackalloc byte[SegmentFormat.HeaderSize];
        blob.Read(0, header);
        if (!header[..4].SequenceEqual(SegmentFormat.Magic) || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != SegmentFormat.Version)
            throw new InvalidDataException($"{blob.Name} : en-tête de segment inconnu.");

        long runCount = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        long dataBytes = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        long firstOffset = BinaryPrimitives.ReadInt64LittleEndian(header[32..]);
        long lastOffset = BinaryPrimitives.ReadInt64LittleEndian(header[40..]);
        long tail = BinaryPrimitives.ReadInt64LittleEndian(header[48..]);
        int shift = header[56];
        if (tail < SegmentFormat.HeaderSize || tail + 16 + SegmentFormat.FooterSize > length)
            throw new InvalidDataException($"{blob.Name} : queue hors du fichier.");

        Span<byte> bounds = stackalloc byte[16];
        blob.Read(tail, bounds);
        long firstChunk = BinaryPrimitives.ReadInt64LittleEndian(bounds);
        long lastChunk = BinaryPrimitives.ReadInt64LittleEndian(bounds[8..]);
        long chunks = lastChunk - firstChunk + 1;
        if (chunks < 0 || tail + 16 + chunks * 12 + SegmentFormat.FooterSize != length)
            throw new InvalidDataException($"{blob.Name} : index de taille fausse.");

        var bytes = new byte[chunks * 12];
        blob.Read(tail + 16, bytes);
        var index = new long[chunks];
        var diffs = new int[chunks];
        for (int c = 0; c < chunks; c++)
        {
            index[c] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(c * 8));
            diffs[c] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)(chunks * 8 + c * 4)));
        }
        return new Segment(blob, runCount, dataBytes, firstOffset, lastOffset, tail, shift, firstChunk, index, diffs);
    }

    /// <summary>Le numéro de la passe, écrit dans l'en-tête à son application.</summary>
    public void WriteId(long id)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, id);
        Blob.Write(8, bytes);
    }

    /// <summary>
    /// Recopie dans <paramref name="destination"/> les octets que le segment
    /// écrit sur <c>[offset, offset + destination.Length)</c>, par-dessus ce
    /// qui s'y trouve. Rien n'est lu si aucune plage ne touche la plage.
    /// </summary>
    public void CopyInto(long offset, Span<byte> destination) => CopyCore(offset, destination, Span<bool>.Empty);

    /// <summary>
    /// Comme <see cref="CopyInto"/>, en marquant en plus, dans
    /// <paramref name="covered"/>, les octets que le segment écrit : la fusion
    /// en a besoin pour savoir où la base recouvre l'original (§7).
    /// </summary>
    public void Overlay(long offset, Span<byte> destination, Span<bool> covered)
    {
        if (covered.Length != destination.Length) throw new ArgumentException("Une marque par octet.", nameof(covered));
        CopyCore(offset, destination, covered);
    }

    private void CopyCore(long offset, Span<byte> destination, Span<bool> covered)
    {
        long a = offset, b = offset + destination.Length;
        if (destination.IsEmpty || RunCount == 0 || b <= FirstOffset || a >= LastOffset) return;

        long c0 = Math.Max(a >> Shift, FirstChunk), c1 = Math.Min((b - 1) >> Shift, LastChunk);
        bool touched = false;
        for (long c = c0; c <= c1 && !touched; c++) touched = _index[c - FirstChunk] >= 0;
        if (!touched) return;

        long start = _index[c0 - FirstChunk];
        if (start < 0) start = ~start;
        // une estimation de ce qu'il faut lire : jusqu'à la première plage de la tranche suivante
        long stop = c1 + 1 <= LastChunk ? _index[c1 + 1 - FirstChunk] : TailOffset;
        if (stop < 0) stop = ~stop;

        // La zone que l'index désigne (§6.1), lue par blocs de 64 Kio et parcourue
        // sans appel par plage : à 20 segments, une lecture complète en parcourt
        // 50 millions, et une petite lecture s'arrête dès qu'elle a dépassé sa fin,
        // au lieu de lire toute la tranche. Les octets d'une plage qui déborde du
        // bloc sont lus directement, à la seule place demandée.
        long zoneEnd = Math.Min(TailOffset, Math.Max(stop, start) + SegmentFormat.RunHeaderSize);
        var block = ArrayPool<byte>.Shared.Rent(ZoneBlockBytes);
        try
        {
            long position = start;
            // un premier bloc à la mesure de la demande : une petite lecture ne charge pas 64 Kio d'une longue plage
            int next = (int)Math.Clamp(2 * (b - a), 4096, ZoneBlockBytes);
            while (position + SegmentFormat.RunHeaderSize <= TailOffset)
            {
                // au-delà de la zone estimée, de petits blocs : il n'y reste en général qu'une plage
                int size = (int)Math.Min(position < zoneEnd ? next : SegmentFormat.RunHeaderSize * 8, TailOffset - position);
                next = ZoneBlockBytes;
                Blob.Read(position, block.AsSpan(0, size));
                int p = 0;
                while (p + SegmentFormat.RunHeaderSize <= size)
                {
                    long runStart = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(p));
                    int runLength = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(p + 8));
                    if (runStart >= b) return;
                    long runEnd = runStart + runLength;
                    int data = p + SegmentFormat.RunHeaderSize;
                    if (runEnd > a)
                    {
                        long from = Math.Max(a, runStart), to = Math.Min(b, runEnd);
                        int at = (int)(from - runStart);
                        var target = destination.Slice((int)(from - a), (int)(to - from));
                        if (data + at + target.Length <= size) block.AsSpan(data + at, target.Length).CopyTo(target);
                        else Blob.Read(position + data + at, target);
                        if (!covered.IsEmpty) covered.Slice((int)(from - a), target.Length).Fill(true);
                    }
                    p = data + runLength;
                }
                position += p;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    private const int ZoneBlockBytes = 64 * 1024;

    /// <summary>Une plage du segment touche-t-elle la tranche <paramref name="chunk"/> ? Répondu par l'index, sans accès disque.</summary>
    public bool Touches(long chunk) => chunk >= FirstChunk && chunk <= LastChunk && _index[chunk - FirstChunk] >= 0;

    /// <summary>Le delta de la tranche <paramref name="chunk"/>, 0 hors du segment.</summary>
    public int DiffAt(long chunk) => chunk >= FirstChunk && chunk <= LastChunk ? Diffs[chunk - FirstChunk] : 0;

    /// <summary>Les plages du segment, dans l'ordre, sans leurs octets.</summary>
    public IEnumerable<ByteRange> Runs()
    {
        if (RunCount == 0) yield break;
        var cursor = new RunCursor(Blob, SegmentFormat.HeaderSize, TailOffset, SegmentFormat.CursorBytes);
        try
        {
            while (cursor.Next(out long start, out int length, out _))
                yield return new ByteRange(start, start + length);
        }
        finally
        {
            cursor.Dispose();
        }
    }

    /// <summary>
    /// Avance de plage en plage dans un segment, en lisant d'avance par blocs :
    /// une lecture disque sert toutes les petites plages d'une tranche. Les
    /// octets d'une longue plage ne sont lus que là où on les demande.
    /// </summary>
    private sealed class RunCursor : IDisposable
    {
        private readonly IBlob _blob;
        private readonly long _end;
        private readonly int _size;
        private byte[]? _cache;
        private long _cacheStart;
        private int _cacheCount;
        private long _position;

        public RunCursor(IBlob blob, long start, long end, int size)
        {
            _blob = blob;
            _position = start;
            _end = end;
            _size = Math.Max(size, SegmentFormat.RunHeaderSize);
        }

        public bool Next(out long start, out int length, out long dataAt)
        {
            start = 0;
            length = 0;
            dataAt = 0;
            if (_position + SegmentFormat.RunHeaderSize > _end) return false;
            var header = Cached(_position, SegmentFormat.RunHeaderSize);
            start = BinaryPrimitives.ReadInt64LittleEndian(header);
            length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            dataAt = _position + SegmentFormat.RunHeaderSize;
            _position = dataAt + length;
            return true;
        }

        /// <summary>Des octets de plage : depuis ce qui est déjà lu, ou directement s'ils n'y sont pas.</summary>
        public void ReadData(long at, Span<byte> destination)
        {
            if (_cache is not null && at >= _cacheStart && at + destination.Length <= _cacheStart + _cacheCount)
            {
                _cache.AsSpan((int)(at - _cacheStart), destination.Length).CopyTo(destination);
                return;
            }
            _blob.Read(at, destination);
        }

        private ReadOnlySpan<byte> Cached(long at, int count)
        {
            if (_cache is null || at < _cacheStart || at + count > _cacheStart + _cacheCount)
            {
                _cache ??= ArrayPool<byte>.Shared.Rent(_size);
                int n = (int)Math.Min(_cache.Length, _end - at);
                _blob.Read(at, _cache.AsSpan(0, n));
                _cacheStart = at;
                _cacheCount = n;
            }
            return _cache.AsSpan((int)(at - _cacheStart), count);
        }

        public void Dispose()
        {
            if (_cache is null) return;
            ArrayPool<byte>.Shared.Return(_cache);
            _cache = null;
        }
    }
}
