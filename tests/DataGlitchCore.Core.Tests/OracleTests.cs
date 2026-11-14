using DataGlitchCore.Core.Tests.Reference;
using Xunit.Sdk;

namespace DataGlitchCore.Core.Tests;

/// <summary>
/// L'oracle des « mêmes octets » (spec dataglitchcore-buffer, §13.1). Des suites
/// aléatoires de passes, d'annulations, de rétablissements et de retours à
/// l'original tournent à la fois sur le cœur et sur sa copie de 24c3d18
/// (<c>Reference*</c>). À chaque pas, l'état courant doit être identique octet
/// pour octet, l'historique concorder, et <see cref="OperationJournal.ChangedBytes"/>
/// égaler une comparaison naïve.
///
/// Le cœur y tourne en mémoire ou sur de vrais fichiers, avec des tranches,
/// des fenêtres et un plafond de passe réduits à quelques Kio, pour forcer
/// tous les chemins : lectures à cheval sur les tranches, passes vidées sur
/// disque en cours de construction.
///
/// Une suite qui échoue donne sa seed. Pour la rejouer seule :
/// <c>DGC_ORACLE_SEED=1234 dotnet test --filter Oracle</c>. Pour en lancer plus :
/// <c>DGC_ORACLE_SEEDS=5000</c> (nombre de seeds), <c>DGC_ORACLE_FIRST_SEED</c>
/// (première seed).
/// </summary>
public sealed class OracleTests
{
    private const int DefaultSeeds = 150;

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        if (int.TryParse(Environment.GetEnvironmentVariable("DGC_ORACLE_SEED"), out int only))
        {
            data.Add(only);
            return data;
        }

        int count = int.TryParse(Environment.GetEnvironmentVariable("DGC_ORACLE_SEEDS"), out int n) && n > 0 ? n : DefaultSeeds;
        int first = int.TryParse(Environment.GetEnvironmentVariable("DGC_ORACLE_FIRST_SEED"), out int f) ? f : 0;
        for (int i = 0; i < count; i++) data.Add(first + i);
        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_Sequences_Give_The_Same_Bytes_As_The_Reference(int seed) => new OracleScenario(seed).Run();

    [Theory]
    [MemberData(nameof(Seeds))]
    public void WriteDiff_Gives_The_Same_Runs_As_ToRuns_Slice_By_Slice(int seed)
    {
        var rng = new Random(seed);
        int n = rng.Next(0, 3000);
        var before = new byte[n];
        rng.NextBytes(before);
        var after = (byte[])before.Clone();
        // des écarts de toutes les longueurs autour de mergeGap
        for (int i = 0; i < n; i += rng.Next(1, 200))
        {
            int len = Math.Min(n - i, rng.Next(1, 40));
            for (int k = 0; k < len; k++) after[i + k] = (byte)rng.Next(256);
        }
        int gap = rng.Next(0, 130);
        int baseOffset = rng.Next(0, 5000);

        var expected = ReferenceDestructionEngine.ToRuns(baseOffset, before, after, gap);

        var file = new byte[baseOffset + n + rng.Next(0, 100)];
        before.CopyTo(file, baseOffset);
        using var buffer = FileBuffer.FromBytes("x", file);
        using var builder = buffer.BeginPass();
        // tranche par tranche, de tailles quelconques, vides comprises
        for (int at = 0; at < n;)
        {
            int slice = Math.Min(n - at, rng.Next(0, 300));
            builder.WriteDiff(baseOffset + at, before.AsSpan(at, slice), after.AsSpan(at, slice), gap);
            at += slice;
        }
        using var batch = builder.Build("diff");

        Assert.Equal(expected.Select(r => (long)r.Offset), batch.Runs().Select(r => r.Start));
        Assert.Equal(expected.Select(r => (long)r.Length), batch.Runs().Select(r => r.Length));
        Assert.Equal(expected.Count, batch.EditCount);
        Assert.Equal(expected.Sum(r => (long)r.Length), batch.BytesChanged);
        var result = (byte[])file.Clone();
        foreach (var r in expected) r.NewBytes.CopyTo(result, r.Offset);
        Assert.Equal(result, batch.After.ReadAll());
    }
}

