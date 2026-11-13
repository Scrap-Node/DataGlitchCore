namespace DataGlitchCore.Core.Tests.Reference;

// ORACLE — ne pas modifier.
//
// DestructionEngine, DestructionMask, ByteRange et HeaderInfo de
// DataGlitchCore 0.2.0 (24c3d18), recopiés tels quels (spec
// dataglitchcore-buffer, §13.1) : mêmes fenêtres, même tirage, mêmes octets.
// DestructionOptions et DestructionOperator sont ceux du cœur : ce sont les
// entrées de la passe, et la spec ne les change pas.

internal readonly record struct ReferenceByteRange(int Start, int End)
{
    public int Length => End - Start;
}

internal sealed record ReferenceHeaderInfo(int PayloadStart);

internal sealed class ReferenceDestructionMask
{
    private ReferenceDestructionMask(IReadOnlyList<ReferenceByteRange> regions) => Regions = regions;

    public IReadOnlyList<ReferenceByteRange> Regions { get; }

    public static ReferenceDestructionMask WholeBuffer { get; } = FromPayloadStart(0);

    public static ReferenceDestructionMask FromPayloadStart(int payloadStart) =>
        new([new ReferenceByteRange(Math.Max(0, payloadStart), int.MaxValue)]);

    public static ReferenceDestructionMask FromRegions(IEnumerable<ReferenceByteRange> regions)
    {
        var sorted = regions.Where(r => r.End > r.Start).OrderBy(r => r.Start).ToList();
        var merged = new List<ReferenceByteRange>(sorted.Count);
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                var last = merged[^1];
                if (r.End > last.End) merged[^1] = new ReferenceByteRange(last.Start, r.End);
            }
            else
            {
                merged.Add(r);
            }
        }
        return new ReferenceDestructionMask(merged);
    }

    public IReadOnlyList<ReferenceByteRange> ClampedTo(int bufferLength)
    {
        var result = new List<ReferenceByteRange>(Regions.Count);
        foreach (var r in Regions)
        {
            int start = Math.Clamp(r.Start, 0, bufferLength);
            int end = Math.Clamp(r.End, 0, bufferLength);
            if (end > start) result.Add(new ReferenceByteRange(start, end));
        }
        return result;
    }

    public int TotalBytes
    {
        get
        {
            int total = 0;
            foreach (var r in Regions) total += r.Length;
            return total;
        }
    }
}

internal static class ReferenceDestructionEngine
{
    public static ReferenceDestructionBatch BuildBatch(ReferenceFileBuffer buffer, DestructionOptions options, ReferenceHeaderInfo? header = null)
    {
        var mask = options.ProtectHeader && header is not null
            ? ReferenceDestructionMask.FromPayloadStart(header.PayloadStart)
            : ReferenceDestructionMask.WholeBuffer;
        return BuildBatch(buffer, options, mask);
    }

    public static ReferenceDestructionBatch BuildBatch(ReferenceFileBuffer buffer, DestructionOptions options, ReferenceDestructionMask mask)
    {
        int length = buffer.Length;
        int blockSize = Math.Max(MinBlockSize(options.Operator), options.BlockSize);

        int start = (int)Math.Clamp(options.RegionStart * length, 0, length);
        int end = (int)Math.Clamp(options.RegionEnd * length, 0, length);
        if (end < start) (start, end) = (end, start);

        var windows = IntersectWindows(start, end, mask, length);
        if (windows.Count == 0) return new ReferenceDestructionBatch(DescribeLabel(options), []);

        var cumulative = new int[windows.Count + 1];
        for (int i = 0; i < windows.Count; i++) cumulative[i + 1] = cumulative[i] + windows[i].Length;
        var space = new PayloadSpace(windows, cumulative);

        var rng = options.Seed is int s ? new Random(s) : new Random();
        int maxShift = Math.Max(1, options.MaxShiftBlocks);
        List<ReferenceByteEdit> edits = new();

        for (int w = 0; w < windows.Count; w++)
        {
            var window = windows[w];
            int windowVirtualStart = cumulative[w];

            for (int off = window.Start; off < window.End; off += blockSize)
            {
                int len = Math.Min(blockSize, window.End - off);
                if (rng.NextDouble() >= options.Density) continue;

                int virtualOff = windowVirtualStart + (off - window.Start);
                var oldBytes = buffer.Read(off, len);
                var newBytes = Apply(buffer, options, rng, off, len, virtualOff, space, maxShift);

                if (newBytes.AsSpan().SequenceEqual(oldBytes)) continue;

                edits.Add(new ReferenceByteEdit(off, oldBytes, newBytes));
            }
        }

        return new ReferenceDestructionBatch(DescribeLabel(options), edits);
    }

