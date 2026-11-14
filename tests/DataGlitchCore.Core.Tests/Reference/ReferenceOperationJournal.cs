namespace DataGlitchCore.Core.Tests.Reference;

// ORACLE — ne pas modifier.
//
// ByteEdit, DestructionBatch et OperationJournal de DataGlitchCore 0.2.0
// (24c3d18), recopiés tels quels (spec dataglitchcore-buffer, §13.1).

internal readonly record struct ReferenceByteEdit(int Offset, byte[] OldBytes, byte[] NewBytes)
{
    public int Length => NewBytes.Length;
}

internal sealed class ReferenceDestructionBatch
{
    public ReferenceDestructionBatch(string label, IReadOnlyList<ReferenceByteEdit> edits)
    {
        Label = label;
        Edits = edits;
    }

    public string Label { get; }
    public IReadOnlyList<ReferenceByteEdit> Edits { get; }
    public int EditCount => Edits.Count;

    public int FirstOffset
    {
        get
        {
            if (Edits.Count == 0) return 0;
            int min = Edits[0].Offset;
            for (int i = 1; i < Edits.Count; i++)
                if (Edits[i].Offset < min) min = Edits[i].Offset;
            return min;
        }
    }

    public int LastOffset
    {
        get
        {
            int max = 0;
            for (int i = 0; i < Edits.Count; i++)
            {
                int end = Edits[i].Offset + Edits[i].Length;
                if (end > max) max = end;
            }
            return max;
        }
    }

    public int BytesChanged
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Edits.Count; i++) n += Edits[i].Length;
            return n;
        }
    }
}

internal sealed class ReferenceOperationJournal
{
    private readonly ReferenceFileBuffer _buffer;
    private readonly List<ReferenceDestructionBatch> _undo = new();
    private readonly List<ReferenceDestructionBatch> _redo = new();

    public ReferenceOperationJournal(ReferenceFileBuffer buffer) => _buffer = buffer;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<ReferenceDestructionBatch> History => _undo;

    public void Apply(ReferenceDestructionBatch batch)
    {
        foreach (var edit in batch.Edits)
            _buffer.Write(edit.Offset, edit.NewBytes);
        _undo.Add(batch);
        _redo.Clear();
    }

    public ReferenceDestructionBatch? Undo()
    {
        if (_undo.Count == 0) return null;
        var batch = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (int i = batch.Edits.Count - 1; i >= 0; i--)
            _buffer.Write(batch.Edits[i].Offset, batch.Edits[i].OldBytes);
        _redo.Add(batch);
        return batch;
    }

    public ReferenceDestructionBatch? Redo()
    {
        if (_redo.Count == 0) return null;
        var batch = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var edit in batch.Edits)
            _buffer.Write(edit.Offset, edit.NewBytes);
        _undo.Add(batch);
        return batch;
    }

    public void ResetAll()
    {
        _buffer.ResetToOriginal();
        _undo.Clear();
        _redo.Clear();
    }
}
