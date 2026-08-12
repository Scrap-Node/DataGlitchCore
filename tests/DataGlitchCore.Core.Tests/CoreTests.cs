using DataGlitchCore.Core;
using Xunit;

namespace DataGlitchCore.Core.Tests;

public class CoreTests
{
    // ---- FileBuffer ----

    [Fact]
    public void FileBuffer_Truncates_At_Cap()
    {
        var data = new byte[1000];
        var buf = FileBuffer.Load("x.bin", new MemoryStream(data), maxBytes: 256);
        Assert.True(buf.Truncated);
        Assert.Equal(256, buf.Length);
    }

    [Fact]
    public void FileBuffer_Keeps_Original_Untouched()
    {
        var buf = FileBuffer.FromBytes("x.bin", new byte[] { 1, 2, 3, 4 });
        buf.Write(1, new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf.Original.ToArray());
        Assert.Equal(new byte[] { 1, 9, 9, 4 }, buf.Current.ToArray());
    }

    [Fact]
    public void FileBuffer_Clamps_An_Oversized_Cap_Instead_Of_Falling_Back()
    {
        // Un cap au-delà du représentable doit être ramené au plafond, PAS
        // faire retomber sur le défaut : l'ancienne version renvoyait
        // silencieusement à 200 Mo, ce qui aurait tronqué un fichier de 2 Go
        // sans le moindre signe dès qu'un réglage laisse monter le cap.
        var data = new byte[1000];
        var buf = FileBuffer.Load("x.bin", new MemoryStream(data), maxBytes: long.MaxValue);
        Assert.False(buf.Truncated);
        Assert.Equal(1000, buf.Length);
    }

    [Fact]
    public void FileBuffer_Sizes_Exactly_When_The_Length_Is_Known()
    {
        // Flux non seekable (cas d'un upload navigateur) : sans expectedLength
        // il faut passer par le chemin à tampon croissant, avec il alloue juste.
        // Les deux doivent produire le même contenu — c'est tout ce que
        // l'appelant a le droit d'observer.
        var data = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();

        var blind = FileBuffer.Load("x.bin", new NonSeekableStream(data), maxBytes: 4096);
        var sized = FileBuffer.Load("x.bin", new NonSeekableStream(data), maxBytes: 4096, expectedLength: data.Length);

        Assert.Equal(data, blind.Current.ToArray());
        Assert.Equal(data, sized.Current.ToArray());
        Assert.False(sized.Truncated);
    }

    [Fact]
    public void FileBuffer_Truncates_On_The_Sized_Path_Too()
    {
        var data = new byte[1000];
        var buf = FileBuffer.Load("x.bin", new NonSeekableStream(data), maxBytes: 256, expectedLength: data.Length);
        Assert.True(buf.Truncated);
        Assert.Equal(256, buf.Length);
    }

    /// <summary>Flux qui refuse de dire sa taille — ce que fait le flux d'un
    /// InputFile Blazor, et donc le cas qui décide du chemin de lecture.</summary>
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    // ---- Journal ----

    [Fact]
    public void Journal_Apply_Undo_Redo_RoundTrips()
    {
        var buf = FileBuffer.FromBytes("x.bin", new byte[] { 10, 20, 30, 40, 50 });
        var journal = new OperationJournal(buf);
        var batch = new DestructionBatch("t", new[]
        {
            new ByteEdit(1, new byte[] { 20 }, new byte[] { 200 }),
            new ByteEdit(3, new byte[] { 40 }, new byte[] { 240 }),
        });

        journal.Apply(batch);
        Assert.Equal(new byte[] { 10, 200, 30, 240, 50 }, buf.Current.ToArray());

        journal.Undo();
        Assert.Equal(new byte[] { 10, 20, 30, 40, 50 }, buf.Current.ToArray());
        Assert.True(journal.CanRedo);

        journal.Redo();
        Assert.Equal(new byte[] { 10, 200, 30, 240, 50 }, buf.Current.ToArray());
    }

