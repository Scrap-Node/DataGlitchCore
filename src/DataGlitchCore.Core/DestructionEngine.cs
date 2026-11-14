namespace DataGlitchCore.Core;

/// <summary>
/// Construit une passe de destruction (<see cref="DestructionBatch"/>) à partir
/// d'un <see cref="FileBuffer"/> et de <see cref="DestructionOptions"/>. Ne
/// change rien à l'état courant : la passe s'écrit dans un segment, que le
/// <see cref="OperationJournal"/> applique ensuite. Avec une seed fixée, la
/// même config produit exactement le même rendu, octet pour octet, que les
/// versions d'avant le buffer sur disque (spec dataglitchcore-buffer, §13.1).
///
/// Entièrement agnostique au format du fichier — aucune notion de PCM, de trame
/// vidéo ou de tout autre domaine. Un projet qui connaît son format (WAV, AVI…)
/// construit le <see cref="DestructionMask"/> qu'il lui passe, et peut affiner le
/// résultat après coup (par ex. confiner la dégradation à une bande de fréquences)
/// en relisant <see cref="DestructionBatch.Before"/> et <see cref="DestructionBatch.After"/>,
/// puis en écrivant le résultat par <see cref="PassBuilder.WriteDiff"/>.
/// </summary>
public static class DestructionEngine
{
    /// <summary>
    /// Surcharge historique : un plancher unique (<see cref="HeaderInfo.PayloadStart"/>),
    /// adaptée en un <see cref="DestructionMask"/> équivalent.
    /// </summary>
    public static DestructionBatch BuildBatch(FileBuffer buffer, DestructionOptions options, HeaderInfo? header = null)
    {
        var mask = options.ProtectHeader && header is not null
            ? DestructionMask.FromPayloadStart(header.PayloadStart)
            : DestructionMask.WholeBuffer;
        return BuildBatch(buffer, options, mask);
    }

