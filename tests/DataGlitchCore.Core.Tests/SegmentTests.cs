namespace DataGlitchCore.Core.Tests;

/// <summary>Le format d'un segment (spec §5.1, §13.2) : écriture en flux, relecture par l'index, pied.</summary>
public sealed class SegmentTests
{
    /// <summary>Un fichier en mémoire qui compte ce qu'on y lit.</summary>
    private sealed class CountingBlob(IBlob inner) : IBlob
    {
        public long BytesRead;
        public int Reads;
        public string Name => inner.Name;
        public long Length => inner.Length;

        public void Read(long offset, Span<byte> destination)
        {
            Reads++;
            BytesRead += destination.Length;
            inner.Read(offset, destination);
        }

        public void Write(long offset, ReadOnlySpan<byte> bytes) => inner.Write(offset, bytes);
        public void Flush() => inner.Flush();
    }

    private static (IBlob Blob, List<(long Offset, byte[] Bytes)> Runs) Write(Random rng, int bufferBytes, int shift, int runs, int maxLength, int maxGap, int minLength = 1)
    {
        var blob = new MemoryBlobStore([]).CreateTemp("s.seg");
        var writer = new SegmentWriter(blob, shift, bufferBytes);
        var written = new List<(long, byte[])>();
        long at = rng.Next(0, 100);
        for (int i = 0; i < runs; i++)
        {
            var bytes = new byte[rng.Next(minLength, maxLength + 1)];
            rng.NextBytes(bytes);
            writer.StartRun(at);
            // en plusieurs morceaux, comme le moteur qui fusionne des blocs
            for (int done = 0; done < bytes.Length;)
            {
                int n = Math.Min(bytes.Length - done, rng.Next(1, 40));
                writer.Append(bytes.AsSpan(done, n));
                done += n;
            }
            writer.AddDelta(at >> shift, 1);
            written.Add((at, bytes));
            at += bytes.Length + rng.Next(0, maxGap + 1);
        }
        writer.Finish();
        return (blob, written);
    }

    [Theory]
    [InlineData(76, 200, 1, 70)]
    // des plages de plus de 255 octets : leur longueur a un deuxième octet non nul, que le
    // vidage d'un tampon pouvait écraser quand le champ tombait à cheval sur le vidage
    [InlineData(300, 600, 256, 290)]
    public void Runs_Survive_Every_Flush_Boundary(int smallest, int largest, int minLength, int maxLength)
    {
        // des tampons minuscules : un en-tête de plage finit par tomber à cheval sur chaque vidage
        var rng = new Random(1);
        for (int bufferBytes = smallest; bufferBytes <= largest; bufferBytes++)
        {
            var (blob, written) = Write(rng, bufferBytes, shift: 6, runs: 60, maxLength: maxLength, maxGap: 30, minLength: minLength);
            var segment = Segment.Open(blob);

            Assert.Equal(written.Select(w => new ByteRange(w.Offset, w.Offset + w.Bytes.Length)), segment.Runs());
            Assert.Equal(written.Count, segment.RunCount);
            Assert.Equal(written.Sum(w => (long)w.Bytes.Length), segment.DataBytes);

            long end = written[^1].Offset + written[^1].Bytes.Length + 50;
            var expected = new byte[end];
            foreach (var (offset, bytes) in written) bytes.CopyTo(expected, offset);
            for (int k = 0; k < 20; k++)
            {
                int from = rng.Next(0, (int)end), length = rng.Next(0, (int)end - from + 1);
                var actual = new byte[length];
                segment.CopyInto(from, actual);
                Assert.True(expected.AsSpan(from, length).SequenceEqual(actual), $"tampon de {bufferBytes} o, lecture [{from}, {from + length})");
            }
        }
    }

