using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataGlitchCore.Bench;

// La boîte à outils du banc (spec dataglitchcore-buffer, §13.3) : mémoire,
// durées, machine, et lancement d'un scénario dans un processus neuf. La même
// dans les bancs de DataBender et de DataMosher, aux noms près.

/// <summary>La mémoire du processus à un instant donné.</summary>
/// <param name="LiveBytes">Octets vivants du tas managé, après une collecte complète.</param>
/// <param name="HeapBytes">Taille du tas managé (<see cref="GCMemoryInfo.HeapSizeBytes"/>), fragmentation comprise.</param>
/// <param name="FootprintBytes">Empreinte du processus : <c>phys_footprint</c> sous macOS, ce qu'affiche le
/// Moniteur d'activité ; ailleurs, la mémoire résidente.</param>
internal sealed record MemorySample(long LiveBytes, long HeapBytes, long FootprintBytes);

/// <summary>Une opération chronométrée, avec ce qu'elle a coûté en mémoire au plus fort.</summary>
/// <param name="Ms">Durée.</param>
/// <param name="PeakFootprintDeltaBytes">Pic d'empreinte pendant l'opération, au-dessus de l'empreinte de départ.</param>
/// <param name="AllocatedBytes">Octets alloués sur le tas managé pendant l'opération.</param>
internal sealed record Timed(double Ms, long PeakFootprintDeltaBytes, long AllocatedBytes);

internal static class Measure
{
    /// <summary>Collecte complète, puis la mémoire qui reste.</summary>
    public static MemorySample Settle()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        return new MemorySample(GC.GetTotalMemory(forceFullCollection: false), GC.GetGCMemoryInfo().HeapSizeBytes, Footprint());
    }

    /// <summary>L'empreinte du processus, en octets.</summary>
    public static long Footprint()
    {
        if (!OperatingSystem.IsMacOS()) return Environment.WorkingSet;
        // rusage_info_v2 : uuid (16 o), puis des uint64 ; ri_phys_footprint est le huitième
        var info = new byte[256];
        if (proc_pid_rusage(Environment.ProcessId, 2, info) != 0) return Environment.WorkingSet;
        return BitConverter.ToInt64(info, 16 + 7 * 8);
    }

    public static Timed Run(Action action) => RunAsync(() =>
    {
        action();
        return Task.CompletedTask;
    }).GetAwaiter().GetResult();

    /// <summary>Chronomètre une opération, en relevant toutes les 2 ms l'empreinte pour en garder le pic.</summary>
    public static async Task<Timed> RunAsync(Func<Task> action)
    {
        long before = Footprint();
        long peak = before;
        using var stop = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long now = Footprint();
                if (now > Volatile.Read(ref peak)) Volatile.Write(ref peak, now);
                Thread.Sleep(2);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();

        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        await action();
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        stop.Cancel();
        sampler.Join();
        long after = Footprint();
        long max = Math.Max(Volatile.Read(ref peak), after);
        return new Timed(watch.Elapsed.TotalMilliseconds, Math.Max(0, max - before), allocated);
    }

    /// <summary>La médiane de plusieurs essais, pour une opération sans effet sur l'état.</summary>
    public static Timed Median(int runs, Action action)
    {
        var all = Enumerable.Range(0, runs).Select(_ => Run(action)).OrderBy(t => t.Ms).ToList();
        return all[all.Count / 2];
    }

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);
}

/// <summary>La machine du banc : ce qu'il faut consigner avec les mesures (D15).</summary>
internal static class Machine
{
    public static Dictionary<string, string> Describe()
    {
        var info = new Dictionary<string, string>();
        if (OperatingSystem.IsMacOS())
        {
            string hardware = Shell("system_profiler", "SPHardwareDataType");
            foreach (var line in hardware.Split('\n'))
            {
                var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2) continue;
                switch (parts[0])
                {
                    case "Model Name": info["Modèle"] = parts[1]; break;
                    case "Model Identifier": info["Identifiant"] = parts[1]; break;
                    case "Chip": info["Puce"] = parts[1]; break;
                    case "Total Number of Cores": info["Cœurs"] = parts[1]; break;
                    case "Memory": info["RAM"] = parts[1]; break;
                }
            }
            info["macOS"] = $"{Shell("sw_vers", "-productVersion").Trim()} ({Shell("sw_vers", "-buildVersion").Trim()})";
        }
        else
        {
            info["Système"] = RuntimeInformation.OSDescription;
            info["RAM"] = $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1L << 30)} Go";
        }
        info[".NET"] = RuntimeInformation.FrameworkDescription;
        return info;
    }

    /// <summary>
    /// La charge moyenne de la machine sur une minute : une mesure prise sous
    /// une charge étrangère au banc (synchronisation iCloud, autres sessions)
    /// n'est pas comparable à une autre.
    /// </summary>
    public static double LoadAverage()
    {
        var loads = new double[3];
        return OperatingSystem.IsWindows() || getloadavg(loads, 3) < 1 ? double.NaN : loads[0];
    }

    [DllImport("libc")]
    private static extern int getloadavg(double[] loadavg, int nelem);

    public static string Shell(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false })!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }
}

/// <summary>Lance un scénario dans un processus neuf : sa mémoire ne doit rien à celui d'avant.</summary>
internal static class Child
{
    public static async Task<T> RunAsync<T>(IEnumerable<string> args)
    {
        string result = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        // lancé par « dotnet x.dll » plutôt que par son exécutable : il faut redonner la dll
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            start.ArgumentList.Add(typeof(Child).Assembly.Location);
        foreach (var a in args) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--result");
        start.ArgumentList.Add(result);

        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(result))
            throw new InvalidOperationException($"Le scénario a échoué (code {process.ExitCode}) : {string.Join(' ', args)}");
        try
        {
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(result), Json.Options)!;
        }
        finally
        {
            File.Delete(result);
        }
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

/// <summary>Les options de la ligne de commande, en <c>--nom valeur</c>.</summary>
internal sealed class Options(string[] args)
{
    public string? Get(string name)
    {
        int i = Array.IndexOf(args, "--" + name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public bool Has(string name) => args.Contains("--" + name);

    public int[] Ints(string name, int[] fallback) =>
        Get(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray() ?? fallback;

    public double Double(string name, double fallback) =>
        Get(name) is { } v ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
}

internal static class Units
{
    public const long Mib = 1024L * 1024;

    public static string Mb(long bytes) => $"{bytes / (double)Mib:0} Mo";

    public static string Ms(double ms) => ms >= 10_000 ? $"{ms / 1000:0.0} s" : $"{ms:0} ms";
}
