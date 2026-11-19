using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DataGlitchCore.Bench;
using DataGlitchCore.Core;

// Banc de mesure du cœur (spec dataglitchcore-buffer, §13.3 et D14). Hors CI.
//
//   dotnet run -c Release --project bench/DataGlitchCore.Bench -- [options]
//
//   --data DIR            dossier des WAV de banc, créés au besoin (défaut : $TMPDIR/dgc-bench)
//   --sizes 256,1024      tailles des WAV, en Mio
//   --passes 1,10,20,30   points de mesure, en nombre de passes ; au-delà de 20, les plus anciennes fusionnent
//   --max-gb 9            empreinte au-delà de laquelle le banc s'arrête plutôt que de faire swapper
//   --label base          nom de la série de mesures
//   --out DIR             où écrire le compte rendu (défaut : bench/mesures)
//
// Le même code mesure aussi la 0.5.0, où la fusion se fait dans Apply :
// WaitForMergesAsync et IsMerging y sont cherchés par réflexion.

var options = new Options(args);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (options.Get("scenario") is { } scenarioFile)
{
    var result = await Scenario.RunAsync(scenarioFile, options);
    await File.WriteAllTextAsync(options.Get("result")!, JsonSerializer.Serialize(result, Json.Options));
    return 0;
}

string data = options.Get("data") ?? Path.Combine(Path.GetTempPath(), "dgc-bench");
Directory.CreateDirectory(data);
int[] sizes = options.Ints("sizes", [256, 1024]);
int[] checkpoints = options.Ints("passes", [1, 10, 20, 30]);
double maxGb = options.Double("max-gb", 9);
string label = options.Get("label") ?? "mesure";

var report = new Report(label, Machine.Describe(), checkpoints, maxGb);
foreach (int size in sizes)
{
    string file = await Inputs.WavAsync(data, size);
    Console.Error.WriteLine($"[banc] {Path.GetFileName(file)} : {Units.Mb(new FileInfo(file).Length)}");
    var result = await Child.RunAsync<ScenarioResult>(
    [
        "--scenario", file, "--passes", string.Join(',', checkpoints), "--max-gb", maxGb.ToString(CultureInfo.InvariantCulture),
    ]);
    report.Add(result);
}

string outDir = options.Get("out") ?? Path.Combine(AppContext.BaseDirectory, "../../../../mesures");
Directory.CreateDirectory(outDir);
string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
string markdown = report.ToMarkdown();
await File.WriteAllTextAsync(Path.Combine(outDir, $"{stamp}-{label}.md"), markdown);
await File.WriteAllTextAsync(Path.Combine(outDir, $"{stamp}-{label}.json"), JsonSerializer.Serialize(report, Json.Options));
Console.WriteLine(markdown);
return 0;

/// <summary>Une passe : sa construction, son application, et l'état des fusions juste avant.</summary>
internal sealed record PassTiming(int Number, double BuildMs, double ApplyMs, bool MergingBefore, long MergedPasses);

/// <summary>Ce qu'on mesure à un nombre de passes donné.</summary>
internal sealed record Checkpoint(
    int Passes,
    MemorySample Memory,
    Timed ZoomedRead,
    Timed WholeRead,
    Timed Export,
    Timed Undo,
    Timed Redo,
    bool Merging,
    long MergedPasses,
    string Base,
    long DiskBytes);

internal sealed record ScenarioResult(
    string File,
    long Bytes,
    MemorySample Start,
    Timed Open,
    MemorySample Opened,
    List<PassTiming> Passes,
    List<Checkpoint> Checkpoints,
    Timed? MergesSettled,
    Timed? Reset,
    string? Stopped,
    double LoadBefore,
    double LoadAfter);

