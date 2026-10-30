using DataGlitchCore.Core;
using Xunit;

namespace DataGlitchCore.Core.Tests;

public class SequenceScriptTests
{
    // ---- historique et plan ----

    [Fact]
    public void Apply_Adds_To_The_Effective_Plan()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(5));

        Assert.True(script.EffectivePlan().Units.TryGetValue(5, out var op));
        Assert.Equal(new DeleteUnit(5), op);
    }

    [Fact]
    public void RepeatUnit_With_Times_Below_Two_Is_Rejected()
    {
        var script = new SequenceScript();
        Assert.Throws<ArgumentOutOfRangeException>(() => script.Apply(new RepeatUnit(3, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => script.Apply(new RepeatUnit(3, 0)));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)] // avant l'unité 0
    [InlineData(0, -1, 1, 1)] // plage qui commence avant 0
    [InlineData(0, 0, 0, 1)]  // plage vide
    [InlineData(0, 0, 1, 0)]  // collé zéro fois
    public void InsertUnits_Out_Of_Its_Domain_Is_Rejected(int after, int first, int count, int times)
    {
        var script = new SequenceScript();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            script.Apply(new InsertUnits(after, first, count, times, SkipKeyUnits: false)));
        Assert.Empty(script.Applied);
    }

    [Fact]
    public void Later_Operation_On_The_Same_Unit_Wins_In_The_Effective_Plan()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(5));
        script.Apply(new RepeatUnit(5, 3)); // change d'avis : répéter plutôt que supprimer

        Assert.Equal(new RepeatUnit(5, 3), script.EffectivePlan().Units[5]);
    }

    [Fact]
    public void ClearUnit_Removes_The_Unit_From_The_Effective_Plan()
    {
        // Un « clic bascule » : supprimer, puis recliquer la même unité.
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(5));
        script.Apply(new ClearUnit(5));

        Assert.False(script.EffectivePlan().Units.ContainsKey(5));
    }

    [Fact]
    public void Undo_Reverts_The_Last_Operation_Regardless_Of_Which_Unit_It_Touched()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(1));
        script.Apply(new RepeatUnit(9, 4));

        script.Undo();

        var plan = script.EffectivePlan();
        Assert.True(plan.Units.ContainsKey(1));
        Assert.False(plan.Units.ContainsKey(9));
    }

    [Fact]
    public void Redo_Reapplies_What_Undo_Reverted()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(1));
        script.Undo();
        script.Redo();

        Assert.True(script.EffectivePlan().Units.ContainsKey(1));
        Assert.False(script.CanRedo);
    }

    [Fact]
    public void Applying_After_An_Undo_Clears_The_Redo_Stack()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(1));
        script.Undo();
        script.Apply(new DeleteUnit(2)); // nouvelle branche : refaire la suppression de 1 n'a plus de sens

        Assert.False(script.CanRedo);
    }

    [Fact]
    public void Undo_And_Redo_On_An_Empty_Script_Are_No_Ops()
    {
        var script = new SequenceScript();
        script.Undo();
        script.Redo();

        Assert.False(script.CanUndo);
        Assert.False(script.CanRedo);
        Assert.Empty(script.Applied);
    }

    [Fact]
    public void ResetAll_Clears_Everything()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(1));
        script.Apply(new InsertUnits(2, 5, 3, 1, SkipKeyUnits: false));
        script.Undo();

        script.ResetAll();

        Assert.Empty(script.Applied);
        Assert.False(script.CanUndo);
        Assert.False(script.CanRedo);
        Assert.Empty(script.EffectivePlan().Units);
        Assert.Empty(script.EffectivePlan().Insertions);
    }

    // ---- collages ----

    [Fact]
    public void Insertions_After_The_Same_Unit_Chain_In_The_Order_They_Were_Applied()
    {
        var script = new SequenceScript();
        var first = new InsertUnits(2, 7, 2, 1, SkipKeyUnits: false);
        var second = new InsertUnits(2, 4, 1, 3, SkipKeyUnits: false);
        script.Apply(first);
        script.Apply(second);

        Assert.Equal([first, second], script.EffectivePlan().Insertions[2]);
    }

    [Fact]
    public void ClearInsertions_Removes_Every_Paste_After_A_Unit_But_Not_Its_Own_Edit()
    {
        var script = new SequenceScript();
        script.Apply(new RepeatUnit(2, 3));
        script.Apply(new InsertUnits(2, 7, 2, 1, SkipKeyUnits: false));
        script.Apply(new InsertUnits(2, 4, 1, 1, SkipKeyUnits: false));
        script.Apply(new InsertUnits(5, 0, 1, 1, SkipKeyUnits: false));

        script.Apply(new ClearInsertions(2));

        var plan = script.EffectivePlan();
        Assert.False(plan.Insertions.ContainsKey(2));
        Assert.True(plan.Insertions.ContainsKey(5));
        Assert.Equal(new RepeatUnit(2, 3), plan.Units[2]);
    }

    [Fact]
    public void ClearUnit_Leaves_The_Pastes_After_The_Unit_In_Place()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(2));
        script.Apply(new InsertUnits(2, 7, 2, 1, SkipKeyUnits: false));

        script.Apply(new ClearUnit(2));

        Assert.True(script.EffectivePlan().Insertions.ContainsKey(2));
    }

    [Fact]
    public void Two_Different_Pastes_Have_Different_Hashes()
    {
        // Les champs d'InsertUnits sont des scalaires : avec une liste, le texte
        // de l'opération ne montrerait que le nom de son type, et ces deux
        // scripts partageraient la même clé de cache.
        var a = new SequenceScript();
        a.Apply(new InsertUnits(2, 7, 2, 1, SkipKeyUnits: false));
        var b = new SequenceScript();
        b.Apply(new InsertUnits(2, 7, 2, 1, SkipKeyUnits: true));
        var c = new SequenceScript();
        c.Apply(new InsertUnits(2, 7, 2, 1, SkipKeyUnits: false));

        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
        Assert.Equal(a.ComputeHash(), c.ComputeHash());
    }

    // ---- déroulé ----

    [Fact]
    public void Expand_On_An_Empty_Script_Is_The_Identity()
    {
        var output = new SequenceScript().EffectivePlan().Expand(4);

        Assert.Equal([0, 1, 2, 3], output.Slots.Select(s => s.Source));
        Assert.Equal([0, 1, 2, 3], output.Slots.Select(s => s.Position));
        Assert.DoesNotContain(output.Slots, s => s.Inserted);
    }

    [Fact]
    public void Expand_Reflects_Deletions_And_Repetitions()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(1));
        script.Apply(new RepeatUnit(2, 3));

        var output = script.EffectivePlan().Expand(6);

        Assert.Equal([0, 2, 2, 2, 3, 4, 5], output.Slots.Select(s => s.Source));
    }

    [Fact]
    public void Expand_Plays_A_Paste_After_Every_Occurrence_Of_Its_Unit()
    {
        var script = new SequenceScript();
        script.Apply(new RepeatUnit(1, 2));
        script.Apply(new InsertUnits(1, 4, 2, 2, SkipKeyUnits: false)); // 4,5 deux fois après 1

        var output = script.EffectivePlan().Expand(6);

        Assert.Equal([0, 1, 1, 4, 5, 4, 5, 2, 3, 4, 5], output.Slots.Select(s => s.Source));
        Assert.Equal([0, 1, 1, 1, 1, 1, 1, 2, 3, 4, 5], output.Slots.Select(s => s.Position));
        Assert.Equal(
            [false, false, false, true, true, true, true, false, false, false, false],
            output.Slots.Select(s => s.Inserted));
    }

    [Fact]
    public void Expand_Keeps_A_Paste_After_A_Deleted_Unit_In_Its_Place()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(2));
        script.Apply(new InsertUnits(2, 0, 1, 1, SkipKeyUnits: false));

        var output = script.EffectivePlan().Expand(4);

        Assert.Equal([0, 1, 0, 3], output.Slots.Select(s => s.Source));
        Assert.Equal([0, 1, 2, 3], output.Slots.Select(s => s.Position));
    }

    [Fact]
    public void Expand_Can_Leave_The_Key_Units_Of_A_Paste_Aside()
    {
        // Unités clés en 0 et 3, comme les I-frames d'un GOP de 3
        Func<int, bool> isKey = u => u % 3 == 0;
        var script = new SequenceScript();
        script.Apply(new InsertUnits(5, 2, 4, 1, SkipKeyUnits: true)); // 2,3,4,5 sans 3

        var output = script.EffectivePlan().Expand(6, isKey);

        Assert.Equal([0, 1, 2, 3, 4, 5, 2, 4, 5], output.Slots.Select(s => s.Source));
        Assert.Equal(3, script.EffectivePlan().Insertions[5][0].SlotCount(isKey));
    }

    [Fact]
    public void Expand_Keeps_Key_Units_When_The_Paste_Does_Not_Skip_Them()
    {
        var script = new SequenceScript();
        script.Apply(new InsertUnits(5, 2, 4, 1, SkipKeyUnits: false));

        var output = script.EffectivePlan().Expand(6, u => u % 3 == 0);

        Assert.Equal([0, 1, 2, 3, 4, 5, 2, 3, 4, 5], output.Slots.Select(s => s.Source));
    }

    [Fact]
    public void Expand_Rejects_A_Plan_Made_For_A_Longer_Sequence()
    {
        var pasteBeyond = new SequenceScript();
        pasteBeyond.Apply(new InsertUnits(0, 3, 2, 1, SkipKeyUnits: false)); // copie 3 et 4
        var anchoredBeyond = new SequenceScript();
        anchoredBeyond.Apply(new InsertUnits(4, 0, 1, 1, SkipKeyUnits: false));
        var editBeyond = new SequenceScript();
        editBeyond.Apply(new DeleteUnit(4));

        Assert.Throws<ArgumentOutOfRangeException>(() => pasteBeyond.EffectivePlan().Expand(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => anchoredBeyond.EffectivePlan().Expand(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => editBeyond.EffectivePlan().Expand(4));
    }

    [Fact]
    public void Positions_Never_Decrease_Whatever_The_Pastes()
    {
        var script = new SequenceScript();
        script.Apply(new InsertUnits(1, 8, 2, 3, SkipKeyUnits: false)); // copie plus loin
        script.Apply(new InsertUnits(7, 0, 3, 1, SkipKeyUnits: false)); // copie plus tôt
        script.Apply(new DeleteUnit(4));
        script.Apply(new RepeatUnit(6, 4));

        var positions = script.EffectivePlan().Expand(10).Slots.Select(s => s.Position).ToList();

        Assert.Equal(positions.Order(), positions);
    }

    // ---- correspondance position → sortie ----

    [Fact]
    public void FindSlot_On_A_Deleted_Unit_Points_At_What_Plays_In_Its_Place()
    {
        // Supprimer décale vers la gauche : ce qui joue à la place de 4 est 5,
        // pas 3, rigoureusement identique à l'original.
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(4));
        var output = script.EffectivePlan().Expand(8);

        int index = output.FindSlot(4);

        Assert.Equal(4, index);
        Assert.Equal(5, output.Slots[index].Source);
    }

    [Fact]
    public void FindSlot_On_A_Repeated_Unit_Points_At_Its_First_Occurrence()
    {
        var script = new SequenceScript();
        script.Apply(new RepeatUnit(2, 3));
        var output = script.EffectivePlan().Expand(5);

        Assert.Equal(1, output.FindSlot(1));
        Assert.Equal(2, output.FindSlot(2));
        Assert.Equal(5, output.FindSlot(3));
    }

    [Fact]
    public void FindSlot_Ignores_Where_A_Pasted_Unit_Was_Copied_From()
    {
        // L'unité 1, collée après 3, joue à la position 3 : chercher la
        // position 1 doit tomber sur l'originale, et la position 4 après le collage.
        var script = new SequenceScript();
        script.Apply(new InsertUnits(3, 1, 1, 2, SkipKeyUnits: false));
        var output = script.EffectivePlan().Expand(6);

        Assert.Equal(1, output.FindSlot(1));
        Assert.Equal(3, output.FindSlot(3));
        Assert.Equal(6, output.FindSlot(4));
    }

    [Fact]
    public void FindSlot_On_A_Deleted_Unit_Followed_By_A_Paste_Points_At_The_Paste()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(2));
        script.Apply(new InsertUnits(2, 5, 1, 1, SkipKeyUnits: false));
        var output = script.EffectivePlan().Expand(6);

        int index = output.FindSlot(2);

        Assert.True(output.Slots[index].Inserted);
        Assert.Equal(5, output.Slots[index].Source);
    }

    [Fact]
    public void FindSlot_Clamps_When_Everything_After_Is_Deleted()
    {
        var script = new SequenceScript();
        for (int u = 3; u < 8; u++) script.Apply(new DeleteUnit(u));
        var output = script.EffectivePlan().Expand(8);

        Assert.Equal(2, output.FindSlot(7));
    }

    [Fact]
    public void FindSlot_On_An_Empty_Output_Reports_Nothing_To_Show()
    {
        var script = new SequenceScript();
        script.Apply(new DeleteUnit(0));

        Assert.Equal(-1, script.EffectivePlan().Expand(1).FindSlot(0));
    }

    [Fact]
    public void CountAt_Counts_Own_Occurrences_And_What_Is_Pasted_After()
    {
        var script = new SequenceScript();
        script.Apply(new RepeatUnit(1, 3));
        script.Apply(new InsertUnits(1, 4, 2, 2, SkipKeyUnits: false));
        script.Apply(new DeleteUnit(3));
        var output = script.EffectivePlan().Expand(6);

        Assert.Equal(1, output.CountAt(0));
        Assert.Equal(3 + 4, output.CountAt(1));
        Assert.Equal(0, output.CountAt(3));
    }
}
