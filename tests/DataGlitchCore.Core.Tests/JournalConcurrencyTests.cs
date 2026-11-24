namespace DataGlitchCore.Core.Tests;

/// <summary>
/// Ce que le journal fait hors de son verrou ou en tâche de fond, hors fusion :
/// l'écriture forcée d'une passe à son application, et la suppression de ce que
/// l'historique ne référence plus (passes à rétablir abandonnées, retour à
/// l'original). Critère 4 : revenir à l'original ne dépend plus de la place à libérer.
/// </summary>
public sealed class JournalConcurrencyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dgc-journal-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly FileBufferOptions Small = new() { ChunkShift = 12, PassBufferBytes = 4096, Durable = false };

    private static byte[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 3)).ToArray();

    private FileBuffer OpenFile(byte[] data)
    {
        string source = Path.Combine(_dir, "source.bin");
        File.WriteAllBytes(source, data);
        return FileBuffer.Open(source, _dir, Small);
    }

    private string[] Files(string folder) =>
        Directory.GetFiles(Path.Combine(_dir, folder)).Select(Path.GetFileName).Order().ToArray()!;

    private long OnDisk() =>
        new FileInfo(Path.Combine(_dir, "source.bin")).Length + Directory.GetFiles(Path.Combine(_dir, "segments")).Sum(f => new FileInfo(f).Length);

    private static DestructionBatch Pass(FileBuffer buffer, int seed) =>
        DestructionEngine.BuildBatch(buffer, new DestructionOptions { Seed = seed, Density = 0.3, BlockSize = 16 });

    /// <summary>Retient les suppressions en tâche de fond jusqu'à <see cref="Release"/>.</summary>
    private sealed class HeldDeletions
    {
        private readonly ManualResetEventSlim _go = new(false);
        public int Waiting;

        public void OnDelete(string name)
        {
            Interlocked.Increment(ref Waiting);
            _go.Wait(TimeSpan.FromSeconds(30));
        }

        public void Release() => _go.Set();
    }

    [Fact]
    public async Task A_Reset_Returns_At_Once_And_Its_Files_Count_Until_They_Are_Deleted()
    {
        var data = Ramp(32 * 4096);
        using var buffer = OpenFile(data);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 20 });
        for (int i = 1; i <= 4; i++) journal.Apply(Pass(buffer, i));
        await journal.WaitForMergesAsync();
        long before = journal.DiskBytes;
        Assert.Equal(OnDisk(), before);

        var held = new HeldDeletions();
        journal.DeleteHook = held.OnDelete;
        journal.ResetAll();

        // l'état est l'original tout de suite ; les segments restent sur le disque, et comptés, jusqu'à leur suppression
        Assert.Equal(data, buffer.Current.ReadAll());
        Assert.False(journal.CanUndo);
        Assert.False(journal.IsMerging);
        Assert.Equal(4, Files("segments").Length);
        Assert.Equal(OnDisk(), journal.DiskBytes);
        Assert.Equal(before, journal.DiskBytes);

        held.Release();
        await journal.WaitForMergesAsync();
        Assert.Empty(Files("segments"));
        Assert.Equal(data.Length, journal.DiskBytes);
    }

    [Fact]
    public async Task The_Redo_Passes_A_New_Pass_Drops_Count_Until_They_Are_Deleted()
    {
        using var buffer = OpenFile(Ramp(32 * 4096));
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 20 });
        journal.Apply(Pass(buffer, 1));
        journal.Apply(Pass(buffer, 2));
        journal.Undo();

        var held = new HeldDeletions();
        journal.DeleteHook = held.OnDelete;
        journal.Apply(Pass(buffer, 3));
        Assert.False(journal.CanRedo);
        Assert.Equal(["00000001.seg", "00000002.seg", "00000003.seg"], Files("segments"));
        Assert.Equal(OnDisk(), journal.DiskBytes);

        held.Release();
        await journal.WaitForMergesAsync();
        Assert.Equal(["00000001.seg", "00000003.seg"], Files("segments"));
        Assert.Equal(OnDisk(), journal.DiskBytes);
    }

    [Fact]
    public async Task A_Pass_That_Only_Fits_Once_The_Deletions_Are_Done_Waits_For_Them()
    {
        var data = Ramp(32 * 4096);
        using var buffer = OpenFile(data);
        long pass = DestructionEngine.EstimateSegmentBytes(buffer, new DestructionOptions { Seed = 1, Density = 0.3, BlockSize = 16 }, DestructionMask.WholeBuffer);
        // la place de l'original, d'une base dense et d'une passe : une seconde passe ne tient pas à côté de la première
        long budget = data.Length + BaseLayer.DenseBytes(buffer) + pass + pass / 2;
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 20, DiskBudgetBytes = budget });
        journal.Apply(Pass(buffer, 1));

        var held = new HeldDeletions();
        journal.DeleteHook = held.OnDelete;
        journal.ResetAll();
        SpinWait.SpinUntil(() => Volatile.Read(ref held.Waiting) > 0, TimeSpan.FromSeconds(30));

        // la passe suivante attend la fin des suppressions au lieu d'être refusée
        var next = Task.Run(() => Pass(buffer, 2));
        await Task.Delay(200);
        Assert.False(next.IsCompleted);
        held.Release();
        using var batch = await next.WaitAsync(TimeSpan.FromSeconds(30));
        journal.Apply(batch);
        Assert.True(journal.CanUndo);
    }

    [Fact]
    public async Task Closing_The_Buffer_Waits_For_The_Deletions()
    {
        var buffer = OpenFile(Ramp(32 * 4096));
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 20 });
        for (int i = 1; i <= 3; i++) journal.Apply(Pass(buffer, i));
        var held = new HeldDeletions();
        journal.DeleteHook = held.OnDelete;
        journal.ResetAll();

        var close = Task.Run(buffer.Dispose);
        await Task.Delay(200);
        Assert.False(close.IsCompleted);
        held.Release();
        await close.WaitAsync(TimeSpan.FromSeconds(30));
        // le store supprime ensuite le dossier : plus rien n'y est supprimé après la fermeture
        Assert.Empty(Files("segments"));
    }

    [Fact]
    public async Task Readers_Of_The_Journal_Do_Not_Wait_For_A_Pass_Being_Forced_To_Disk()
    {
        using var buffer = FileBuffer.FromBytes("x", Ramp(32 * 4096), Small);
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 20 });
        journal.Apply(Pass(buffer, 1));
        var publishing = new ManualResetEventSlim(false);
        var go = new ManualResetEventSlim(false);
        ((MemoryBlobStore)buffer.Store).Fault = (step, _) =>
        {
            if (step != "publier") return;
            publishing.Set();
            go.Wait(TimeSpan.FromSeconds(30));
        };

        var apply = Task.Run(() => journal.Apply(Pass(buffer, 2)));
        Assert.True(publishing.Wait(TimeSpan.FromSeconds(30)));
        // pendant que la passe se range, l'historique, la place et l'état se lisent sans attendre
        // (sans quoi l'attente ci-dessous expire : la lecture attendrait la fin de l'écriture forcée)
        var (count, _, canUndo) = await Task.Run(() => (journal.History.Count, journal.DiskBytes, journal.CanUndo))
                                            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, count);
        Assert.True(canUndo);

        go.Set();
        var info = await apply.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, info.Id);
        Assert.Equal(2, journal.History.Count);
    }

    [Fact]
    public async Task Writes_That_Are_Not_Serialised_Are_Refused_Without_Changing_Anything()
    {
        using var buffer = FileBuffer.FromBytes("x", Ramp(32 * 4096), Small);
        var store = (MemoryBlobStore)buffer.Store;
        var journal = new OperationJournal(buffer, new JournalOptions { MaxUndoSteps = 20 });
        journal.Apply(Pass(buffer, 1));
        var current = buffer.Current.ReadAll();
        using var first = Pass(buffer, 2);
        using var second = Pass(buffer, 3);
        var publishing = new ManualResetEventSlim(false);
        var go = new ManualResetEventSlim(false);
        store.Fault = (step, _) =>
        {
            if (step != "publier" || publishing.IsSet) return;
            publishing.Set();
            go.Wait(TimeSpan.FromSeconds(30));
        };

        // une seconde passe pendant que la première se range : refusée
        var apply = Task.Run(() => journal.Apply(first));
        Assert.True(publishing.Wait(TimeSpan.FromSeconds(30)));
        Assert.Throws<InvalidOperationException>(() => journal.Apply(second));
        Assert.False(second.Applied);

        // une annulation pendant que la première se range : c'est elle qui est refusée, une fois rangée
        journal.Undo();
        go.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => apply.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(first.Applied);
        Assert.Empty(journal.History);
        Assert.True(journal.CanRedo);
        journal.Redo();
        Assert.Equal(current, buffer.Current.ReadAll());

        // abandonnée, la passe refusée ne laisse rien
        first.Dispose();
        second.Dispose();
        Assert.Equal(["segments/00000001.seg"], store.Names.Order().ToArray());
    }
}