/// <summary>
/// Un WAV ouvert sur disque, puis des passes par défaut enchaînées sans
/// pause, comme un utilisateur pressé, et à chaque point de mesure : mémoire,
/// lectures, export, annuler, rétablir. Une fusion en tâche de fond tourne
/// pendant ce temps, si elle a lieu : c'est ce qu'on veut mesurer.
/// </summary>
internal static class Scenario
{
    public static async Task<ScenarioResult> RunAsync(string file, Options options)
    {
        int[] checkpoints = options.Ints("passes", [1, 10, 20, 30]);
        long maxBytes = (long)(options.Double("max-gb", 9) * (1L << 30));
        long size = new FileInfo(file).Length;
        string work = Directory.CreateTempSubdirectory("dgc-bench-core-").FullName;
        try
        {
            double loadBefore = Machine.LoadAverage();
            var start = Measure.Settle();
            FileBuffer buffer = null!;
            var open = Measure.Run(() => buffer = FileBuffer.Open(file, work));
            using var _ = buffer;
            var journal = new OperationJournal(buffer);
            var opened = Measure.Settle();
            var mask = DestructionMask.FromPayloadStart(44);

            var passes = new List<PassTiming>();
            var points = new List<Checkpoint>();
            string? stopped = null;
            for (int i = 1; i <= checkpoints.Max(); i++)
            {
                if (Measure.Footprint() > maxBytes)
                {
                    stopped = $"arrêté avant la passe {i} : empreinte de {Units.Mb(Measure.Footprint())}, au-delà de la limite de {Units.Mb(maxBytes)}";
                    break;
                }

                // la passe par défaut de DataBender : Xor, 15 %, blocs de 64 o, tout le signal
                var passOptions = new DestructionOptions { Operator = DestructionOperator.Xor, Density = 0.15, BlockSize = 64, XorMask = 0xFF, Seed = i };
                bool mergingBefore = Merges.IsMerging(journal);
                DestructionBatch batch = null!;
                var build = Measure.Run(() => batch = DestructionEngine.BuildBatch(buffer, passOptions, mask));
                var apply = Measure.Run(() => journal.Apply(batch));
                batch.Dispose();
                passes.Add(new PassTiming(i, build.Ms, apply.Ms, mergingBefore, journal.MergedPasses));
                Console.Error.WriteLine($"[banc]   passe {i} : {Units.Ms(build.Ms)} + {Units.Ms(apply.Ms)}{(mergingBefore ? ", fusion en cours" : "")}");
                if (!checkpoints.Contains(i)) continue;

                var memory = Measure.Settle();
                bool merging = Merges.IsMerging(journal);
                var zoomed = Measure.Median(3, () => buffer.Current.Read(size / 2, new byte[size / 100]));
                var whole = Measure.Median(3, () => ReadAll(buffer));
                string export = Path.Combine(work, "export.bin");
                var exported = await Measure.RunAsync(() => ExportFile.WriteAsync(export, (stream, ct) => buffer.WriteCurrentToAsync(stream, ct)));
                File.Delete(export);
                var undo = Measure.Run(() => journal.Undo());
                var redo = Measure.Run(() => journal.Redo());
                points.Add(new Checkpoint(i, memory, zoomed, whole, exported, undo, redo, merging, journal.MergedPasses,
                                          BaseKind(work), journal.DiskBytes));
                Console.Error.WriteLine($"[banc]   point {i} : tas {Units.Mb(memory.LiveBytes)}, disque {Units.Mb(journal.DiskBytes)}");
            }

            var settled = await Measure.RunAsync(() => Merges.WaitAsync(journal));
            var reset = Measure.Run(journal.ResetAll);
            return new ScenarioResult(file, size, start, open, opened, passes, points, settled, reset, stopped, loadBefore, Machine.LoadAverage());
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void ReadAll(FileBuffer buffer)
    {
        var chunk = new byte[1 << 20];
        for (long at = 0; at < buffer.Length; at += chunk.Length)
            buffer.Current.Read(at, chunk.AsSpan(0, (int)Math.Min(chunk.Length, buffer.Length - at)));
    }

    /// <summary>La base sur le disque : aucune, éparse ou dense (avec ses compteurs à part).</summary>
    private static string BaseKind(string work)
    {
        var bases = Directory.GetFiles(Path.Combine(work, "segments"), "base-*");
        return bases.Length == 0 ? "aucune" : bases.Any(f => f.EndsWith(".diff", StringComparison.Ordinal)) ? "dense" : "éparse";
    }
}

/// <summary>Les fusions en tâche de fond, cherchées par réflexion : en 0.5.0, elles n'existent pas, la fusion se fait dans Apply.</summary>
internal static class Merges
{
    private static readonly System.Reflection.MethodInfo? Wait = typeof(OperationJournal).GetMethod("WaitForMergesAsync");
    private static readonly System.Reflection.PropertyInfo? Running = typeof(OperationJournal).GetProperty("IsMerging");

    public static bool InBackground => Wait is not null;

    public static bool IsMerging(OperationJournal journal) => Running?.GetValue(journal) is true;

    public static Task WaitAsync(OperationJournal journal) =>
        Wait is null ? Task.CompletedTask : (Task)Wait.Invoke(journal, [CancellationToken.None])!;
}

/// <summary>Les WAV du banc : 48 kHz, stéréo, 16 bits, quelques sinus et du bruit. Les mêmes que ceux du banc de DataBender.</summary>
internal static class Inputs
{
    public static async Task<string> WavAsync(string dir, int mib)
    {
        string path = Path.Combine(dir, $"bench-{mib}.wav");
        if (File.Exists(path)) return path;

        const int rate = 48_000, channels = 2, frameSize = 4;
        long dataLength = (mib * Units.Mib - 44) / frameSize * frameSize;
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + dataLength));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), rate * frameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), frameSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)dataLength);

        string temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            await stream.WriteAsync(header);
            var chunk = new byte[1 << 20];
            uint noise = 12345;
            long frame = 0;
            for (long done = 0; done < dataLength;)
            {
                int n = (int)Math.Min(chunk.Length, dataLength - done);
                for (int i = 0; i < n; i += frameSize, frame++)
                {
                    double t = frame / (double)rate;
                    double tone = 0.2 * Math.Sin(2 * Math.PI * 220 * t) + 0.1 * Math.Sin(2 * Math.PI * 1234.5 * t)
                                + 0.05 * Math.Sin(2 * Math.PI * 7000 * t * (1 + 0.1 * Math.Sin(0.3 * t)));
                    noise = noise * 1664525 + 1013904223;
                    double white = ((noise >> 8) / (double)(1 << 24) - 0.5) * 0.1;
                    short left = (short)Math.Clamp((tone + white) * 32767, short.MinValue, short.MaxValue);
                    short right = (short)Math.Clamp((tone - white) * 32767, short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i), left);
                    BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i + 2), right);
                }
                await stream.WriteAsync(chunk.AsMemory(0, n));
                done += n;
            }
        }
        File.Move(temp, path);
        return path;
    }
}

