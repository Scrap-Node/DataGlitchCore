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

    /// <summary>
    /// Écrit dans le modèle et dans le journal. Par défaut, attend ensuite la
    /// fin des fusions, pour vérifier la base qu'elles écrivent.
    /// </summary>
    private static void Write(OperationJournal journal, FileBuffer buffer, byte[] model, long offset, byte[] bytes, string? appData = null,
                              bool wait = true)
    {
        bytes.CopyTo(model, offset);
        var before = new byte[bytes.Length];
        buffer.Current.Read(offset, before);
        using var builder = buffer.BeginPass();
        builder.WriteDiff(offset, before, bytes, mergeGap: 0);
        journal.Apply(builder.Build($"écrit à {offset}"), appData);
        if (wait) Settle(journal);
    }

    private static void Settle(OperationJournal journal)
    {
        Assert.True(journal.WaitForMergesAsync().Wait(TimeSpan.FromSeconds(30)), "les fusions ne finissent pas");
    }

    /// <summary>Retient la tâche de fond à une étape de la fusion, jusqu'à <see cref="Release"/> ; un retour à l'original l'interrompt.</summary>
    private sealed class Hold(string step)
    {
        private readonly SemaphoreSlim _release = new(0);
        private readonly SemaphoreSlim _reached = new(0);
        public int Reached;

        public async Task OnStep(string current, CancellationToken ct)
        {
            if (current != step) return;
            Interlocked.Increment(ref Reached);
            _reached.Release();
            await _release.WaitAsync(ct);
        }

        /// <summary>Attend que la fusion soit arrivée à l'étape retenue.</summary>
        public void WaitReached() => Assert.True(_reached.Wait(TimeSpan.FromSeconds(30)), $"la fusion n'arrive pas à l'étape « {step} »");

        public void Release(int count = 1) => _release.Release(count);
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
        Settle(journal);
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
    public void A_Failed_Merge_Leaves_The_State_Right_And_Is_Retried_By_The_Next_Pass(string failingFile, string failingStep)
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

        store.Fault = (step, name) =>
        {
            if (name == failingFile && step == failingStep) throw new IOException("disque plein");
        };
        Write(journal, buffer, model, 200, Fill(20, 4), wait: false);

        // la passe est appliquée ; sa fusion échoue en tâche de fond, sans rien casser
        var failed = Assert.Throws<AggregateException>(() => journal.WaitForMergesAsync().Wait()).InnerException;
        Assert.IsType<IOException>(failed);
        Assert.NotNull(journal.MergeError);
        Assert.True(journal.IsMerging);
        Assert.Equal(model, buffer.Current.ReadAll());
        Assert.Equal(3, journal.MergedPasses);
        Assert.Single(journal.History);
        // rien d'écrit à moitié : les fichiers d'avant, plus le segment de la nouvelle passe
        Assert.Equal(files.Append("segments/00000004.seg").Order(), store.Names.Order());

        // la passe suivante relance la fusion de ce qui attend encore
        store.Fault = null;
        Write(journal, buffer, model, 300, Fill(5, 5));
        Assert.Null(journal.MergeError);
        Assert.False(journal.IsMerging);
        Assert.Equal(4, journal.MergedPasses);
        Assert.Equal(model, buffer.Current.ReadAll());
        Assert.True(journal.Base!.IsDense);
    }

    [Fact]
    public void A_Pass_Over_The_Disk_Budget_Is_Refused_Before_Anything_Is_Written()
    {
        var data = Ramp(8 * Chunk);
        using var buffer = OpenFile(data);
        // l'original, la place de réécrire une base, et 2000 octets
        long budget = data.Length + BaseLayer.DenseBytes(buffer) + 2000;
        var journal = new OperationJournal(buffer, new JournalOptions { DiskBudgetBytes = budget });
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
        // et elle laisse la place de réécrire une base, comme exigé
        Assert.True(journal.DiskBytes + BaseLayer.DenseBytes(buffer) <= budget);
    }

    [Fact]
    public void The_Disk_Budget_Merges_Old_Passes_Before_MaxUndoSteps_And_Is_Never_Exceeded()
    {
        var data = Ramp(16 * Chunk);
        using var buffer = OpenFile(data);
        // l'original, une base, la place d'en réécrire une, et quelques passes
        long budget = data.Length + 2 * BaseLayer.DenseBytes(buffer) + 3 * (data.Length / 2);
        var journal = new OperationJournal(buffer, new JournalOptions { DiskBudgetBytes = budget });

        for (int i = 0; i < 12; i++)
        {
            var options = new DestructionOptions { Seed = i, Density = 0.9, BlockSize = 32, Operator = DestructionOperator.Fixed, FixedValue = (byte)i,
                                                   RegionStart = 0, RegionEnd = 0.5 };
            journal.Apply(DestructionEngine.BuildBatch(buffer, options));
            // pendant la fusion, l'ancienne base et la nouvelle comptent toutes les deux
            Assert.True(journal.DiskBytes <= budget, $"pas {i}, fusion en cours : {journal.DiskBytes} o pour un budget de {budget}");
            Settle(journal);
            long onDisk = new DirectoryInfo(Path.Combine(_dir, "segments")).GetFiles().Sum(f => f.Length) + data.Length;
            Assert.Equal(journal.DiskBytes, onDisk);
            Assert.True(onDisk <= budget, $"pas {i} : {onDisk} o pour un budget de {budget}");
        }
        Assert.True(journal.MergedPasses > 0);
        Assert.True(journal.History.Count < 12);
    }

    [Fact]
    public void The_Budget_Counts_Both_Bases_While_A_Merge_Runs()
    {
        var data = Ramp(16 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var hold = new Hold("écrite");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(9 * Chunk, 1));
        Write(journal, buffer, model, 12 * Chunk, Fill(3 * Chunk, 2), wait: false); // la première fusionne, dense
        hold.WaitReached();

        // la nouvelle base est écrite et rangée, l'ancienne état tient encore : les deux comptent
        var during = journal.Snapshot();
        Assert.True(during.Merging);
        Assert.Equal(BaseLayer.DenseBytes(buffer), during.ReservedBytes);
        long segments = new DirectoryInfo(Path.Combine(_dir, "segments")).GetFiles()
            .Where(f => !f.Name.StartsWith("base-", StringComparison.Ordinal)).Sum(f => f.Length);
        Assert.Equal(data.Length + during.ReservedBytes + segments, journal.DiskBytes);
        Assert.Contains("base-00000001.seg", Files("segments"));

        hold.Release();
        Settle(journal);
        Assert.Equal(0, journal.Snapshot().ReservedBytes);
        Assert.Equal(data.Length + new DirectoryInfo(Path.Combine(_dir, "segments")).GetFiles().Sum(f => f.Length), journal.DiskBytes);
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public async Task A_Pass_That_Only_Fits_Once_The_Merge_Lands_Waits_For_It()
    {
        var data = new byte[16 * Chunk];
        var small = new DestructionOptions { Seed = 1, Density = 1, BlockSize = 16, Operator = DestructionOperator.Fixed, FixedValue = 7,
                                             RegionStart = 0.99, RegionEnd = 1 };

        // les tailles, mesurées à blanc en mémoire : mêmes écritures, mêmes segments
        long first, second, estimate, dense;
        using (var dry = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small))
        {
            var j = new OperationJournal(dry, new JournalOptions { MaxUndoSteps = 1 });
            var m = (byte[])data.Clone();
            Write(j, dry, m, 0, Fill(6 * Chunk, 1), wait: false);
            first = j.Files.Single().Length;
            Write(j, dry, m, 10 * Chunk, Fill(100, 2), wait: false);
            second = j.Files.Single(f => f.Name == "segments/00000002.seg").Length;
            estimate = DestructionEngine.EstimateSegmentBytes(dry, small, DestructionMask.WholeBuffer);
            dense = BaseLayer.DenseBytes(dry);
        }

        // pendant la fusion de la première passe, elle compte deux fois (segment et base
        // réservée) ; après, une seule : le budget tient entre les deux
        long budget = data.Length + first + second + estimate + dense + first / 2;
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1, DiskBudgetBytes = budget });
        var hold = new Hold("début");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(6 * Chunk, 1), wait: false);
        Write(journal, buffer, model, 10 * Chunk, Fill(100, 2), wait: false);
        hold.WaitReached();
        Assert.True(journal.DiskBytes + estimate + dense > budget);

        var pass = Task.Run(() => DestructionEngine.BuildBatch(buffer, small));
        await Task.Delay(300);
        Assert.False(pass.IsCompleted); // elle attend la fin de la fusion

        journal.MergeHook = null;
        hold.Release();
        using var batch = await pass.WaitAsync(TimeSpan.FromSeconds(30));
        journal.Apply(batch);
        Settle(journal);
        Assert.True(journal.DiskBytes <= budget);
        Assert.Equal(2, journal.MergedPasses);
    }

    [Fact]
    public void Apply_Returns_Before_The_Merge_And_The_Merged_Passes_Leave_The_History_At_Once()
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1, MergeAppData = d => string.Join("+", d) });
        var hold = new Hold("début");
        journal.MergeHook = hold.OnStep;

        Write(journal, buffer, model, 0, Fill(100, 1), "a", wait: false);
        Write(journal, buffer, model, 200, Fill(100, 2), "b", wait: false);
        hold.WaitReached();

        // pour l'historique, la première est fusionnée ; sur le disque, encore un segment
        Assert.True(journal.IsMerging);
        Assert.Equal(1, journal.MergedPasses);
        Assert.Equal(1, journal.LastMergedId);
        Assert.Equal("a", journal.MergedAppData);
        Assert.Equal([2L], journal.History.Select(p => p.Id));
        Assert.Null(journal.Base);
        Assert.Equal(["00000001.seg", "00000002.seg"], Files("segments"));
        Assert.Equal(model, buffer.Current.ReadAll());

        hold.Release();
        Settle(journal);
        Assert.False(journal.IsMerging);
        Assert.Equal(["00000002.seg", "base-00000001.seg"], Files("segments"));
        Assert.Equal("a", journal.Base!.AppData);
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void A_Pass_Built_While_A_Merge_Lands_Is_Still_Applied_On_The_Same_Bytes()
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var hold = new Hold("écrite");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(3 * Chunk, 1), wait: false);
        Write(journal, buffer, model, 5 * Chunk, Fill(100, 2), wait: false);
        hold.WaitReached();

        // la passe part sur l'état où la première passe est encore un segment…
        using var builder = buffer.BeginPass();
        var before = builder.Before.ReadAll();
        Assert.Equal(model, before);

        // …la fusion bascule et supprime ce segment pendant qu'on la construit
        journal.MergeHook = null;
        hold.Release();
        Settle(journal);
        Assert.DoesNotContain("00000001.seg", Files("segments"));
        Assert.Equal(model, builder.Before.ReadAll()); // relue sur la base, mêmes octets

        var after = (byte[])before.Clone();
        after.AsSpan(Chunk, 50).Fill(9);
        builder.WriteDiff(0, before, after);
        var batch = builder.Build("pendant la fusion");
        Assert.Equal(after, batch.After.ReadAll());
        journal.Apply(batch); // même état : la fusion n'en a changé que l'agencement
        after.CopyTo(model, 0);
        Settle(journal);
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_Follow_The_Merge_Even_When_A_Deleted_File_Says_Access_Denied(bool windows)
    {
        // sous Windows, ouvrir un fichier en cours de suppression rend « accès refusé », pas « introuvable »
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var store = (MemoryBlobStore)buffer.Store;
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var hold = new Hold("écrite");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(3 * Chunk, 1), wait: false);
        Write(journal, buffer, model, 5 * Chunk, Fill(100, 2), wait: false);
        hold.WaitReached();
        using var builder = buffer.BeginPass();
        var before = buffer.ViewOf(builder.BaseState);
        var batchBefore = builder.Before;

        journal.MergeHook = null;
        hold.Release();
        Settle(journal);
        store.Fault = (step, name) =>
        {
            if (step == "lire" && !store.Names.Contains(name))
                throw windows ? new UnauthorizedAccessException($"Accès refusé : {name}") : new FileNotFoundException(name);
        };
        Assert.Equal(model, buffer.Current.ReadAll());
        Assert.Equal(model, batchBefore.ReadAll());
        Assert.Equal(model, before.ReadAll());
        var after = (byte[])model.Clone();
        after.AsSpan(Chunk, 10).Fill(9);
        builder.WriteDiff(0, model, after);
        journal.Apply(builder.Build("pendant la fusion"));
        store.Fault = null;
        Assert.Equal(after, buffer.Current.ReadAll());
    }

    [Fact]
    public void Undo_And_Redo_During_A_Merge_Leave_The_Merging_Passes_Alone()
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 2 });
        var hold = new Hold("début");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(100, 1), wait: false);
        var afterFirst = (byte[])model.Clone();
        Write(journal, buffer, model, 1000, Fill(100, 2), wait: false);
        Write(journal, buffer, model, 2000, Fill(100, 3), wait: false);
        hold.WaitReached();

        Assert.NotNull(journal.Undo());
        Assert.NotNull(journal.Undo());
        Assert.Null(journal.Undo()); // la première fusionne : elle ne s'annule plus
        Assert.Equal(afterFirst, buffer.Current.ReadAll());
        Assert.NotNull(journal.Redo());
        Assert.NotNull(journal.Redo());
        Assert.Equal(model, buffer.Current.ReadAll());

        journal.Undo();
        hold.Release();
        Settle(journal);
        model.AsSpan(2000, 100).Clear();
        data.AsSpan(2000, 100).CopyTo(model.AsSpan(2000));
        Assert.Equal(model, buffer.Current.ReadAll());
        journal.Redo();
        Assert.Equal(3, buffer.Current.ReadAll()[2000]);
    }

    [Theory]
    [InlineData("début")]
    [InlineData("écrite")]
    public void Reset_Interrupts_A_Merge_And_Leaves_Nothing_Behind(string step)
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var hold = new Hold(step);
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(5 * Chunk, 1), wait: false);
        Write(journal, buffer, model, 6 * Chunk, Fill(10, 2), wait: false);
        hold.WaitReached();

        journal.ResetAll();
        Assert.False(journal.IsMerging);
        Assert.Equal(0, journal.MergedPasses);
        Assert.Equal(data, buffer.Current.ReadAll());
        Assert.Empty(Files("segments"));
        Assert.Empty(Files("tmp"));

        // la session repart, et ses fusions avec
        journal.MergeHook = null;
        model = (byte[])data.Clone();
        Write(journal, buffer, model, 10, Fill(10, 3));
        Write(journal, buffer, model, 30, Fill(10, 4));
        Assert.Equal(1, journal.MergedPasses);
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void Passes_Applied_During_A_Merge_Wait_For_The_Next_One()
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var hold = new Hold("début");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(10, 1), wait: false);
        Write(journal, buffer, model, 100, Fill(10, 2), wait: false);
        hold.WaitReached(); // la première fusion ne prend que la première passe
        for (int i = 3; i <= 6; i++) Write(journal, buffer, model, i * 100, Fill(10, (byte)i), wait: false);
        Assert.Equal(5, journal.MergedPasses);
        Assert.Equal(model, buffer.Current.ReadAll());

        hold.Release(2);
        Settle(journal);
        Assert.Equal(2, hold.Reached); // une fusion pour la première, une seule pour les quatre suivantes
        Assert.Equal(5, journal.Base!.Id);
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public async Task Waiting_For_Merges_Waits_Until_The_Merged_Files_Are_Gone()
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var hold = new Hold("basculée");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(10, 1), wait: false);
        Write(journal, buffer, model, 100, Fill(10, 2), wait: false);
        hold.WaitReached();

        // basculée : l'état ne référence plus le segment fusionné, mais il est encore sur le disque ; la
        // fusion n'est finie qu'une fois supprimé ce qu'elle remplace (§7, étape 3), et il compte jusque-là
        Assert.True(journal.IsMerging);
        Assert.Contains("00000001.seg", Files("segments"));
        long onDisk = new FileInfo(Path.Combine(_dir, "source.bin")).Length
                      + Directory.GetFiles(Path.Combine(_dir, "segments")).Sum(f => new FileInfo(f).Length);
        Assert.Equal(onDisk, journal.DiskBytes);
        var wait = journal.WaitForMergesAsync();
        await Task.Delay(200);
        Assert.False(wait.IsCompleted);

        hold.Release();
        await wait.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(journal.IsMerging);
        Assert.Equal(["00000002.seg", "base-00000001.seg"], Files("segments"));
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void A_Failed_Merge_Is_Retried_By_Any_Next_Pass()
    {
        var data = Ramp(4 * Chunk);
        var model = (byte[])data.Clone();
        using var buffer = FileBuffer.FromBytes("x", (byte[])data.Clone(), Small);
        var store = (MemoryBlobStore)buffer.Store;
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 2 });
        store.Fault = (step, name) =>
        {
            if (name.StartsWith("tmp/base-", StringComparison.Ordinal)) throw new IOException("disque plein");
        };
        Write(journal, buffer, model, 0, Fill(10, 1), wait: false);
        Write(journal, buffer, model, 100, Fill(10, 2), wait: false);
        Write(journal, buffer, model, 200, Fill(10, 3), wait: false); // la première fusionne, et échoue
        Assert.Throws<AggregateException>(() => journal.WaitForMergesAsync().Wait());
        Assert.True(journal.IsMerging);

        // une passe qui, d'elle-même, ne ferait rien fusionner : elle relance pourtant la fusion en attente
        store.Fault = null;
        journal.Undo();
        model.AsSpan(200, 10).Clear();
        data.AsSpan(200, 10).CopyTo(model.AsSpan(200));
        Write(journal, buffer, model, 300, Fill(10, 4));
        Assert.Equal(1, journal.MergedPasses);
        Assert.False(journal.IsMerging);
        Assert.NotNull(journal.Base);
        Assert.Equal(model, buffer.Current.ReadAll());
    }

    [Fact]
    public void Closing_The_Buffer_Interrupts_A_Merge()
    {
        var data = Ramp(8 * Chunk);
        var model = (byte[])data.Clone();
        var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 1 });
        var hold = new Hold("début");
        journal.MergeHook = hold.OnStep;
        Write(journal, buffer, model, 0, Fill(10, 1), wait: false);
        Write(journal, buffer, model, 100, Fill(10, 2), wait: false);
        hold.WaitReached();

        buffer.Dispose(); // n'attend pas qu'on relâche la fusion : il l'interrompt
        Assert.Empty(Files("tmp"));
        Assert.Equal(["00000001.seg", "00000002.seg"], Files("segments"));
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
            Write(journal, buffer, model, (i * 5003) % (data.Length - 3000), Fill(3000, (byte)i), wait: false);
        Settle(journal);
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
            // une fois la fusion faite : pendant, l'ancienne base et la nouvelle coexistent
            Settle(journal);

            var files = new DirectoryInfo(Path.Combine(_dir, "segments")).GetFiles();
            long unmerged = files.Where(f => !f.Name.StartsWith("base-", StringComparison.Ordinal)).Sum(f => f.Length);
            long total = data.Length + files.Sum(f => f.Length);
            Assert.True(total <= data.Length + BaseLayer.DenseBytes(buffer) + unmerged,
                $"pas {i} : {total} o sur le disque, {unmerged} o de segments non fusionnés");
            Assert.True(journal.History.Count <= 2);
        }
    }
}