/// <summary>Une suite aléatoire d'opérations, jouée sur le cœur et sur la référence.</summary>
internal sealed class OracleScenario(int seed)
{
    private readonly Random _rng = new(seed);
    private readonly List<string> _log = new();

    private FileBuffer _buffer = null!;
    private OperationJournal _journal = null!;
    private ReferenceFileBuffer _ref = null!;
    private ReferenceOperationJournal _refJournal = null!;
    private string? _directory;

    public void Run()
    {
        var data = OracleData.Make(_rng, out string shape);
        var options = new FileBufferOptions
        {
            ChunkShift = new[] { 12, 13, 20 }[_rng.Next(3)],
            PassBufferBytes = new[] { 100, 4096, 65536, 32 << 20 }[_rng.Next(4)],
            ReadWindowBytes = new[] { 4096, 1 << 20 }[_rng.Next(2)],
            Durable = false,
        };
        bool files = _rng.Next(4) == 0;
        _log.Add($"fichier de {data.Length} o, {shape}, {(files ? "sur disque" : "en mémoire")}, tranche 2^{options.ChunkShift}, " +
                 $"plafond {options.PassBufferBytes} o, fenêtre {options.ReadWindowBytes} o");

        if (files)
        {
            _directory = Directory.CreateTempSubdirectory("dgc-oracle-").FullName;
            string source = Path.Combine(_directory, "source.bin");
            File.WriteAllBytes(source, data);
            _buffer = FileBuffer.Open(source, _directory, options);
        }
        else
        {
            _buffer = FileBuffer.FromBytes("oracle.bin", (byte[])data.Clone(), options);
        }
        try
        {
            RunSteps(data);
        }
        finally
        {
            _buffer.Dispose();
            if (_directory is not null) Directory.Delete(_directory, recursive: true);
        }
    }

    private void RunSteps(byte[] data)
    {
        _journal = new OperationJournal(_buffer);
        _ref = ReferenceFileBuffer.FromBytes("oracle.bin", (byte[])data.Clone());
        _refJournal = new ReferenceOperationJournal(_ref);

        bool large = data.Length > 256 * 1024;
        int steps = large ? _rng.Next(1, 6) : _rng.Next(1, 30);

        Guard(-1, Check);
        for (int step = 0; step < steps; step++)
        {
            int s = step;
            Guard(s, () =>
            {
                int r = _rng.Next(100);
                if (r < 50) Pass(large);
                else if (r < 60) PostProcessedPass(large);
                else if (r < 75) Undo();
                else if (r < 90) Redo();
                else Reset();
                Check();
            });
        }
    }

    // ---------- opérations ----------

    private void Pass(bool large)
    {
        var options = RandomOptions(large);
        var (prod, reference, mask) = Build(options);
        _log.Add($"passe {Describe(options)}, masque {mask} : {reference.EditCount} écritures");
        AssertSameBatch(reference, prod);

        // une passe vide s'empile aussi : le journal ne la refuse pas
        if (reference.EditCount == 0 && _rng.Next(3) != 0)
        {
            prod.Dispose();
            return;
        }
        var info = _journal.Apply(prod);
        Assert.Equal(prod.Id, info.Id);
        _refJournal.Apply(reference);
    }

