using System.Buffers.Binary;
using System.Text;
using DataGlitchCore.Core.Tests.Reference;

namespace DataGlitchCore.Core.Tests;

public class ByteViewTests
{
    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7)).ToArray();

    // ---- lecture ----

    [Fact]
    public void Read_Copies_The_Requested_Range()
    {
        var buf = FileBuffer.FromBytes("x", Ramp(100));
        var part = new byte[10];

        buf.CurrentView.Read(42, part);

        Assert.Equal(Ramp(100)[42..52], part);
        Assert.Equal(100, buf.CurrentView.Length);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 11)]
    [InlineData(10, 1)]
    [InlineData(11, 0)]
    public void Read_Refuses_A_Range_Outside_The_View(long offset, int count)
    {
        var view = FileBuffer.FromBytes("x", new byte[10]).CurrentView;
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Read(offset, new byte[count]));
    }

    [Fact]
    public void Read_Of_Nothing_At_The_End_Is_Fine()
    {
        var view = FileBuffer.FromBytes("x", new byte[10]).CurrentView;
        view.Read(10, Span<byte>.Empty);
    }

    [Fact]
    public void CurrentView_Follows_Passes_Undo_And_Reset_While_OriginalView_Never_Moves()
    {
        var data = Ramp(64);
        var buf = FileBuffer.FromBytes("x", (byte[])data.Clone());
        var journal = new OperationJournal(buf);
        var view = buf.CurrentView;

        journal.Apply(new DestructionBatch("t", [new ByteEdit(3, [data[3]], [0xEE])]));
        Assert.Equal(0xEE, view.ReadAll()[3]);

        journal.Undo();
        Assert.Equal(data, view.ReadAll());

        journal.Redo();
        journal.ResetAll();
        Assert.Equal(data, view.ReadAll());
        Assert.Equal(data, buf.OriginalView.ReadAll());
    }

#pragma warning disable CS0618 // les membres obsolètes marchent jusqu'à leur retrait, en 0.4.0
    [Fact]
    public void Obsolete_Spans_Still_Give_The_Same_Bytes_As_The_Views()
    {
        var buf = FileBuffer.FromBytes("x", Ramp(32));
        new OperationJournal(buf).Apply(new DestructionBatch("t", [new ByteEdit(1, [7], [9])]));

        Assert.Equal(buf.OriginalView.ReadAll(), buf.Original.ToArray());
        Assert.Equal(buf.CurrentView.ReadAll(), buf.Current.ToArray());
        Assert.Equal(buf.CurrentView.ReadAll(), buf.SnapshotCurrent());
    }