    [Fact]
    public void An_Untouched_Range_Is_Skipped_Without_Reading()
    {
        var rng = new Random(2);
        var blob = new MemoryBlobStore([]).CreateTemp("s.seg");
        var writer = new SegmentWriter(blob, chunkShift: 6, bufferBytes: 4096);
        writer.StartRun(10);
        writer.Append(new byte[5]);
        writer.StartRun(1000);
        writer.Append(new byte[5]);
        writer.Finish();
        var counting = new CountingBlob(blob);
        var segment = Segment.Open(counting);
        counting.Reads = 0;

        // tranches 2 à 14 : aucune plage ne les touche
        segment.CopyInto(128, new byte[700]);
        Assert.Equal(0, counting.Reads);

        segment.CopyInto(0, new byte[64]);
        Assert.True(counting.Reads > 0);
    }

    [Fact]
    public void A_Long_Run_Is_Read_Only_Where_Asked()
    {
        var blob = new MemoryBlobStore([]).CreateTemp("s.seg");
        var writer = new SegmentWriter(blob, chunkShift: 10, bufferBytes: 4096);
        var bytes = Enumerable.Range(0, 1 << 20).Select(i => (byte)(i * 7)).ToArray();
        writer.StartRun(0);
        writer.Append(bytes);
        writer.Finish();
        var counting = new CountingBlob(blob);
        var segment = Segment.Open(counting);
        counting.BytesRead = 0;

        var part = new byte[100];
        segment.CopyInto(900_000, part);

        Assert.Equal(bytes.AsSpan(900_000, 100).ToArray(), part);
        Assert.True(counting.BytesRead < 64 * 1024, $"{counting.BytesRead} octets lus pour 100 demandés");
    }

    [Fact]
    public void Counters_And_Index_Come_Back_From_Disk()
    {
        var blob = new MemoryBlobStore([]).CreateTemp("s.seg");
        var writer = new SegmentWriter(blob, chunkShift: 4, bufferBytes: 4096);
        writer.StartRun(20);              // tranche 1
        writer.Append(new byte[30]);      // jusqu'à 50 : tranches 1 à 3
        writer.AddDelta(1, 5);
        writer.AddDelta(3, -2);
        writer.StartRun(100);             // tranche 6
        writer.Append(new byte[2]);
        writer.AddDelta(6, 1);
        writer.Finish();

        var segment = Segment.Open(blob);
        Assert.Equal((1L, 6L), (segment.FirstChunk, segment.LastChunk));
        Assert.Equal([5, 0, -2, 0, 0, 1], segment.Diffs);
        Assert.Equal((20L, 102L), (segment.FirstOffset, segment.LastOffset));
    }

    [Fact]
    public void An_Empty_Segment_Is_Valid()
    {
        var blob = new MemoryBlobStore([]).CreateTemp("s.seg");
        new SegmentWriter(blob, 20, 4096).Finish();
        var segment = Segment.Open(blob);

        Assert.Equal(0, segment.RunCount);
        Assert.Empty(segment.Runs());
        var data = new byte[] { 1, 2, 3 };
        segment.CopyInto(0, data);
        Assert.Equal(new byte[] { 1, 2, 3 }, data);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(40)]
    public void A_Truncated_Segment_Is_Refused(int cut)
    {
        var (blob, _) = Write(new Random(3), 4096, 6, runs: 5, maxLength: 20, maxGap: 5);
        var bytes = new byte[blob.Length - cut];
        blob.Read(0, bytes);
        var truncated = new MemoryBlobStore([]).CreateTemp("t.seg");
        truncated.Write(0, bytes);

        Assert.Throws<InvalidDataException>(() => Segment.Open(truncated));
    }

    [Fact]
    public void Runs_Must_Arrive_Sorted()
    {
        var writer = new SegmentWriter(new MemoryBlobStore([]).CreateTemp("s.seg"), 20, 4096);
        writer.StartRun(100);
        writer.Append(new byte[10]);
        Assert.Throws<InvalidOperationException>(() => writer.StartRun(105));
    }
}
