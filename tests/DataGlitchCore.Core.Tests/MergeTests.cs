namespace DataGlitchCore.Core.Tests;

/// <summary>
/// La fusion et l'historique borné (spec §5.2, §7, critère 6) : les cas que
/// les tirages de l'oracle atteignent trop rarement pour qu'on s'y fie.
/// </summary>
public sealed class MergeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dgc-merge-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Chunk = 4096;
    private static readonly FileBufferOptions Small = new() { ChunkShift = 12, PassBufferBytes = 4096, Durable = false };

    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 3)).ToArray();

    private static byte[] Fill(int n, byte value) => Enumerable.Repeat(value, n).ToArray();

    private FileBuffer OpenFile(byte[] data, FileBufferOptions? options = null)
    {
        string source = Path.Combine(_dir, "source.bin");
        File.WriteAllBytes(source, data);
        return FileBuffer.Open(source, _dir, options ?? Small);
    }

    private string[] Files(string folder) =>
        Directory.GetFiles(Path.Combine(_dir, folder)).Select(Path.GetFileName).Order().ToArray()!;

    private static long Naive(byte[] original, byte[] current) => original.Zip(current).Count(p => p.First != p.Second);

    /// <summary>Écrit dans le modèle et dans le journal.</summary>
    private static void Write(OperationJournal journal, FileBuffer buffer, byte[] model, long offset, byte[] bytes, string? appData = null)
    {
        bytes.CopyTo(model, offset);
        var before = new byte[bytes.Length];
        buffer.Current.Read(offset, before);
        using var builder = buffer.BeginPass();
        builder.WriteDiff(offset, before, bytes, mergeGap: 0);
        journal.Apply(builder.Build($"écrit à {offset}"), appData);
    }

    [Fact]
    public void A_Run_Ending_On_A_Chunk_Boundary_Stops_Before_A_Skipped_Chunk()
    {
        var data = Ramp(5 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });

        Write(journal, buffer, model, Chunk - 6, Fill(6, 0xAA));          // finit pile au bout de la tranche 0
        Write(journal, buffer, model, 3 * Chunk + 10, Fill(10, 0xBB));    // la tranche 2 n'est pas touchée
        Write(journal, buffer, model, 4 * Chunk, Fill(1, 0xCC));          // fait fusionner les deux premières

        Assert.Equal(2, journal.MergedPasses);
        var merged = journal.Base!.Sparse!;
        Assert.Equal([new ByteRange(Chunk - 6, Chunk), new ByteRange(3 * Chunk + 10, 3 * Chunk + 20)], merged.Runs());
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void Runs_Less_Than_12_Bytes_Apart_Join_Across_A_Chunk_Boundary_With_The_Original_Bytes()
    {
        var data = Ramp(3 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });

        Write(journal, buffer, model, Chunk - 6, Fill(4, 0xAA));      // [4090, 4094)
        Write(journal, buffer, model, Chunk + 5, Fill(4, 0xBB));      // [4101, 4105) : 7 octets plus loin
        Write(journal, buffer, model, Chunk + 30, Fill(4, 0xCC));     // [4126, 4130) : 21 octets plus loin
        Write(journal, buffer, model, 0, Fill(1, 0xDD));

        Assert.Equal(3, journal.MergedPasses);
        Assert.Equal([new ByteRange(Chunk - 6, Chunk + 9), new ByteRange(Chunk + 30, Chunk + 34)], journal.Base!.Sparse!.Runs());
        Assert.Equal(model, buffer.Current.ReadAll());
        // l'écart garde l'original : ni compté comme différent, ni dans l'état
        Assert.Equal(Naive(data, model), journal.ChangedBytes(0, data.Length));
    }

    [Fact]
    public void The_Base_Becomes_Dense_Once_It_Covers_Half_The_File()
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });

        Write(journal, buffer, model, 0, Fill(4 * Chunk - 20, 0x11));
        Write(journal, buffer, model, 6 * Chunk, Fill(10, 0x22));
        Assert.False(journal.Base!.IsDense);
        Assert.Equal(["00000002.seg", "base-00000001.seg"], Files("segments"));

        Write(journal, buffer, model, 4 * Chunk - 20, Fill(20, 0x33));
        Assert.False(journal.Base!.IsDense);                            // 10 octets de moins que la moitié
        Assert.Equal(["00000003.seg", "base-00000002.seg"], Files("segments"));

        Write(journal, buffer, model, 7 * Chunk, Fill(5, 0x44));         // la base couvre la moitié
        Assert.True(journal.Base!.IsDense);
        Assert.Equal(3, journal.MergedPasses);
        Assert.Equal(["00000004.seg", "base-00000003.diff", "base-00000003.seg"], Files("segments"));
        Assert.Equal(data.Length, new FileInfo(Path.Combine(_dir, "segments", "base-00000003.seg")).Length);
        Assert.Equal(model, buffer.Current.ReadAll());

        // dense, la base se refait par clone : encore une fusion, puis tout s'annule jusqu'à elle
        Write(journal, buffer, model, 100, Fill(3, 0x55));
        Assert.True(journal.Base!.IsDense);
        Assert.Equal(["00000005.seg", "base-00000004.diff", "base-00000004.seg"], Files("segments"));
        Assert.Equal(model, buffer.Current.ReadAll());
        journal.Undo();
        Assert.False(journal.CanUndo);
        model.AsSpan(100, 3).Fill(0x11);
        Assert.Equal(model, buffer.Current.ReadAll());
        Assert.Equal(data, buffer.Original.ReadAll());
        Assert.Equal(Naive(data, model), journal.ChangedBytes(0, data.Length));
        Assert.Empty(Files("tmp"));

        journal.ResetAll();
        Assert.Equal(data, buffer.Current.ReadAll());
        Assert.Equal(0, journal.MergedPasses);
        Assert.Empty(Files("segments"));
    }

    [Fact]
    public void A_Sparse_Base_That_Would_Outweigh_A_Dense_One_Becomes_Dense()
    {
        // un octet sur 13 : la base ne couvre qu'un treizième du fichier, mais
        // chaque octet y coûte 12 octets d'en-tête de plage
        var data = Ramp(16 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });

        var after = (byte[])data.Clone();
        for (int i = 0; i < after.Length; i += 13) after[i] ^= 0xFF;
        after.CopyTo(model, 0);
        using (var builder = buffer.BeginPass())
        {
            builder.WriteDiff(0, data, after, mergeGap: 0);
            journal.Apply(builder.Build("un sur treize"));
        }
        Write(journal, buffer, model, 5, Fill(1, 0));

        Assert.True(journal.Base!.IsDense);
        Assert.True(journal.Base.DiskBytes <= BaseLayer.DenseBytes(buffer));
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void History_Keeps_At_Most_MaxUndoSteps_And_The_Last_Pass_Stays_Undoable()
    {
        var data = Ramp(4 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 3 });

        var states = new List<byte[]>();
        for (int i = 0; i < 10; i++)
        {
            Write(journal, buffer, model, i * 1000, Fill(500, (byte)i));
            states.Add((byte[])model.Clone());
            Assert.True(journal.History.Count <= 3);
            Assert.True(journal.CanUndo);
        }
        Assert.Equal(7, journal.MergedPasses);
        Assert.Equal(7, journal.LastMergedId);
        Assert.Equal([8L, 9, 10], journal.History.Select(p => p.Id));

        for (int i = 9; i >= 7; i--)
        {
            Assert.Equal(states[i], buffer.Current.ReadAll());
            Assert.NotNull(journal.Undo());
        }
        Assert.Null(journal.Undo());
        Assert.Equal(states[6], buffer.Current.ReadAll());
        while (journal.Redo() is not null) { }
        Assert.Equal(states[9], buffer.Current.ReadAll());

        // une passe après une annulation : ce qui était à rétablir disparaît, la fusion continue
        journal.Undo();
        Write(journal, buffer, model = (byte[])states[8].Clone(), 3, Fill(2, 0xEE));
        Assert.Equal(7, journal.MergedPasses);
        Assert.Equal([8L, 9, 11], journal.History.Select(p => p.Id));
        Assert.Equal(model, buffer.Current.ReadAll());
        Assert.Equal(journal.History.Count + 1, Files("segments").Length - (journal.Base!.IsDense ? 1 : 0));
    }

    [Fact]
    public void MergeAppData_Receives_The_Base_Then_The_Merged_Passes_Oldest_First()
    {
        var calls = new List<string>();
        using var buffer = FileBuffer.FromBytes("x", Ramp(3 * Chunk), Small);
        var journal = new OperationJournal(buffer, new JournalOptions
        {
            MaxUndoSteps = 2,
            MergeAppData = data =>
            {
                calls.Add(string.Join("+", data.Select(d => d ?? "∅")));
                return string.Concat(data);
            },
        });
        var model = Ramp(3 * Chunk);

        Write(journal, buffer, model, 0, Fill(3, 1), "a");
        Write(journal, buffer, model, 10, Fill(3, 2), null);
        Assert.Null(journal.MergedAppData);
        Write(journal, buffer, model, 20, Fill(3, 3), "c");
        Write(journal, buffer, model, 30, Fill(3, 4), "d");

        Assert.Equal(["a", "a+∅"], calls);
        Assert.Equal("a", journal.MergedAppData);
        Assert.Equal(["c", "d"], journal.History.Select(p => p.AppData));
        journal.ResetAll();
        Assert.Null(journal.MergedAppData);
        Assert.Equal(0, journal.LastMergedId);
    }

    [Theory]
    [InlineData("tmp/base-00000003.seg", "écrire")]       // la base éparse, en cours d'écriture
    [InlineData("tmp/base-00000003.dense", "écrire")]     // la base dense, en cours d'écriture
    [InlineData("tmp/base-00000003.dense", "publier")]
    [InlineData("tmp/base-00000003.diff", "publier")]     // la base dense déjà rangée, ses compteurs non
    public void A_Failed_Merge_Abandons_The_Pass_And_Changes_Nothing(string failingFile, string failingStep)
    {
        var data = Ramp(4 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var store = (MemoryBlobStore)buffer.Store;
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        Write(journal, buffer, model, 0, Fill(10, 1));
        Write(journal, buffer, model, 100, Fill(10, 2));
        Write(journal, buffer, model, Chunk, Fill(2 * Chunk + 10, 3)); // la prochaine fusion rendra la base dense
        var files = store.Names.Order().ToList();
        long merged = journal.MergedPasses;

        store.Fault = (step, name) =>
        {
            if (name == failingFile && step == failingStep) throw new IOException("disque plein");
        };
        var before = new byte[20];
        buffer.Current.Read(200, before);
        using var builder = buffer.BeginPass();
        builder.WriteDiff(200, before, Fill(20, 4));
        var batch = builder.Build("refusée");

        var refused = Assert.Throws<IOException>(() => journal.Apply(batch));
        Assert.Contains("rien n'a été modifié", refused.Message);
        store.Fault = null;
        Assert.Equal(model, buffer.Current.ReadAll());
        Assert.Equal(merged, journal.MergedPasses);
        Assert.Single(journal.History);
        Assert.Equal(files, store.Names.Order().ToList());
        Assert.Throws<InvalidOperationException>(() => journal.Apply(batch));

        // et la session continue
        Write(journal, buffer, model, 300, Fill(5, 5));
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void A_Pass_Over_The_Disk_Budget_Is_Refused_Before_Anything_Is_Written()
    {
        var data = Ramp(8 * Chunk);
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { DiskBudgetBytes = data.Length + 2000 });
        var options = new DestructionOptions { Seed = 1, Density = 1, BlockSize = 64, Operator = DestructionOperator.Fixed, FixedValue = 9 };

        var refused = Assert.Throws<DiskBudgetExceededException>(() => DestructionEngine.BuildBatch(buffer, options));
        Assert.Contains("Rien n'a été modifié", refused.Message);
        Assert.Equal(data, buffer.Current.ReadAll());
        Assert.Empty(Files("tmp"));
        Assert.Empty(Files("segments"));
        Assert.False(journal.CanUndo);

        // une petite passe tient
        options.RegionEnd = 0.01;
        journal.Apply(DestructionEngine.BuildBatch(buffer, options));
        Assert.True(journal.DiskBytes <= data.Length + 2000);
    }

    [Fact]
    public void The_Disk_Budget_Merges_Old_Passes_Before_MaxUndoSteps()
    {
        var data = Ramp(16 * Chunk);
        using var buffer = OpenFile(data);
        long budget = data.Length + BaseLayer.DenseBytes(buffer) + 3 * (data.Length / 2);
        var journal = new OperationJournal(buffer, new JournalOptions { DiskBudgetBytes = budget });

        for (int i = 0; i < 12; i++)
        {
            var options = new DestructionOptions { Seed = i, Density = 0.9, BlockSize = 32, Operator = DestructionOperator.Fixed, FixedValue = (byte)i,
                                                   RegionStart = 0, RegionEnd = 0.5 };
            journal.Apply(DestructionEngine.BuildBatch(buffer, options));
            long onDisk = new DirectoryInfo(Path.Combine(_dir, "segments")).GetFiles().Sum(f => f.Length) + data.Length;
            Assert.Equal(journal.DiskBytes, onDisk);
            Assert.True(onDisk <= budget, $"pas {i} : {onDisk} o pour un budget de {budget}");
        }
        Assert.True(journal.MergedPasses > 0);
        Assert.True(journal.History.Count < 12);
    }

    [Fact]
    public void The_Estimate_Covers_The_Segment_Of_A_Full_Pass()
    {
        var data = Ramp(64 * Chunk);
        using var buffer = FileBuffer.FromBytes("x", data, Small);
        foreach (int block in new[] { 1, 2, 7, 64, 4096 })
        {
            var options = new DestructionOptions { Seed = 3, Density = 1, BlockSize = block, Operator = DestructionOperator.Fixed, FixedValue = 0xFF, RegionStart = 0.1, RegionEnd = 0.9 };
            using var batch = DestructionEngine.BuildBatch(buffer, options);
            long estimate = DestructionEngine.EstimateSegmentBytes(buffer, options, DestructionMask.WholeBuffer);
            Assert.True(batch.Segment.Blob.Length <= estimate, $"bloc {block} : {batch.Segment.Blob.Length} o écrits pour {estimate} estimés");
            Assert.True(estimate <= 2 * 0.8 * data.Length + 12 * 64 + 200);
        }
    }

    [Fact]
    public void The_Counters_File_Reads_Back_And_Refuses_A_Truncated_Copy()
    {
        using var buffer = OpenFile(Ramp(8 * Chunk));
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var model = Ramp(8 * Chunk);
        Write(journal, buffer, model, 0, Fill(5 * Chunk, 1));
        var merged = (byte[])model.Clone();                 // la base : la première passe seule
        Write(journal, buffer, model, 7 * Chunk, Fill(5, 2));

        var dense = journal.Base!;
        Assert.True(dense.IsDense);
        Assert.Equal(dense.DenseDiffs, DiffFileFormat.Read(dense.DiffFile!, 12));
        var original = Ramp(8 * Chunk);
        Assert.Equal(Enumerable.Range(0, 8).Select(c => (int)Naive(original[(c * Chunk)..((c + 1) * Chunk)], merged[(c * Chunk)..((c + 1) * Chunk)])),
                     dense.DenseDiffs!);
        Assert.Throws<InvalidDataException>(() => DiffFileFormat.Read(dense.DiffFile!, 20));

        string path = Path.Combine(_dir, "segments", "base-00000001.diff");
        var bytes = File.ReadAllBytes(path);
        var truncated = new MemoryBlobStore([]).CreateTemp("t.diff");
        truncated.Write(0, bytes.AsSpan(0, bytes.Length - 3));
        Assert.Throws<InvalidDataException>(() => DiffFileFormat.Read(truncated, 12));
    }

    [Fact]
    public async Task Readers_Never_Fail_While_Merges_Delete_What_They_Were_Reading()
    {
        var data = Ramp(64 * Chunk);
        using var buffer = OpenFile(data, new FileBufferOptions { ChunkShift = 12, Durable = false });
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        using var stop = new CancellationTokenSource();
        long reads = 0;
        var reader = Task.Run(() =>
        {
            var bytes = new byte[data.Length];
            while (!stop.IsCancellationRequested)
            {
                buffer.Current.Read(0, bytes);
                reads++;
            }
        });

        var model = (byte[])data.Clone();
        for (int i = 0; i < 40; i++)
            Write(journal, buffer, model, (i * 5003) % (data.Length - 3000), Fill(3000, (byte)i));
        stop.Cancel();
        await reader; // une exception du lecteur ferait échouer le test ici
        Assert.True(reads > 0);
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void The_Disk_Of_A_Session_Stays_Under_Twice_The_File_Plus_Unmerged_Segments()
    {
        var data = Ramp(32 * Chunk);
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 2 });
        var rng = new Random(9);
        for (int i = 0; i < 25; i++)
        {
            var options = new DestructionOptions { Seed = i, Density = rng.NextDouble(), BlockSize = rng.Next(1, 200), Operator = (DestructionOperator)rng.Next(7) };
            journal.Apply(DestructionEngine.BuildBatch(buffer, options));
            if (i % 4 == 3) journal.Undo();

            var files = new DirectoryInfo(Path.Combine(_dir, "segments")).GetFiles();
            long unmerged = files.Where(f => !f.Name.StartsWith("base-", StringComparison.Ordinal)).Sum(f => f.Length);
            long total = data.Length + files.Sum(f => f.Length);
            Assert.True(total <= data.Length + BaseLayer.DenseBytes(buffer) + unmerged,
                $"pas {i} : {total} o sur le disque, {unmerged} o de segments non fusionnés");
            Assert.True(journal.History.Count <= 2);
        }
    }
}