    /// <summary>
    /// Une passe reprise après coup, comme <c>ConfineToBand</c> dans DataBender :
    /// l'avant et l'après d'une étendue, découpés en plages par <c>ToRuns</c>.
    /// </summary>
    private void PostProcessedPass(bool large)
    {
        var options = RandomOptions(large);
        var (rawBatch, reference, mask) = Build(options);
        rawBatch.Dispose();
        if (reference.EditCount == 0) return;

        int length = _ref.Length;
        int lo = Math.Max(0, reference.FirstOffset - _rng.Next(0, 300));
        int hi = Math.Min(length, reference.LastOffset + _rng.Next(0, 300));
        var before = _ref.Current.Slice(lo, hi - lo).ToArray();
        var raw = (byte[])before.Clone();
        foreach (var e in reference.Edits) e.NewBytes.CopyTo(raw, e.Offset - lo);

        // le « filtre » : garde, rend ou déforme chaque octet détruit, et déborde un peu
        var after = (byte[])before.Clone();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != before[i])
            {
                int k = _rng.Next(4);
                after[i] = k == 0 ? before[i] : k == 1 ? (byte)_rng.Next(256) : raw[i];
            }
            else if (_rng.Next(200) == 0)
            {
                after[i] = (byte)_rng.Next(256);
            }
        }

        int gap = _rng.Next(0, 130);
        var refRuns = ReferenceDestructionEngine.ToRuns(lo, before, after, gap);
        string label = reference.Label + " · bande";
        _log.Add($"passe reprise {Describe(options)}, masque {mask}, [{lo}, {hi}), écart {gap} : {refRuns.Count} plages");

        // comme ConfineToBand au lot 2 : l'avant et l'après, tranche par tranche
        using var builder = _buffer.BeginPass();
        for (int at = 0; at < before.Length;)
        {
            int slice = Math.Min(before.Length - at, _rng.Next(1, 5000));
            builder.WriteDiff(lo + at, before.AsSpan(at, slice), after.AsSpan(at, slice), gap);
            at += slice;
        }
        var batch = builder.Build(label);
        var expected = new ReferenceDestructionBatch(label, refRuns);
        Assert.Equal(expected.EditCount, batch.EditCount);
        Assert.Equal(expected.BytesChanged, batch.BytesChanged);
        Assert.Equal(expected.FirstOffset, batch.FirstOffset);
        Assert.Equal(expected.LastOffset, batch.LastOffset);
        Assert.Equal(refRuns.Select(r => new ByteRange(r.Offset, r.Offset + r.Length)), batch.Runs());

        _journal.Apply(batch);
        _refJournal.Apply(expected);
    }

    private void Undo()
    {
        var expected = _refJournal.Undo();
        var actual = _journal.Undo();
        Assert.Equal(expected is null, actual is null);
        _log.Add($"annuler : {expected?.Label ?? "rien"}");
        Assert.Equal(expected?.Label, actual?.Label);
    }

    private void Redo()
    {
        var expected = _refJournal.Redo();
        var actual = _journal.Redo();
        Assert.Equal(expected is null, actual is null);
        _log.Add($"rétablir : {expected?.Label ?? "rien"}");
        Assert.Equal(expected?.Label, actual?.Label);
    }

    private void Reset()
    {
        _log.Add("retour à l'original");
        _refJournal.ResetAll();
        _journal.ResetAll();
    }

    // ---------- vérifications ----------

    private void Check()
    {
        Assert.Equal(_ref.Length, _buffer.Length);
        AssertBytes("original", _ref.Original, OracleRead.Original(_buffer));
        AssertBytes("état courant", _ref.Current, OracleRead.Current(_buffer));

        Assert.Equal(_refJournal.CanUndo, _journal.CanUndo);
        Assert.Equal(_refJournal.CanRedo, _journal.CanRedo);
        Assert.Equal(_refJournal.History.Count, _journal.History.Count);
        long lastId = 0;
        for (int i = 0; i < _refJournal.History.Count; i++)
        {
            var expected = _refJournal.History[i];
            var actual = _journal.History[i];
            Assert.Equal(expected.Label, actual.Label);
            Assert.Equal(expected.EditCount, actual.EditCount);
            Assert.Equal(expected.BytesChanged, actual.BytesChanged);
            Assert.Equal(expected.FirstOffset, actual.FirstOffset);
            Assert.Equal(expected.LastOffset, actual.LastOffset);
            // numérotées à l'application, dans l'ordre de l'historique
            Assert.True(actual.Id > lastId, $"passe {i} numérotée {actual.Id} après {lastId}");
            Assert.Null(actual.AppData);
            lastId = actual.Id;
        }

        CheckViews();
        CheckChangedBytes();
        if (_directory is not null)
            Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "tmp"))); // pas de passe abandonnée qui traîne
    }

    /// <summary>Les compteurs du journal contre une comparaison naïve, sur des plages quelconques.</summary>
    private void CheckChangedBytes()
    {
        var original = _ref.Original;
        var current = _ref.Current;
        for (int k = 0; k < 3; k++)
        {
            int start = _rng.Next(-5, _ref.Length + 5);
            int length = _rng.Next(4) == 0 ? _ref.Length : _rng.Next(-1, _ref.Length + 5);
            int lo = Math.Clamp(start, 0, _ref.Length), hi = Math.Clamp(start + Math.Max(0, length), 0, _ref.Length);
            long naive = 0;
            for (int i = lo; i < hi; i++) if (original[i] != current[i]) naive++;
            Assert.Equal(naive, _journal.ChangedBytes(start, length));
        }
    }

    /// <summary>Ce que les vues rendent d'autre qu'une lecture complète : une plage, un flux, la carte des différences.</summary>
    private void CheckViews()
    {
        var view = _buffer.Current;
        int length = _ref.Length;

        int offset = _rng.Next(0, length + 1);
        var part = new byte[_rng.Next(0, length - offset + 1)];
        view.Read(offset, part);
        AssertBytes("lecture d'une plage", _ref.Current.Slice(offset, part.Length), part);

        using (var stream = view.OpenRead())
        {
            stream.Position = offset;
            var streamed = new MemoryStream();
            var chunk = new byte[_rng.Next(1, 70_000)];
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0) streamed.Write(chunk, 0, n);
            AssertBytes("flux", _ref.Current[offset..], streamed.ToArray());
        }

        int buckets = _rng.Next(-1, 2000);
        int start = _rng.Next(-5, length + 5);
        int count = _rng.Next(4) == 0 ? -1 : _rng.Next(-1, length + 5);
        var expected = ReferenceDiffMap.Compute(_ref.Original, _ref.Current, buckets, start, count);
        Assert.Equal(expected, DiffMap.Compute(_buffer.Original, view, buckets, start, count));
        Assert.Equal(expected, DiffMap.Compute(_buffer, buckets, start, count));
    }

    private static void AssertBytes(string what, ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int at = expected.CommonPrefixLength(actual);
        if (at < expected.Length)
            throw new XunitException($"{what} : octet {at} différent (attendu {expected[at]}, obtenu {actual[at]}).");
    }

    /// <summary>
    /// La passe du cœur contre celle de la référence : mêmes comptes, même
    /// avant, et un après qui est l'avant plus les écritures de la référence.
    /// Ses plages couvrent toutes ces écritures, triées et disjointes.
    /// </summary>
    private void AssertSameBatch(ReferenceDestructionBatch expected, DestructionBatch actual)
    {
        Assert.Equal(expected.Label, actual.Label);
        Assert.Equal(expected.EditCount, actual.EditCount);
        Assert.Equal(expected.BytesChanged, actual.BytesChanged);
        Assert.Equal(expected.FirstOffset, actual.FirstOffset);
        Assert.Equal(expected.LastOffset, actual.LastOffset);

        AssertBytes("avant de la passe", _ref.Current, actual.Before.ReadAll());
        var after = _ref.Current.ToArray();
        foreach (var e in expected.Edits) e.NewBytes.CopyTo(after, e.Offset);
        AssertBytes("après de la passe", after, actual.After.ReadAll());

        var runs = actual.Runs().ToList();
        for (int i = 1; i < runs.Count; i++)
            Assert.True(runs[i].Start > runs[i - 1].End || (runs[i].Start == runs[i - 1].End && runs[i].Length > 0), "plages en désordre ou qui se recouvrent");
        int r = 0;
        foreach (var e in expected.Edits)
        {
            while (r < runs.Count && runs[r].End <= e.Offset) r++;
            Assert.True(r < runs.Count && runs[r].Start <= e.Offset && runs[r].End >= e.Offset + e.Length,
                $"l'écriture [{e.Offset}, {e.Offset + e.Length}) n'est dans aucune plage");
        }
    }

    private void Guard(int step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            throw new XunitException(
                $"Oracle : seed {seed}, pas {step}. Pour rejouer : DGC_ORACLE_SEED={seed} dotnet test --filter Oracle\n" +
                $"Derniers pas :\n  {string.Join("\n  ", _log.TakeLast(15))}\n\n{ex}");
        }
    }

    // ---------- tirages ----------

    private (DestructionBatch Prod, ReferenceDestructionBatch Reference, string Mask) Build(DestructionOptions options)
    {
        int length = _ref.Length;
        switch (_rng.Next(6))
        {
            case 0:
            {
                // la surcharge historique, à plancher unique
                int? payload = _rng.Next(3) == 0 ? null : _rng.Next(-10, length + 10);
                var prod = DestructionEngine.BuildBatch(_buffer, options, payload is int p ? new HeaderInfo(p) : null);
                var reference = ReferenceDestructionEngine.BuildBatch(_ref, options, payload is int q ? new ReferenceHeaderInfo(q) : null);
                return (prod, reference, payload is null ? "en-tête absent" : $"en-tête {payload}");
            }
            case 1:
                return (DestructionEngine.BuildBatch(_buffer, options, DestructionMask.WholeBuffer),
                        ReferenceDestructionEngine.BuildBatch(_ref, options, ReferenceDestructionMask.WholeBuffer),
                        "tout");
            case 2:
            {
                int p = _rng.Next(-10, length + 10);
                return (DestructionEngine.BuildBatch(_buffer, options, DestructionMask.FromPayloadStart(p)),
                        ReferenceDestructionEngine.BuildBatch(_ref, options, ReferenceDestructionMask.FromPayloadStart(p)),
                        $"plancher {p}");
            }
            case 3:
            {
                var regions = RawRegions(length);
                return (DestructionEngine.BuildBatch(_buffer, options, DestructionMask.FromRegions(regions.Select(r => new ByteRange(r.Start, r.End)))),
                        ReferenceDestructionEngine.BuildBatch(_ref, options, ReferenceDestructionMask.FromRegions(regions)),
                        $"{regions.Count} régions en vrac");
            }
            default:
            {
                var regions = AviLikeRegions(length);
                return (DestructionEngine.BuildBatch(_buffer, options, DestructionMask.FromRegions(regions.Select(r => new ByteRange(r.Start, r.End)))),
                        ReferenceDestructionEngine.BuildBatch(_ref, options, ReferenceDestructionMask.FromRegions(regions)),
                        $"{regions.Count} frames façon AVI");
            }
        }
    }

    /// <summary>Des régions quelconques : désordonnées, qui se chevauchent, vides, inversées, hors du fichier.</summary>
    private List<ReferenceByteRange> RawRegions(int length)
    {
        var regions = new List<ReferenceByteRange>();
        int count = _rng.Next(0, 12);
        for (int i = 0; i < count; i++)
        {
            int a = _rng.Next(-20, length + 20);
            int b = _rng.Next(3) == 0 ? a + _rng.Next(-5, 6) : _rng.Next(-20, length + 20);
            regions.Add(new ReferenceByteRange(a, b));
        }
        return regions;
    }

    /// <summary>
    /// Un masque troué comme celui d'un AVI : des chunks avec un en-tête de 8
    /// octets, un préfixe protégé, la charge utile destructible, un octet de
    /// bourrage quand la taille est impaire. Une partie des frames est écartée,
    /// comme avec la portée « I-frames seulement ».
    /// </summary>
    private List<ReferenceByteRange> AviLikeRegions(int length)
    {
        var regions = new List<ReferenceByteRange>();
        int maxPayload = Math.Max(1, Math.Min(4000, length / 4));
        bool sparse = _rng.Next(3) == 0;
        long pos = _rng.Next(0, Math.Min(300, length + 1));
        while (pos < length)
        {
            int payload = _rng.Next(0, maxPayload + 1);
            int guard = _rng.Next(0, Math.Min(payload, 40) + 1);
            long start = pos + 8 + guard;
            long end = pos + 8 + payload;
            if (end > start && (!sparse || _rng.Next(4) == 0))
                regions.Add(new ReferenceByteRange((int)Math.Min(start, int.MaxValue), (int)Math.Min(end, int.MaxValue)));
            pos += 8 + payload + (payload & 1);
        }
        return regions;
    }

    private DestructionOptions RandomOptions(bool large)
    {
        double[] densities = [0, 0.001, 0.02, 0.15, 0.5, 0.9, 1.0, _rng.NextDouble()];
        int[] blocks = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 31, 64, 65, 100, 256, 512, 1000, 4096, _rng.Next(1, 300)];
        var options = new DestructionOptions
        {
            Operator = (DestructionOperator)_rng.Next(7),
            Density = densities[_rng.Next(densities.Length)],
            BlockSize = blocks[_rng.Next(blocks.Length)],
            Seed = _rng.Next(int.MinValue, int.MaxValue),
            ProtectHeader = _rng.Next(2) == 0,
            FixedValue = (byte)_rng.Next(256),
            XorMask = (byte)_rng.Next(256),
            BitRotate = _rng.Next(-2, 10),
            MaxShiftBlocks = _rng.Next(-1, 70),
        };
        // sur un gros fichier, des blocs d'1 octet à forte densité ne prouvent rien de plus et coûtent cher
        if (large) options.BlockSize = Math.Max(options.BlockSize, 16);

        switch (_rng.Next(10))
        {
            case <= 4:
                (options.RegionStart, options.RegionEnd) = (0, 1);
                break;
            case <= 6:
            {
                double a = _rng.NextDouble(), b = _rng.NextDouble();
                (options.RegionStart, options.RegionEnd) = (Math.Min(a, b), Math.Max(a, b));
                break;
            }
            case 7:
            {
                double a = _rng.NextDouble(), b = _rng.NextDouble();
                (options.RegionStart, options.RegionEnd) = (Math.Max(a, b), Math.Min(a, b));
                break;
            }
            case 8:
                (options.RegionStart, options.RegionEnd) = (_rng.NextDouble() * 1.4 - 0.2, _rng.NextDouble() * 1.4 - 0.2);
                break;
            default:
            {
                double a = _rng.NextDouble();
                (options.RegionStart, options.RegionEnd) = (a, a);
                break;
            }
        }
        return options;
    }

    private static string Describe(DestructionOptions o) =>
        $"{o.Operator} d={o.Density:0.###} b={o.BlockSize} seed={o.Seed} zone=[{o.RegionStart:0.###}, {o.RegionEnd:0.###}] " +
        $"protège={o.ProtectHeader} fixe={o.FixedValue} xor={o.XorMask} rot={o.BitRotate} écho={o.MaxShiftBlocks}";
}