    private static List<ReferenceByteRange> IntersectWindows(int start, int end, ReferenceDestructionMask mask, int length)
    {
        var windows = new List<ReferenceByteRange>();
        foreach (var region in mask.ClampedTo(length))
        {
            int lo = Math.Max(start, region.Start);
            int hi = Math.Min(end, region.End);
            if (hi > lo) windows.Add(new ReferenceByteRange(lo, hi));
        }
        return windows;
    }

    public static List<ReferenceByteEdit> ToRuns(int baseOffset, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int mergeGap = 64)
    {
        var runs = new List<ReferenceByteEdit>();
        int i = 0, n = before.Length;

        while (i < n)
        {
            while (i < n && before[i] == after[i]) i++;
            if (i >= n) break;

            int start = i, lastDiff = i, same = 0;
            while (i < n && same <= mergeGap)
            {
                if (before[i] == after[i]) same++;
                else { same = 0; lastDiff = i; }
                i++;
            }

            int len = lastDiff - start + 1;
            var oldSlice = before.Slice(start, len).ToArray();
            var newSlice = after.Slice(start, len).ToArray();
            runs.Add(new ReferenceByteEdit(baseOffset + start, oldSlice, newSlice));
        }

        return runs;
    }

    private const int DuplicateMotifLength = 4;

    public static int MinBlockSize(DestructionOperator op) => op switch
    {
        DestructionOperator.Reverse => 2,
        DestructionOperator.DuplicateBlock => DuplicateMotifLength + 1,
        _ => 1,
    };

    private static byte[] Apply(ReferenceFileBuffer buffer, DestructionOptions o, Random rng, int off, int len, int virtualOff, PayloadSpace space, int maxShift)
    {
        var dst = new byte[len];
        switch (o.Operator)
        {
            case DestructionOperator.Zero:
                break;

            case DestructionOperator.Fixed:
                Array.Fill(dst, o.FixedValue);
                break;

            case DestructionOperator.Xor:
            {
                buffer.ReadInto(off, dst);
                byte mask = o.XorMask;
                for (int i = 0; i < len; i++) dst[i] ^= mask;
                break;
            }

            case DestructionOperator.BitShift:
            {
                buffer.ReadInto(off, dst);
                int k = Math.Clamp(o.BitRotate, 1, 7);
                for (int i = 0; i < len; i++)
                {
                    byte b = dst[i];
                    dst[i] = (byte)((b << k) | (b >> (8 - k)));
                }
                break;
            }

            case DestructionOperator.Reverse:
            {
                var src = buffer.Read(off, len);
                for (int i = 0; i < len; i++) dst[i] = src[len - 1 - i];
                break;
            }

            case DestructionOperator.ShiftBlock:
            {
                int shift = rng.Next(1, maxShift + 1) * len;
                int virtualSrc = Math.Max(0, virtualOff - shift);
                space.ReadInto(buffer, virtualSrc, dst);
                break;
            }

            case DestructionOperator.DuplicateBlock:
            {
                var src = buffer.Read(off, len);
                int motif = Math.Min(DuplicateMotifLength, len);
                for (int i = 0; i < len; i++) dst[i] = src[i % motif];
                break;
            }
        }
        return dst;
    }

    private static string DescribeLabel(DestructionOptions o)
    {
        string seed = o.Seed is int s ? s.ToString() : "aléatoire";
        int block = Math.Max(MinBlockSize(o.Operator), o.BlockSize);
        return $"{o.Operator} · densité {o.Density:P0} · bloc {block}o · seed {seed}";
    }

    private readonly struct PayloadSpace(IReadOnlyList<ReferenceByteRange> windows, int[] cumulative)
    {
        public void ReadInto(ReferenceFileBuffer buffer, int virtualStart, Span<byte> dst)
        {
            int remaining = dst.Length;
            int v = virtualStart;
            int written = 0;
            while (remaining > 0)
            {
                int idx = LocateWindowIndex(v);
                int withinWindow = v - cumulative[idx];
                int available = windows[idx].Length - withinWindow;
                int take = Math.Min(remaining, available);

                buffer.ReadInto(windows[idx].Start + withinWindow, dst.Slice(written, take));

                written += take;
                v += take;
                remaining -= take;
            }
        }

        private int LocateWindowIndex(int virtualOffset)
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
}