/// <summary>Le compte rendu, en Markdown, et sa version JSON.</summary>
internal sealed class Report(string label, Dictionary<string, string> machine, int[] checkpoints, double maxGb)
{
    public string Label { get; } = label;
    public Dictionary<string, string> Host { get; } = machine;
    public int[] CheckpointPasses { get; } = checkpoints;
    public double MaxGb { get; } = maxGb;
    public string Commit { get; } = Revision(".");
    public string Version { get; } = typeof(FileBuffer).Assembly.GetName().Version?.ToString(3) ?? "?";
    public bool BackgroundMerges { get; } = Merges.InBackground;
    public List<ScenarioResult> Scenarios { get; } = new();

    public void Add(ScenarioResult result) => Scenarios.Add(result);

    private static string Revision(string repo)
    {
        string commit = Machine.Shell("git", $"-C {repo} rev-parse --short HEAD").Trim();
        bool dirty = Machine.Shell("git", $"-C {repo} status --porcelain --untracked-files=no").Trim().Length > 0;
        return dirty ? commit + " (modifié)" : commit;
    }

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.AppendLine($"# Banc du cœur : {Label}");
        md.AppendLine();
        md.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}, DataGlitchCore {Version} ({Commit}), fusion {(BackgroundMerges ? "en tâche de fond" : "dans Apply")}. " +
                      $"Limite d'empreinte : {MaxGb} Go.");
        md.AppendLine();
        foreach (var (key, value) in Host) md.AppendLine($"- **{key}** : {value}");
        md.AppendLine();
        md.AppendLine("Passe par défaut de DataBender, enchaînée sans pause : Xor, densité 15 %, blocs de 64 o, tout le signal, " +
                      "seed = numéro de la passe. Durée de la passe : construction + application, ce qu'attend l'utilisateur. " +
                      "Lecture zoomée : 1 % du fichier ; lecture complète : tout le fichier par fenêtres d'1 Mio ; médiane de 3. " +
                      "Export : l'état courant écrit tout ou rien, forcé sur le disque. Tas : octets vivants après une collecte complète.");
        foreach (var s in Scenarios)
        {
            md.AppendLine();
            md.AppendLine($"## {Path.GetFileName(s.File)} : {Units.Mb(s.Bytes)}");
            md.AppendLine();
            md.AppendLine($"- Ouverture : {Units.Ms(s.Open.Ms)} ; tas {Units.Mb(s.Start.LiveBytes)} → {Units.Mb(s.Opened.LiveBytes)}");
            if (s.Passes.Count > 0)
            {
                var durations = s.Passes.Select(p => p.BuildMs + p.ApplyMs).ToList();
                var sorted = durations.Order().ToList();
                md.AppendLine($"- Passes ({s.Passes.Count}) : première {Units.Ms(durations[0])}, médiane {Units.Ms(sorted[sorted.Count / 2])}, " +
                              $"plus longue {Units.Ms(sorted[^1])}, dernière {Units.Ms(durations[^1])}");
                md.AppendLine($"- Passes 21 et suivantes : {string.Join(", ", s.Passes.Skip(20).Select(p => Units.Ms(p.BuildMs + p.ApplyMs) + (p.MergingBefore ? "*" : "")))}" +
                              (BackgroundMerges ? " (* : une fusion tournait au début de la passe)" : ""));
            }
            if (s.MergesSettled is { } settled) md.AppendLine($"- Fin des fusions en cours, après la dernière passe : {Units.Ms(settled.Ms)}");
            if (s.Reset is { } reset) md.AppendLine($"- Retour à l'original, en fin de série : {Units.Ms(reset.Ms)}");
            if (s.Stopped is { } stopped) md.AppendLine($"- **{stopped}**");
            md.AppendLine($"- Charge de la machine (moyenne sur une minute) : {s.LoadBefore:0.0} au début, {s.LoadAfter:0.0} à la fin, pour {Environment.ProcessorCount} cœurs");
            md.AppendLine();
            md.AppendLine("| Passes | Durée de la passe | Tas | Δ tas depuis l'ouverture | Empreinte | Lecture zoomée | Lecture complète | Export | Annuler | Rétablir | Fusion | Base | Disque |");
            md.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---:|");
            foreach (var c in s.Checkpoints)
            {
                var p = s.Passes[c.Passes - 1];
                md.AppendLine($"| {c.Passes} | {Units.Ms(p.BuildMs + p.ApplyMs)} | {Units.Mb(c.Memory.LiveBytes)} | " +
                              $"{Units.Mb(c.Memory.LiveBytes - s.Opened.LiveBytes)} | {Units.Mb(c.Memory.FootprintBytes)} | " +
                              $"{Units.Ms(c.ZoomedRead.Ms)} | {Units.Ms(c.WholeRead.Ms)} | {Units.Ms(c.Export.Ms)} | " +
                              $"{Units.Ms(c.Undo.Ms)} | {Units.Ms(c.Redo.Ms)} | " +
                              $"{(c.MergedPasses == 0 ? "aucune" : $"{c.MergedPasses} passes{(c.Merging ? ", en cours" : "")}")} | {c.Base} | " +
                              $"{c.DiskBytes / (double)s.Bytes:0.00} × |");
            }
        }
        return md.ToString();
    }
}