    /// <summary>
    /// Construit une passe dans les limites d'un <see cref="DestructionMask"/> :
    /// la zone ciblée par <see cref="DestructionOptions.RegionStart"/>/<see cref="DestructionOptions.RegionEnd"/>
    /// est intersectée avec les régions du masque, ce qui donne une liste de
    /// fenêtres disjointes — la boucle de blocs tourne à l'intérieur de
    /// chacune, jamais en dehors.
    ///
    /// L'état d'avant la passe se lit par fenêtres (1 Mio) ; l'écho de
    /// <see cref="DestructionOperator.ShiftBlock"/> est servi par la fenêtre
    /// courante ou la précédente, sinon par une lecture directe. Deux blocs
    /// modifiés d'une même fenêtre du masque, à moins de 12 octets l'un de
    /// l'autre, partagent une plage du segment.
    /// </summary>
    public static DestructionBatch BuildBatch(FileBuffer buffer, DestructionOptions options, DestructionMask mask)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mask);

        long length = buffer.Length;
        int blockSize = Math.Max(MinBlockSize(options.Operator), options.BlockSize);
        var windows = Windows(buffer, options, mask);

        // la place d'abord (§6.2) : une passe au-delà du budget est refusée avant d'écrire quoi que ce soit
        buffer.Journal?.EnsureRoom(Estimate(windows, options, buffer.Options.ChunkShift));

        using var builder = buffer.BeginPass();
        if (windows.Count == 0) return builder.Build(DescribeLabel(options));

        // Espace-payload : les fenêtres mises bout à bout comme un buffer
        // virtuel contigu, pour que ShiftBlock (l'écho) ne puisse jamais lire
        // un octet protégé situé dans un trou entre deux fenêtres.
        var cumulative = new long[windows.Count + 1];
        for (int i = 0; i < windows.Count; i++) cumulative[i + 1] = cumulative[i] + windows[i].Length;
        var space = new PayloadSpace(windows, cumulative);

        var rng = options.Seed is int s ? new Random(s) : new Random();
        int maxShift = Math.Max(1, options.MaxShiftBlocks);
        using var reader = new PassReader(buffer, builder.BaseState, Math.Max(buffer.Options.ReadWindowBytes, blockSize));
        var writer = builder.Writer;
        int shift = buffer.Options.ChunkShift;

        var oldBytes = new byte[blockSize];
        var newBytes = new byte[blockSize];
        var original = new byte[blockSize];
        var gap = new byte[SegmentFormat.RunHeaderSize];
        long previousEnd = 0;
        int previousWindow = -1;

        for (int w = 0; w < windows.Count; w++)
        {
            var window = windows[w];
            long windowVirtualStart = cumulative[w];

            for (long off = window.Start; off < window.End; off += blockSize)
            {
                int len = (int)Math.Min(blockSize, window.End - off);
                if (rng.NextDouble() >= options.Density) continue;

                long virtualOff = windowVirtualStart + (off - window.Start);
                var old = oldBytes.AsSpan(0, len);
                var orig = original.AsSpan(0, len);
                var dst = newBytes.AsSpan(0, len);
                reader.Block(off, old, orig);
                Apply(reader, options, rng, len, virtualOff, space, maxShift, old, dst);

                // Fenêtres par frame ⇒ beaucoup de blocs courts où l'opérateur
                // ne change en fait rien : sans ce garde-fou, une écriture sans
                // le moindre changement gonflerait BytesChanged et la diff.
                if (dst.SequenceEqual(old)) continue;

                long gapLength = off - previousEnd;
                if (previousWindow == w && writer.InRun && gapLength < SegmentFormat.RunHeaderSize)
                {
                    // les octets de l'écart gardent leur valeur d'avant la passe
                    var between = gap.AsSpan(0, (int)gapLength);
                    reader.Before(previousEnd, between);
                    writer.Append(between);
                }
                else
                {
                    writer.StartRun(off);
                }
                writer.Append(dst);
                builder.CountEdit(off, off + len);
                AddDeltas(writer, shift, off, old, dst, orig);
                previousEnd = off + len;
                previousWindow = w;
            }
        }

        return builder.Build(DescribeLabel(options));
    }

    /// <summary>
    /// Les compteurs du segment : par tranche, les octets que le bloc fait
    /// passer de « égal à l'original » à « différent », moins l'inverse.
    /// </summary>
    private static void AddDeltas(SegmentWriter writer, int shift, long offset, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, ReadOnlySpan<byte> original)
    {
        int done = 0;
        while (done < after.Length)
        {
            long position = offset + done;
            long chunk = position >> shift;
            int n = (int)Math.Min(after.Length - done, ((chunk + 1) << shift) - position);
            long delta = DiffMap.CountDifferences(after.Slice(done, n), original.Slice(done, n))
                       - DiffMap.CountDifferences(before.Slice(done, n), original.Slice(done, n));
            writer.AddDelta(chunk, (int)delta);
            done += n;
        }
    }

    /// <summary>
    /// Estime prudemment la taille du segment d'une passe, avant de la
    /// construire (spec §6.2) : densité × (1 + 12 / bloc) × octets de la zone,
    /// plafonné à deux fois la zone, plus l'en-tête, l'index et le pied. Une
    /// passe confinée à une bande écrit deux segments : l'app compte le double.
    /// </summary>
    public static long EstimateSegmentBytes(FileBuffer buffer, DestructionOptions options, DestructionMask mask)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mask);
        return Estimate(Windows(buffer, options, mask), options, buffer.Options.ChunkShift);
    }

    private static long Estimate(List<ByteRange> windows, DestructionOptions options, int shift)
    {
        long fixedBytes = SegmentFormat.HeaderSize + 16 + SegmentFormat.FooterSize;
        if (windows.Count == 0) return fixedBytes;

        long zone = windows.Sum(w => w.Length);
        int block = Math.Max(MinBlockSize(options.Operator), options.BlockSize);
        double density = Math.Clamp(options.Density, 0, 1);
        double data = Math.Min(density * (1 + (double)SegmentFormat.RunHeaderSize / block) * zone, 2.0 * zone);
        long chunks = ((windows[^1].End - 1) >> shift) - (windows[0].Start >> shift) + 1;
        return fixedBytes + 12 * chunks + (long)Math.Ceiling(data);
    }

    /// <summary>La zone ciblée, intersectée avec les régions du masque : des fenêtres disjointes, triées.</summary>
    private static List<ByteRange> Windows(FileBuffer buffer, DestructionOptions options, DestructionMask mask)
    {
        // zone ciblée, en octets — le masque, pas ce plancher, protège
        long length = buffer.Length;
        long start = (long)Math.Clamp(options.RegionStart * length, 0, length);
        long end = (long)Math.Clamp(options.RegionEnd * length, 0, length);
        if (end < start) (start, end) = (end, start);
        return IntersectWindows(start, end, mask, length);
    }

    private static List<ByteRange> IntersectWindows(long start, long end, DestructionMask mask, long length)
    {
        var windows = new List<ByteRange>();
        foreach (var region in mask.ClampedTo(length))
        {
            long lo = Math.Max(start, region.Start);
            long hi = Math.Min(end, region.End);
            if (hi > lo) windows.Add(new ByteRange(lo, hi));
        }
        return windows;
    }

    /// <summary>Longueur du motif que <see cref="DestructionOperator.DuplicateBlock"/>
    /// recopie sur tout le bloc.</summary>
    private const int DuplicateMotifLength = 4;

    /// <summary>
    /// Plus petit bloc sur lequel l'opérateur peut changer quoi que ce soit :
    /// inverser 1 octet, ou répéter un motif de 4 octets sur 4 octets ou moins,
    /// rend le bloc à l'identique. Le moteur relève
    /// <see cref="DestructionOptions.BlockSize"/> à ce minimum.
    /// </summary>
    public static int MinBlockSize(DestructionOperator op) => op switch
    {
        DestructionOperator.Reverse => 2,
        DestructionOperator.DuplicateBlock => DuplicateMotifLength + 1,
        _ => 1,
    };

    private static void Apply(PassReader reader, DestructionOptions o, Random rng, int len, long virtualOff,
                              PayloadSpace space, int maxShift, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        switch (o.Operator)
        {
            case DestructionOperator.Zero:
                dst.Clear();
                break;

            case DestructionOperator.Fixed:
                dst.Fill(o.FixedValue);
                break;

            case DestructionOperator.Xor:
            {
                byte mask = o.XorMask;
                for (int i = 0; i < len; i++) dst[i] = (byte)(src[i] ^ mask);
                break;
            }

            case DestructionOperator.BitShift:
            {
                int k = Math.Clamp(o.BitRotate, 1, 7);
                for (int i = 0; i < len; i++)
                {
                    byte b = src[i];
                    dst[i] = (byte)((b << k) | (b >> (8 - k)));
                }
                break;
            }

            case DestructionOperator.Reverse:
            {
                for (int i = 0; i < len; i++) dst[i] = src[len - 1 - i];
                break;
            }

            case DestructionOperator.ShiftBlock:
            {
                // L'écho travaille en espace-payload : "off - shift" ne peut
                // jamais tomber dans un trou protégé entre deux fenêtres. Le
                // calcul reste en int, comme avant : même tirage, mêmes octets.
                int shift = rng.Next(1, maxShift + 1) * len;
                long virtualSrc = Math.Max(0, virtualOff - shift);
                space.ReadInto(reader, virtualSrc, dst);
                break;
            }

            case DestructionOperator.DuplicateBlock:
            {
                int motif = Math.Min(DuplicateMotifLength, len);
                for (int i = 0; i < len; i++) dst[i] = src[i % motif];
                break;
            }
        }
    }

    private static string DescribeLabel(DestructionOptions o)
    {
        string seed = o.Seed is int s ? s.ToString() : "aléatoire";
        int block = Math.Max(MinBlockSize(o.Operator), o.BlockSize);
        return $"{o.Operator} · densité {o.Density:P0} · bloc {block}o · seed {seed}";
    }

    /// <summary>
    /// Vue "toutes les fenêtres mises bout à bout" d'un buffer troué par des
    /// régions protégées — l'espace dans lequel <see cref="DestructionOperator.ShiftBlock"/>
    /// calcule sa source d'écho. <paramref name="cumulative"/>[i] est le début,
    /// en espace virtuel, de la fenêtre i.
    /// </summary>
    private readonly struct PayloadSpace(IReadOnlyList<ByteRange> windows, long[] cumulative)
    {
        public void ReadInto(PassReader reader, long virtualStart, Span<byte> dst)
        {
            int remaining = dst.Length;
            long v = virtualStart;
            int written = 0;
            while (remaining > 0)
            {
                int idx = LocateWindowIndex(v);
                long withinWindow = v - cumulative[idx];
                long available = windows[idx].Length - withinWindow;
                int take = (int)Math.Min(remaining, available);

                reader.Before(windows[idx].Start + withinWindow, dst.Slice(written, take));

                written += take;
                v += take;
                remaining -= take;
            }
        }

        /// <summary>Plus grand indice <c>i</c> tel que <c>cumulative[i] &lt;= virtualOffset</c>.</summary>
        private int LocateWindowIndex(long virtualOffset)
        {
            int lo = 0, hi = windows.Count - 1, idx = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cumulative[mid] <= virtualOffset) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return idx;
        }
    }

    /// <summary>
    /// L'état d'avant la passe, lu par fenêtres qui avancent avec les blocs :
    /// la fenêtre courante et la précédente, et l'original aux mêmes offsets.
    /// Sans base dense, l'original est aussi le fond de l'état : il n'est lu
    /// qu'une fois pour les deux usages.
    /// </summary>
    private sealed class PassReader(FileBuffer buffer, BufferState state, int size) : IDisposable
    {
        private readonly IBlob _original = buffer.Store.Source;
        private readonly IBlob _bottom = state.Bottom;
        private readonly Segment[] _layers = state.Layers;
        private readonly long _length = buffer.Length;
        private Window _current = new(size);
        private Window _previous = new(size);

        /// <summary>Un bloc, et l'original au même endroit. La fenêtre avance jusqu'à lui s'il n'y est pas.</summary>
        public void Block(long offset, Span<byte> before, Span<byte> original)
        {
            if (!_current.Contains(offset, before.Length))
            {
                (_previous, _current) = (_current, _previous);
                _current.Load(_original, _bottom, _layers, offset, (int)Math.Min(size, _length - offset));
            }
            _current.Copy(offset, before, original);
        }

        /// <summary>Des octets de l'état d'avant : depuis une des deux fenêtres, sinon par une lecture directe.</summary>
        public void Before(long offset, Span<byte> destination)
        {
            if (_current.Contains(offset, destination.Length)) _current.Copy(offset, destination, default);
            else if (_previous.Contains(offset, destination.Length)) _previous.Copy(offset, destination, default);
            else LayeredSource.ReadLayers(_bottom, _layers, offset, destination);
        }

        public void Dispose()
        {
        }

        private sealed class Window(int size)
        {
            private readonly byte[] _before = new byte[size];
            private readonly byte[] _original = new byte[size];
            private long _start = -1;
            private int _count;

            public bool Contains(long offset, int length) =>
                _start >= 0 && offset >= _start && offset + length <= _start + _count;

            public void Load(IBlob original, IBlob bottom, Segment[] layers, long offset, int count)
            {
                var orig = _original.AsSpan(0, count);
                var before = _before.AsSpan(0, count);
                original.Read(offset, orig);
                if (ReferenceEquals(bottom, original)) orig.CopyTo(before);
                else bottom.Read(offset, before);
                foreach (var layer in layers) layer.CopyInto(offset, before);
                _start = offset;
                _count = count;
            }

            public void Copy(long offset, Span<byte> before, Span<byte> original)
            {
                int at = (int)(offset - _start);
                _before.AsSpan(at, before.Length).CopyTo(before);
                if (!original.IsEmpty) _original.AsSpan(at, original.Length).CopyTo(original);
            }
        }
    }
}