#pragma warning restore CS0618

    // ---- flux ----

    [Fact]
    public async Task OpenRead_Gives_A_Read_Only_Seekable_Stream_Of_The_Whole_State()
    {
        var data = Ramp(3 * 1024 * 1024 + 17);
        using var stream = FileBuffer.FromBytes("x", data).CurrentView.OpenRead();

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(data.Length, stream.Length);

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        Assert.Equal(data, copy.ToArray());
        Assert.Equal(0, stream.Read(new byte[8], 0, 8)); // au bout : plus rien
    }

    [Fact]
    public void OpenRead_Seeks_From_Every_Origin()
    {
        var data = Ramp(100);
        using var stream = FileBuffer.FromBytes("x", data).CurrentView.OpenRead();
        var one = new byte[1];

        Assert.Equal(90, stream.Seek(-10, SeekOrigin.End));
        stream.ReadExactly(one);
        Assert.Equal(data[90], one[0]);

        Assert.Equal(81, stream.Seek(-10, SeekOrigin.Current));
        stream.ReadExactly(one);
        Assert.Equal(data[81], one[0]);

        Assert.Equal(5, stream.Seek(5, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-6, SeekOrigin.Current));

        stream.Position = 500; // au-delà de la fin : rien à lire, sans erreur
        Assert.Equal(0, stream.Read(one));
    }

    [Fact]
    public void OpenRead_Gives_Independent_Streams()
    {
        var view = FileBuffer.FromBytes("x", Ramp(10)).CurrentView;
        using var a = view.OpenRead();
        using var b = view.OpenRead();

        a.Position = 7;
        Assert.Equal(0, b.Position);
        Assert.Equal(Ramp(10)[0], (byte)b.ReadByte());
    }

    [Fact]
    public void OpenRead_Refuses_Writes_And_Reads_After_Dispose()
    {
        var stream = FileBuffer.FromBytes("x", Ramp(10)).CurrentView.OpenRead();
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(3));

        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
    }

    [Fact]
    public async Task OpenRead_Honours_A_Cancelled_Token()
    {
        using var stream = FileBuffer.FromBytes("x", Ramp(10)).CurrentView.OpenRead();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadAsync(new byte[4], new CancellationToken(canceled: true)).AsTask());
    }

    // ---- DiffMap ----

    [Fact]
    public void DiffMap_On_Views_Is_The_Same_As_On_The_Whole_File_Across_Windows()
    {
        // plus de trois fenêtres d'1 Mio, et des cases à cheval sur leurs bords
        var rng = new Random(5);
        var original = new byte[3 * 1024 * 1024 + 4099];
        rng.NextBytes(original);
        var buf = FileBuffer.FromBytes("x", (byte[])original.Clone());
        var journal = new OperationJournal(buf);
        journal.Apply(DestructionEngine.BuildBatch(buf, new DestructionOptions { Seed = 3, Density = 0.3, BlockSize = 4000 }));
        var current = buf.CurrentView.ReadAll();

        foreach (int buckets in new[] { 1, 3, 7, 1600, original.Length + 10 })
        {
            Assert.Equal(
                ReferenceDiffMap.Compute(original, current, buckets),
                DiffMap.Compute(buf.OriginalView, buf.CurrentView, buckets));
        }
        Assert.Equal(
            ReferenceDiffMap.Compute(original, current, 420, 1_048_570, 2_000_000),
            DiffMap.Compute(buf.OriginalView, buf.CurrentView, 420, 1_048_570, 2_000_000));
    }

    [Fact]
    public void CountDifferences_Counts_Every_Lane_And_The_Tail()
    {
        var rng = new Random(11);
        for (int n = 0; n < 200; n++)
        {
            var a = new byte[n];
            rng.NextBytes(a);
            var b = (byte[])a.Clone();
            int expected = 0;
            for (int i = 0; i < n; i++)
            {
                if (rng.Next(3) != 0) continue;
                b[i] ^= (byte)rng.Next(1, 256);
                expected++;
            }
            Assert.Equal(expected, DiffMap.CountDifferences(a, b));
        }
    }

    // ---- Riff ----

    [Fact]
    public void Riff_Walk_On_A_View_Reads_The_Same_Chunks_As_On_The_Span()
    {
        // assez de chunks pour traverser plusieurs fenêtres d'en-têtes
        var bytes = Avi(frames: 3000, rng: new Random(1));
        var view = FileBuffer.FromBytes("x.avi", bytes).CurrentView;

        var expected = ReferenceRiff.Walk(bytes, 0, bytes.Length);
        var actual = Riff.Walk(view, 0, view.Length);

        AssertSameChunks(expected, actual);
        Assert.Contains(actual, c => c.ListType == "movi");
    }

    [Theory]
    [MemberData(nameof(OracleTests.Seeds), MemberType = typeof(OracleTests))]
    public void Riff_Walk_On_A_View_Fails_Or_Succeeds_Like_The_Reference(int seed)
    {
        // un AVI abîmé au hasard : tailles faussées, coupé net, région mal bornée
        var rng = new Random(seed);
        var bytes = Avi(frames: rng.Next(0, 60), rng);
        for (int k = rng.Next(0, 4); k > 0 && bytes.Length > 8; k--)
        {
            int at = rng.Next(0, bytes.Length - 4);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)rng.Next());
        }
        if (rng.Next(3) == 0) bytes = bytes[..rng.Next(0, bytes.Length + 1)];
        int from = rng.Next(5) == 0 ? rng.Next(-4, 40) : 0;
        int to = rng.Next(5) == 0 ? rng.Next(0, bytes.Length + 40) : bytes.Length;
        bool tolerate = rng.Next(2) == 0;
        var view = FileBuffer.FromBytes("x.avi", bytes).CurrentView;

        IReadOnlyList<ReferenceRiffChunk>? expected = null;
        Exception? expectedError = null;
        try
        {
            expected = ReferenceRiff.Walk(bytes, from, to, tolerate);
        }
        catch (Exception ex)
        {
            expectedError = ex;
        }

        if (expectedError is null)
        {
            AssertSameChunks(expected!, Riff.Walk(view, from, to, tolerate));
            return;
        }
        var actualError = Assert.ThrowsAny<Exception>(() => Riff.Walk(view, from, to, tolerate));
        Assert.IsType(expectedError.GetType(), actualError);
        if (expectedError is RiffFormatException) Assert.Equal(expectedError.Message, actualError.Message);
    }

    [Fact]
    public void Riff_Walk_On_A_View_Refuses_Offsets_Beyond_32_Bits_Until_0_4()
    {
        var view = FileBuffer.FromBytes("x", new byte[16]).CurrentView;
        Assert.Throws<ArgumentOutOfRangeException>(() => Riff.Walk(view, 0, (long)int.MaxValue + 1));
    }

    private static void AssertSameChunks(IReadOnlyList<ReferenceRiffChunk> expected, IReadOnlyList<RiffChunk> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            Assert.Equal((e.Id, e.ListType, e.Start, e.Size), (a.Id, a.ListType, a.Start, a.Size));
        }
    }

    /// <summary>Un AVI minimal : hdrl, puis movi et ses frames de tailles variées (impaires comprises), puis idx1.</summary>
    private static byte[] Avi(int frames, Random rng)
    {
        var movi = new List<byte>();
        for (int i = 0; i < frames; i++)
        {
            var payload = new byte[rng.Next(0, i % 50 == 0 ? 40_000 : 300)];
            rng.NextBytes(payload);
            movi.AddRange(Chunk("00dc", payload));
        }
        var hdrl = List("hdrl", Chunk("avih", new byte[56]), List("strl", Chunk("strh", new byte[56])));
        var content = Encoding.ASCII.GetBytes("AVI ").Concat(hdrl).Concat(List("movi", movi.ToArray())).Concat(Chunk("idx1", new byte[16 * frames]));
        return Chunk("RIFF", content.ToArray());
    }

    private static byte[] Chunk(string id, byte[] content)
    {
        var bytes = new List<byte>(content.Length + 9);
        bytes.AddRange(Encoding.ASCII.GetBytes(id));
        bytes.AddRange(BitConverter.GetBytes((uint)content.Length));
        bytes.AddRange(content);
        if (content.Length % 2 != 0) bytes.Add(0);
        return bytes.ToArray();
    }

    private static byte[] List(string type, params byte[][] children) =>
        Chunk("LIST", Encoding.ASCII.GetBytes(type).Concat(children.SelectMany(c => c)).ToArray());
}