    // ---- Engine ----

    [Fact]
    public void Engine_Is_Reproducible_With_Seed()
    {
        var opts = new DestructionOptions { Seed = 42, Density = 0.5, BlockSize = 4, Operator = DestructionOperator.Xor };

        var a = FileBuffer.FromBytes("x", Enumerable.Range(0, 400).Select(i => (byte)i).ToArray());
        var b = FileBuffer.FromBytes("x", Enumerable.Range(0, 400).Select(i => (byte)i).ToArray());

        var batchA = DestructionEngine.BuildBatch(a, opts);
        var batchB = DestructionEngine.BuildBatch(b, opts);

        Assert.Equal(batchA.EditCount, batchB.EditCount);
        Assert.True(batchA.EditCount > 0);
        for (int i = 0; i < batchA.EditCount; i++)
        {
            Assert.Equal(batchA.Edits[i].Offset, batchB.Edits[i].Offset);
            Assert.Equal(batchA.Edits[i].NewBytes, batchB.Edits[i].NewBytes);
        }
    }

    [Fact]
    public void Engine_ProtectHeader_Skips_Payload_Start()
    {
        // Contrairement à DataBender.Core.Tests (qui construit un vrai WAV via
        // MediaProbe), DataGlitchCore ne connaît aucun format : le HeaderInfo
        // se construit directement, avec le PayloadStart de son choix.
        var buf = FileBuffer.FromBytes("x", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var header = new HeaderInfo(PayloadStart: 44);

        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 8, ProtectHeader = true };
        var batch = DestructionEngine.BuildBatch(buf, opts, header);

        Assert.NotEmpty(batch.Edits);
        Assert.All(batch.Edits, e => Assert.True(e.Offset >= header.PayloadStart));
    }

    [Fact]
    public void Engine_ProtectHeader_False_Allows_Editing_Before_PayloadStart()
    {
        var buf = FileBuffer.FromBytes("x", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var header = new HeaderInfo(PayloadStart: 44);

        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 8, ProtectHeader = false };
        var batch = DestructionEngine.BuildBatch(buf, opts, header);

        Assert.Contains(batch.Edits, e => e.Offset < header.PayloadStart);
    }

    [Fact]
    public void Engine_Xor_Then_Xor_Restores_Bytes()
    {
        var buf = FileBuffer.FromBytes("x", Enumerable.Range(0, 128).Select(i => (byte)i).ToArray());
        var journal = new OperationJournal(buf);
        var opts = new DestructionOptions { Seed = 7, Density = 1.0, BlockSize = 8, Operator = DestructionOperator.Xor, XorMask = 0xAA };

        journal.Apply(DestructionEngine.BuildBatch(buf, opts));
        Assert.NotEqual(buf.Original.ToArray(), buf.Current.ToArray());
        journal.Apply(DestructionEngine.BuildBatch(buf, opts)); // XOR involutif
        Assert.Equal(buf.Original.ToArray(), buf.Current.ToArray());
    }

    // ---- DestructionMask (protection multi-régions, pour l'AVI) ----

    [Fact]
    public void Engine_With_No_Mask_Is_Identical_To_The_Legacy_Single_Range()
    {
        // La surcharge à HeaderInfo n'est plus qu'un adaptateur vers la
        // surcharge à masque — ce test épingle que l'un et l'autre restent
        // rigoureusement identiques, pas seulement "compatibles".
        var buf = FileBuffer.FromBytes("x", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var header = new HeaderInfo(PayloadStart: 44);
        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 8, Operator = DestructionOperator.Xor, ProtectHeader = true };

        var viaHeader = DestructionEngine.BuildBatch(buf, opts, header);
        var viaMask = DestructionEngine.BuildBatch(buf, opts, DestructionMask.FromPayloadStart(header.PayloadStart));

        Assert.Equal(viaHeader.EditCount, viaMask.EditCount);
        Assert.True(viaHeader.EditCount > 0);
        for (int i = 0; i < viaHeader.EditCount; i++)
        {
            Assert.Equal(viaHeader.Edits[i].Offset, viaMask.Edits[i].Offset);
            Assert.Equal(viaHeader.Edits[i].NewBytes, viaMask.Edits[i].NewBytes);
        }
    }

