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
