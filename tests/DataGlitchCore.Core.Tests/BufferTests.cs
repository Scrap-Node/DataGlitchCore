namespace DataGlitchCore.Core.Tests;

/// <summary>Le buffer sur disque et son journal (spec §4.2, §6, §13.2).</summary>
public sealed class BufferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dgc-buffer-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 13)).ToArray();

    private FileBuffer OpenFile(byte[] data, FileBufferOptions? options = null)
    {
        string source = Path.Combine(_dir, "source.bin");
        File.WriteAllBytes(source, data);
        return FileBuffer.Open(source, _dir, options);
    }

    private string[] Files(string folder) =>
        Directory.GetFiles(Path.Combine(_dir, folder)).Select(Path.GetFileName).Order().ToArray()!;

    private static DestructionOptions Xor(int seed) => new() { Seed = seed, Density = 0.5, BlockSize = 16 };

    // ---- état ----

    [Fact]
    public void Apply_Refuses_A_Pass_Built_On_Another_State()
    {
        using var buf = OpenFile(Ramp(4096));
        var journal = new OperationJournal(buf);
        journal.Apply(DestructionEngine.BuildBatch(buf, Xor(1)));

        using var stale = DestructionEngine.BuildBatch(buf, Xor(2));
        journal.Undo(); // l'état a changé depuis sa construction
        var before = buf.Current.ReadAll();

        var refused = Assert.Throws<InvalidOperationException>(() => journal.Apply(stale));
        Assert.Contains("Rien n'a été modifié", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, buf.Current.ReadAll());
        Assert.False(journal.CanUndo);
    }

    [Fact]
    public void Apply_Refuses_A_Pass_Of_Another_Buffer_Or_Applied_Twice()
    {
        using var a = FileBuffer.FromBytes("a", Ramp(256));
        using var b = FileBuffer.FromBytes("b", Ramp(256));
        var journal = new OperationJournal(a);

        using var foreign = DestructionEngine.BuildBatch(b, Xor(1));
        Assert.Throws<InvalidOperationException>(() => journal.Apply(foreign));

        var batch = DestructionEngine.BuildBatch(a, Xor(1));
        journal.Apply(batch);
        Assert.Throws<InvalidOperationException>(() => journal.Apply(batch));
    }

    [Fact]
    public void Before_And_After_Are_The_States_Around_The_Pass()
    {
        using var buf = OpenFile(Ramp(10_000));
        var journal = new OperationJournal(buf);
        journal.Apply(DestructionEngine.BuildBatch(buf, Xor(1)));
        var state = buf.Current.ReadAll();

        using var batch = DestructionEngine.BuildBatch(buf, Xor(2));

        Assert.Equal(state, batch.Before.ReadAll());
        Assert.Equal(state, buf.Current.ReadAll()); // construite, elle ne change rien
        var after = batch.After.ReadAll();
        Assert.NotEqual(state, after);
        journal.Apply(batch);
        Assert.Equal(after, buf.Current.ReadAll());
    }

    // ---- fichiers ----

    [Fact]
    public void A_Pass_Disposed_Without_Applying_Leaves_Nothing_On_Disk()
    {
        using var buf = OpenFile(Ramp(100_000), new FileBufferOptions { PassBufferBytes = 4096 });

        var batch = DestructionEngine.BuildBatch(buf, Xor(1));
        Assert.Single(Files("tmp"));
        batch.Dispose();
        Assert.Empty(Files("tmp"));

        using (var builder = buf.BeginPass())
            builder.WriteDiff(0, [0], [1]);
        Assert.Empty(Files("tmp"));
        Assert.Empty(Files("segments"));
    }

    [Fact]
    public void Applied_Passes_Live_In_Segments_And_Go_With_Redo_And_Reset()
    {
        using var buf = OpenFile(Ramp(50_000));
        var journal = new OperationJournal(buf);
        for (int i = 1; i <= 3; i++) journal.Apply(DestructionEngine.BuildBatch(buf, Xor(i)));
        Assert.Equal(["00000001.seg", "00000002.seg", "00000003.seg"], Files("segments"));
        Assert.Empty(Files("tmp"));

        // annuler garde le segment, à rétablir ; une nouvelle passe le supprime
        journal.Undo();
        Assert.Equal(3, Files("segments").Length);
        journal.Apply(DestructionEngine.BuildBatch(buf, Xor(9)));
        Assert.Equal(["00000001.seg", "00000002.seg", "00000004.seg"], Files("segments"));

        journal.ResetAll();
        Assert.Empty(Files("segments"));
        Assert.Equal(buf.Original.ReadAll(), buf.Current.ReadAll());
    }

    [Fact]
    public void The_Segment_Carries_The_Pass_Number()
    {
        using var buf = OpenFile(Ramp(4096));
        var journal = new OperationJournal(buf);
        journal.Apply(DestructionEngine.BuildBatch(buf, Xor(1)));
        var info = journal.Apply(DestructionEngine.BuildBatch(buf, Xor(2)));

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "segments", "00000002.seg"));
        Assert.Equal("DGCS"u8.ToArray(), bytes[..4]);
        Assert.Equal(info.Id, BitConverter.ToInt64(bytes, 8));
    }

    // ---- pannes (R4) ----

    [Theory]
    [InlineData("écrire")]
    [InlineData("publier")]
    public void A_Failure_Leaves_The_State_Unchanged_And_Nothing_Behind(string step)
    {
        var data = Ramp(200_000);
        using var buf = FileBuffer.FromBytes("x", data, new FileBufferOptions { PassBufferBytes = 4096 });
        var journal = new OperationJournal(buf);
        journal.Apply(DestructionEngine.BuildBatch(buf, Xor(1)));
        var state = buf.Current.ReadAll();
        var files = ((MemoryBlobStore)buf.Store).Names.Order().ToArray();
        long changed = journal.ChangedBytes(0, buf.Length);

        int calls = 0;
        ((MemoryBlobStore)buf.Store).Fault = (kind, _) =>
        {
            // le disque se remplit au milieu de la passe, ou au moment de la ranger
            if (kind == step && ++calls == (step == "écrire" ? 3 : 1)) throw new IOException("Plus de place sur le disque.");
        };
        Assert.Throws<IOException>(() =>
        {
            using var batch = DestructionEngine.BuildBatch(buf, Xor(2));
            journal.Apply(batch);
        });
        ((MemoryBlobStore)buf.Store).Fault = null;

        Assert.Equal(state, buf.Current.ReadAll());
        Assert.Single(journal.History);
        Assert.Equal(changed, journal.ChangedBytes(0, buf.Length));
        Assert.Equal(files, ((MemoryBlobStore)buf.Store).Names.Order().ToArray());

        // et le journal repart : une passe suivante s'applique normalement
        journal.Apply(DestructionEngine.BuildBatch(buf, Xor(3)));
        Assert.Equal(2, journal.History.Count);
    }

    // ---- réserve de descripteurs (R3) ----

    [Fact]
    public void Open_Files_Stay_Under_The_Process_Limit()
    {
        int limit = FileHandles.Limit;
        try
        {
            FileHandles.Limit = 4;
            using var buf = OpenFile(Ramp(64 * 1024), new FileBufferOptions { ChunkShift = 12 });
            var journal = new OperationJournal(buf);
            var expected = buf.Original.ReadAll();
            for (int i = 1; i <= 12; i++)
            {
                journal.Apply(DestructionEngine.BuildBatch(buf, Xor(i)));
                Assert.True(FileHandles.OpenCount <= 4, $"{FileHandles.OpenCount} descripteurs ouverts après la passe {i}");
            }

            // douze segments relus derrière une réserve de quatre descripteurs
            var current = buf.Current.ReadAll();
            Assert.True(FileHandles.OpenCount <= 4);
            Assert.NotEqual(expected, current);
            for (int i = 0; i < 12; i++) journal.Undo();
            Assert.Equal(expected, buf.Current.ReadAll());
        }
        finally
        {
            FileHandles.Limit = limit;
        }
    }

    // ---- différences (§6.3) ----

    [Fact]
    public void ChangedBytes_Is_Exact_On_Any_Range()
    {
        using var buf = FileBuffer.FromBytes("x", Ramp(50_000), new FileBufferOptions { ChunkShift = 10 });
        var journal = new OperationJournal(buf);
        for (int i = 1; i <= 4; i++) journal.Apply(DestructionEngine.BuildBatch(buf, Xor(i)));
        journal.Undo();
        var original = buf.Original.ReadAll();
        var current = buf.Current.ReadAll();

        var rng = new Random(5);
        for (int k = 0; k < 200; k++)
        {
            int start = rng.Next(0, 50_000), length = rng.Next(0, 50_000 - start);
            int naive = 0;
            for (int i = start; i < start + length; i++) if (original[i] != current[i]) naive++;
            Assert.Equal(naive, journal.ChangedBytes(start, length));
        }
    }

    [Fact]
    public void DiffMap_Reads_The_Counters_Beyond_The_Direct_Threshold()
    {
        // tranches de 1 Kio, comparaison directe sous 4 Kio par case
        var options = new FileBufferOptions { ChunkShift = 10, DirectDiffBucketBytes = 4096 };
        using var buf = FileBuffer.FromBytes("x", Ramp(64 * 1024), options);
        var journal = new OperationJournal(buf);
        journal.Apply(DestructionEngine.BuildBatch(buf, new DestructionOptions { Seed = 1, Density = 0.3, BlockSize = 50, RegionEnd = 0.5 }));

        // 8 cases de 8 Kio, alignées sur les tranches : les compteurs donnent la valeur exacte
        var byCounters = DiffMap.Compute(buf, 8);
        var direct = DiffMap.Compute(buf.Original, buf.Current, 8);
        Assert.Equal(direct, byCounters);
        Assert.True(byCounters[0] > 0);
        Assert.Equal(0f, byCounters[7]);

        // sous le seuil, comparaison directe
        Assert.Equal(DiffMap.Compute(buf.Original, buf.Current, 64), DiffMap.Compute(buf, 64));
    }

    // ---- écriture ----

    [Fact]
    public async Task WriteCurrent_Streams_The_Layered_State()
    {
        using var buf = OpenFile(Ramp(3 * 1024 * 1024 + 11));
        var journal = new OperationJournal(buf);
        journal.Apply(DestructionEngine.BuildBatch(buf, Xor(1)));

        using var output = new MemoryStream();
        await buf.WriteCurrentToAsync(output);
        Assert.Equal(buf.Current.ReadAll(), output.ToArray());

        using var part = new MemoryStream();
        await buf.WriteCurrentToAsync(part, 1_000_000, 2_000_000);
        Assert.Equal(buf.Current.ReadAll().AsSpan(1_000_000, 2_000_000).ToArray(), part.ToArray());
    }
}