    [Fact]
    public void Engine_Multi_Region_Only_Edits_Inside_Regions()
    {
        var buf = FileBuffer.FromBytes("x", new byte[1024]);
        var regions = new[] { new ByteRange(0, 100), new ByteRange(300, 350), new ByteRange(900, 1024) };
        var mask = DestructionMask.FromRegions(regions);
        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 16, Operator = DestructionOperator.Fixed, FixedValue = 0xAA };

        var batch = DestructionEngine.BuildBatch(buf, opts, mask);

        Assert.NotEmpty(batch.Edits);
        Assert.All(batch.Edits, e => Assert.Contains(regions, r => e.Offset >= r.Start && e.Offset + e.Length <= r.End));
    }

    [Fact]
    public void Engine_Multi_Region_Leaves_Every_Protected_Byte_Untouched()
    {
        var original = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        var buf = FileBuffer.FromBytes("x", (byte[])original.Clone());
        var regions = new[] { new ByteRange(0, 100), new ByteRange(300, 350), new ByteRange(900, 1024) };
        var mask = DestructionMask.FromRegions(regions);
        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 16, Operator = DestructionOperator.Fixed, FixedValue = 0xAA };
        var journal = new OperationJournal(buf);

        journal.Apply(DestructionEngine.BuildBatch(buf, opts, mask));

        var current = buf.Current.ToArray();
        for (int i = 0; i < original.Length; i++)
        {
            bool inRegion = regions.Any(r => i >= r.Start && i < r.End);
            if (!inRegion) Assert.Equal(original[i], current[i]);
        }
    }

    [Fact]
    public void Engine_ShiftBlock_Never_Reads_Outside_The_Destroyable_Regions()
    {
        // Un trou protégé rempli d'une sentinelle IMPAIRE, et un contenu
        // destructible TOUJOURS PAIR ailleurs : si l'écho de ShiftBlock (qui
        // lit "plus tôt") fuit hors des fenêtres, un octet impair apparaît
        // dans un NewBytes — sans ambiguïté possible avec du contenu
        // légitime, contrairement à un buffer uniformément nul (où l'écho ne
        // changerait jamais rien, et où une sentinelle "normale" pourrait
        // aussi apparaître par coïncidence dans le contenu réel).
        var data = new byte[400];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)((i * 2) % 256);
        for (int i = 100; i < 200; i++) data[i] = 0x5B; // trou protégé
        var buf = FileBuffer.FromBytes("x", data);
        var mask = DestructionMask.FromRegions([new ByteRange(0, 100), new ByteRange(200, 400)]);
        var opts = new DestructionOptions
        {
            Seed = 3, Density = 1.0, BlockSize = 8, Operator = DestructionOperator.ShiftBlock, MaxShiftBlocks = 16,
        };

        var batch = DestructionEngine.BuildBatch(buf, opts, mask);

        Assert.NotEmpty(batch.Edits);
        Assert.All(batch.Edits, e => Assert.All(e.NewBytes, b => Assert.Equal(0, b % 2)));
    }

    [Fact]
    public void Engine_Records_No_Edit_When_The_Block_Is_Unchanged()
    {
        // Une seule fenêtre = un seul bloc : l'écho de ShiftBlock n'a nulle
        // part où lire "plus tôt" que lui-même, donc ne change rien.
        var buf = FileBuffer.FromBytes("x", Enumerable.Range(0, 8).Select(i => (byte)i).ToArray());
        var mask = DestructionMask.FromRegions([new ByteRange(0, 8)]);
        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 8, Operator = DestructionOperator.ShiftBlock, MaxShiftBlocks = 4 };

        var batch = DestructionEngine.BuildBatch(buf, opts, mask);

        Assert.Empty(batch.Edits);
    }

    [Fact]
    public void Mask_FromRegions_Normalises_Overlaps_And_Order()
    {
        var mask = DestructionMask.FromRegions(
        [
            new ByteRange(50, 100),
            new ByteRange(0, 20),
            new ByteRange(90, 120),   // chevauche la première
            new ByteRange(120, 130),  // adjacente à la précédente, une fois fusionnée
            new ByteRange(200, 190),  // inversée : ignorée
            new ByteRange(300, 300),  // vide : ignorée
        ]);

        Assert.Equal([new ByteRange(0, 20), new ByteRange(50, 130)], mask.Regions);
    }

    [Fact]
    public void Engine_ProtectHeader_Beyond_The_Targeted_Zone_Edits_Nothing()
    {
        // Bug latent de l'ancien modèle à un seul plancher : un PayloadStart
        // situé APRÈS la zone ciblée par RegionEnd faisait échanger start/end
        // et la destruction tombait alors À L'INTÉRIEUR du header prétendument
        // protégé, au lieu de ne rien toucher.
        var buf = FileBuffer.FromBytes("x", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var header = new HeaderInfo(PayloadStart: 200);
        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 8, ProtectHeader = true, RegionStart = 0.0, RegionEnd = 0.5 };

        var batch = DestructionEngine.BuildBatch(buf, opts, header);

        Assert.Empty(batch.Edits);
    }

    [Fact]
    public void Engine_ProtectHeader_Past_The_End_Of_The_Buffer_Edits_Nothing()
    {
        // Bug latent de l'ancien modèle : un PayloadStart au-delà de la
        // taille du buffer faisait lever buffer.Read (offset hors bornes)
        // au lieu de ne simplement rien avoir à détruire.
        var buf = FileBuffer.FromBytes("x", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var header = new HeaderInfo(PayloadStart: 9999);
        var opts = new DestructionOptions { Seed = 1, Density = 1.0, BlockSize = 8, ProtectHeader = true };

        var batch = DestructionEngine.BuildBatch(buf, opts, header);

        Assert.Empty(batch.Edits);
    }

    // ---- ToRuns (post-traitement public, ex-privé sous DataBender) ----

    [Fact]
    public void ToRuns_Finds_A_Single_Changed_Range()
    {
        var before = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var after = (byte[])before.Clone();
        after[3] = 99; after[4] = 98;

        var runs = DestructionEngine.ToRuns(baseOffset: 100, before, after);

        var run = Assert.Single(runs);
        Assert.Equal(103, run.Offset);
        Assert.Equal(new byte[] { 4, 5 }, run.OldBytes);
        Assert.Equal(new byte[] { 99, 98 }, run.NewBytes);
    }

    [Fact]
    public void ToRuns_Merges_Ranges_Separated_By_A_Small_Gap()
    {
        var before = new byte[20];
        var after = (byte[])before.Clone();
        after[0] = 1;
        after[5] = 1; // écart de 4 octets identiques : sous mergeGap, doit fusionner

        var runs = DestructionEngine.ToRuns(0, before, after, mergeGap: 8);

        Assert.Single(runs);
    }

    [Fact]
    public void ToRuns_Is_Empty_When_Buffers_Are_Identical()
    {
        var data = new byte[32];
        Assert.Empty(DestructionEngine.ToRuns(0, data, (byte[])data.Clone()));
    }

    // ---- DiffMap ----

    [Fact]
    public void DiffMap_Reports_Exact_Changed_Fraction()
    {
        var original = new byte[100];
        var current = new byte[100];
        // 1re moitié entièrement modifiée, 2e moitié intacte
        for (int i = 0; i < 50; i++) current[i] = 0xFF;

        var diff = DiffMap.Compute(original, current, buckets: 2);

        Assert.Equal(1f, diff[0]);
        Assert.Equal(0f, diff[1]);
    }

    [Fact]
    public void DiffMap_Is_Zero_When_Identical()
    {
        var data = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var diff = DiffMap.Compute(data, (byte[])data.Clone(), buckets: 8);
        Assert.All(diff, d => Assert.Equal(0f, d));
    }

    // ---- Zoom temporel (ViewWindow) ----

    [Fact]
    public void View_Zoom_Keeps_The_Anchor_Under_The_Cursor()
    {
        var v = ViewWindow.Full;
        // on zoome trois fois autour de 0,3 : ce point doit rester à la même
        // position relative dans la fenêtre à chaque étape
        for (int i = 0; i < 3; i++)
        {
            double before = v.ToView(0.3);
            v = v.ZoomAt(0.3, 2.0);
            Assert.Equal(before, v.ToView(0.3), precision: 6);
        }
        Assert.Equal(0.125, v.Span, precision: 6);
    }

    [Fact]
    public void View_Zoom_Near_An_Edge_Stays_Inside_The_File()
    {
        // ancre collée au début : la fenêtre ne doit pas partir en négatif
        var v = ViewWindow.Full.ZoomAt(0.0, 8.0);
        Assert.Equal(0.0, v.Start, precision: 9);
        Assert.Equal(0.125, v.End, precision: 6);

        // et symétriquement à la fin
        var w = ViewWindow.Full.ZoomAt(1.0, 8.0);
        Assert.Equal(1.0, w.End, precision: 9);
        Assert.InRange(w.Start, 0, 1);
    }

    [Fact]
    public void View_Never_Zooms_Past_The_Minimum_Span()
    {
        var v = ViewWindow.Full;
        for (int i = 0; i < 60; i++) v = v.ZoomAt(0.5, 2.0);
        Assert.True(v.Span >= ViewWindow.DefaultMinSpan);
        Assert.InRange(v.Start, 0, 1);
        Assert.InRange(v.End, 0, 1);
    }

    [Fact]
    public void View_Zoom_Out_Returns_To_The_Whole_File()
    {
        var v = ViewWindow.Full.ZoomAt(0.42, 16.0);
        for (int i = 0; i < 10; i++) v = v.ZoomAt(0.42, 0.5);
        Assert.True(v.IsFull);
        Assert.Equal(0.0, v.Start, precision: 9);
        Assert.Equal(1.0, v.End, precision: 9);
    }

    [Fact]
    public void View_Pan_Is_Clamped_To_The_File()
    {
        var v = new ViewWindow(0.4, 0.6);
        Assert.Equal(0.0, v.PanBy(-10).Start, precision: 9);
        Assert.Equal(0.2, v.PanBy(-10).Span, precision: 6);   // l'échelle est conservée
        Assert.Equal(1.0, v.PanBy(10).End, precision: 9);
        Assert.Equal(0.2, v.PanBy(10).Span, precision: 6);
    }

    [Fact]
    public void View_Maps_Coordinates_Both_Ways()
    {
        var v = new ViewWindow(0.25, 0.75);
        Assert.Equal(0.0, v.ToView(0.25), precision: 9);
        Assert.Equal(1.0, v.ToView(0.75), precision: 9);
        Assert.Equal(0.5, v.ToView(0.5), precision: 9);
        // aller-retour
        foreach (var f in new[] { 0.0, 0.3, 0.5, 0.9, 1.0 })
            Assert.Equal(f, v.ToView(v.ToFile(f)), precision: 9);
    }

    [Fact]
    public void View_Normalises_Inverted_Bounds()
    {
        var v = new ViewWindow(0.8, 0.2);
        Assert.Equal(0.2, v.Start, precision: 9);
        Assert.Equal(0.8, v.End, precision: 9);
    }
}
