using DataGlitchCore.Core;
using Xunit;

namespace DataGlitchCore.Core.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dgc-export-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- FileBuffer.WriteCurrentToAsync ----

    [Fact]
    public async Task WriteCurrent_WritesTheCurrentState_NotTheOriginal()
    {
        var buf = FileBuffer.FromBytes("x.bin", [1, 2, 3, 4]);
        new OperationJournal(buf).Write(buf, 1, [9, 9]);

        using var output = new MemoryStream();
        await buf.WriteCurrentToAsync(output);

        Assert.Equal(new byte[] { 1, 9, 9, 4 }, output.ToArray());
    }

    [Fact]
    public async Task WriteCurrent_CrossesChunkBoundariesWithoutLosingAByte()
    {
        // un peu plus de deux morceaux, avec un reste
        var data = Enumerable.Range(0, FileBuffer.WriteChunkSize * 2 + 4321).Select(i => (byte)(i * 7)).ToArray();
        var buf = FileBuffer.FromBytes("x.bin", data);

        using var output = new MemoryStream();
        await buf.WriteCurrentToAsync(output);

        Assert.Equal(data, output.ToArray());
    }

    [Fact]
    public async Task WriteCurrent_WritesJustTheRequestedRange()
    {
        var buf = FileBuffer.FromBytes("x.bin", [10, 20, 30, 40, 50]);

        using var output = new MemoryStream();
        await buf.WriteCurrentToAsync(output, 1, 3);

        Assert.Equal(new byte[] { 20, 30, 40 }, output.ToArray());
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 6)]
    [InlineData(4, 2)]
    [InlineData(6, 0)]
    public async Task WriteCurrent_RefusesARangeOutsideTheBuffer(int offset, int count)
    {
        var buf = FileBuffer.FromBytes("x.bin", [1, 2, 3, 4, 5]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => buf.WriteCurrentToAsync(Stream.Null, offset, count));
    }

    [Fact]
    public async Task WriteCurrent_StopsWhenCancelled()
    {
        var buf = FileBuffer.FromBytes("x.bin", new byte[16]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            buf.WriteCurrentToAsync(new MemoryStream(), new CancellationToken(canceled: true)));
    }

    // ---- ExportFile ----

    [Fact]
    public async Task ExportFile_WritesTheFileAndLeavesNoTemporary()
    {
        string target = Path.Combine(_dir, "sortie.wav");

        await ExportFile.WriteAsync(target, (s, ct) => s.WriteAsync(new byte[] { 1, 2, 3 }, ct).AsTask());

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
        Assert.Equal([target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ExportFile_ReplacesAnExistingFileOnlyOnSuccess()
    {
        string target = Path.Combine(_dir, "sortie.wav");
        File.WriteAllBytes(target, [7, 7]);

        await ExportFile.WriteAsync(target, (s, ct) => s.WriteAsync(new byte[] { 1, 2, 3 }, ct).AsTask());

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task ExportFile_AFailedWriteKeepsTheExistingFileAndCleansUp()
    {
        string target = Path.Combine(_dir, "sortie.wav");
        File.WriteAllBytes(target, [7, 7]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportFile.WriteAsync(target, async (s, ct) =>
        {
            await s.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            throw new InvalidOperationException("en route");
        }));

        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(target));
        Assert.Equal([target], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ExportFile_ACancelledWriteLeavesNothingBehind()
    {
        string target = Path.Combine(_dir, "sortie.wav");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportFile.WriteAsync(target, async (s, ct) =>
        {
            await s.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            // annulé une fois le contenu écrit : le fichier ne doit pas apparaître pour autant
            cts.Cancel();
        }, cts.Token));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void ExportFile_TemporaryIsHiddenNextToTheTarget()
    {
        using var export = ExportFile.Create(Path.Combine(_dir, "sortie.mp4"));

        Assert.Equal(_dir, Path.GetDirectoryName(export.TempPath));
        Assert.StartsWith(".sortie.mp4.", Path.GetFileName(export.TempPath));
        Assert.EndsWith(".tmp", export.TempPath);
    }

    [Fact]
    public void ExportFile_AnExternalToolCanWriteTheTemporary()
    {
        // le cas de ffmpeg : il écrit lui-même au chemin donné
        string target = Path.Combine(_dir, "sortie.mp4");
        using (var export = ExportFile.Create(target))
        {
            File.WriteAllBytes(export.TempPath, [4, 2]);
            export.Commit();
        }

        Assert.Equal(new byte[] { 4, 2 }, File.ReadAllBytes(target));
        Assert.Equal([target], Directory.GetFiles(_dir));
    }

    [Fact]
    public void ExportFile_DisposedWithoutCommitDeletesTheTemporary()
    {
        string target = Path.Combine(_dir, "sortie.mp4");
        using (var export = ExportFile.Create(target))
            File.WriteAllBytes(export.TempPath, [4, 2]);

        Assert.Empty(Directory.GetFiles(_dir));
    }

    // ---- écriture forcée sur le disque ----

    /// <summary>Les étapes de l'écriture forcée, dans l'ordre, et ce que la cible contenait à chacune.</summary>
    private List<string> ObserveDurability(string target)
    {
        var steps = new List<string>();
        DurableFile.Observer.Value = (step, path) =>
        {
            string content = File.Exists(target) ? string.Join(',', File.ReadAllBytes(target)) : "absente";
            steps.Add($"{step} {Path.GetFileName(path)} (cible : {content})");
        };
        return steps;
    }

    [Fact]
    public async Task ExportFile_ForcesTheTemporaryToDiskBeforeTheRenameThenTheFolder()
    {
        string target = Path.Combine(_dir, "sortie.wav");
        File.WriteAllBytes(target, [7, 7]);
        var steps = ObserveDurability(target);

        await ExportFile.WriteAsync(target, (s, ct) => s.WriteAsync(new byte[] { 1, 2, 3 }, ct).AsTask());

        Assert.Equal(3, steps.Count);
        // le temporaire est forcé alors que la cible est encore l'ancienne
        Assert.Matches(@"^fichier \.sortie\.wav\.[0-9a-f]{8}\.tmp \(cible : 7,7\)$", steps[0]);
        Assert.Equal("renommage sortie.wav (cible : 1,2,3)", steps[1]);
        Assert.Equal($"dossier {Path.GetFileName(_dir)} (cible : 1,2,3)", steps[2]);
    }

    [Fact]
    public void ExportFile_ForcesAFileWrittenByAnExternalToolToo()
    {
        string target = Path.Combine(_dir, "sortie.mp4");
        var steps = ObserveDurability(target);

        using (var export = ExportFile.Create(target))
        {
            File.WriteAllBytes(export.TempPath, [4, 2]);
            export.Commit();
        }

        Assert.Equal(["fichier", "renommage", "dossier"], steps.Select(s => s.Split(' ')[0]));
    }

    [Fact]
    public async Task ExportFile_AFailedWriteForcesNothing()
    {
        string target = Path.Combine(_dir, "sortie.wav");
        var steps = ObserveDurability(target);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExportFile.WriteAsync(target, (_, _) => throw new InvalidOperationException("en route")));

        Assert.Empty(steps);
    }

    [Fact]
    public void DurableFile_ForcesARealFolderOnThisSystem()
    {
        // le vrai appel système : F_FULLFSYNC sous macOS, fsync ailleurs
        DurableFile.FlushDirectory(_dir);
    }

    [Fact]
    public void DurableFile_ForcesARealFileOnThisSystem()
    {
        string path = Path.Combine(_dir, "f.bin");
        File.WriteAllBytes(path, [1]);
        DurableFile.FlushFile(path);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void DurableFile_RefusesAMissingFolderOrFile()
    {
        string missing = Path.Combine(_dir, "absent");
        if (!OperatingSystem.IsWindows())
            Assert.Throws<DirectoryNotFoundException>(() => DurableFile.FlushDirectory(missing));
        Assert.Throws<FileNotFoundException>(() => DurableFile.FlushFile(missing));
    }
}