/// <summary>Les fichiers de départ : tailles et contenus variés, pour que chaque opérateur ait des blocs qu'il ne change pas.</summary>
internal static class OracleData
{
    public static byte[] Make(Random rng, out string shape)
    {
        int r = rng.Next(100);
        int size = r < 5 ? rng.Next(0, 17)
            : r < 70 ? rng.Next(17, 4097)
            : r < 95 ? rng.Next(4097, 80_000)
            : rng.Next(1_048_000, 1_200_000); // au-delà d'une fenêtre d'1 Mio

        var data = new byte[size];
        switch (rng.Next(5))
        {
            case 0:
                shape = "aléatoire";
                rng.NextBytes(data);
                break;
            case 1:
                shape = "nul";
                break;
            case 2:
                shape = "rampe";
                for (int i = 0; i < size; i++) data[i] = (byte)i;
                break;
            case 3:
            {
                shape = "motif";
                var motif = new byte[rng.Next(1, 9)];
                rng.NextBytes(motif);
                for (int i = 0; i < size; i++) data[i] = motif[i % motif.Length];
                break;
            }
            default:
                shape = "aléatoire troué de zéros";
                rng.NextBytes(data);
                for (int i = 0; i < size; i += rng.Next(1, 500))
                    data.AsSpan(i, Math.Min(size - i, rng.Next(1, 200))).Clear();
                break;
        }
        return data;
    }
}

/// <summary>Comment l'oracle lit le cœur : un seul endroit à changer quand les lectures changent.</summary>
internal static class OracleRead
{
    public static byte[] Original(FileBuffer buffer) => buffer.Original.ReadAll();

    public static byte[] Current(FileBuffer buffer) => buffer.Current.ReadAll();
}
