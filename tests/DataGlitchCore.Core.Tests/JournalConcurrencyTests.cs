namespace DataGlitchCore.Core.Tests;

/// <summary>
/// Ce que le journal fait hors de son verrou, hors fusion : l'écriture forcée
/// d'une passe à son application.
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
