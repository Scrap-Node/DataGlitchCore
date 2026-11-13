namespace DataGlitchCore.Core;

/// <summary>
/// Une écriture contiguë : les octets d'origine (pour l'undo) et les nouveaux
/// (pour l'apply/redo). Ne stocke que les plages touchées, pas tout le buffer.
/// </summary>
public readonly record struct ByteEdit(int Offset, byte[] OldBytes, byte[] NewBytes)
{
    public int Length => NewBytes.Length;
}

/// <summary>Une passe de destruction complète = un cran d'undo/redo.</summary>
public sealed class DestructionBatch
{
    public DestructionBatch(string label, IReadOnlyList<ByteEdit> edits)
    {
        Label = label;
        Edits = edits;
    }

    /// <summary>
    /// Numéro donné par <see cref="OperationJournal.Apply"/> : il croît d'une
    /// passe appliquée à la suivante, dans la vie du journal, et ne revient
    /// jamais en arrière, même après un retour à l'original. Annuler puis
    /// rétablir garde le numéro. 0 tant que la passe n'est pas appliquée.
    ///
    /// Une clé stable pour un état : une app peut s'en servir comme clé de
    /// cache, plutôt que de l'identité de l'objet.
    /// </summary>
    public long Id { get; internal set; }

    public string Label { get; }
    public IReadOnlyList<ByteEdit> Edits { get; }
    public int EditCount => Edits.Count;

    /// <summary>Offset du premier octet détruit — le « début de la destruction ».</summary>
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

    /// <summary>Offset juste après le dernier octet détruit — la fin de la destruction.</summary>
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

/// <summary>
/// Applique les passes sur le <see cref="FileBuffer"/> et gère l'historique
/// undo/redo. Le coût d'un undo/redo est proportionnel à la taille de la passe,
/// pas à la taille du fichier — aucun snapshot complet du buffer n'est conservé.
/// </summary>
public sealed class OperationJournal
{
    private readonly FileBuffer _buffer;
    private readonly List<DestructionBatch> _undo = new();
    private readonly List<DestructionBatch> _redo = new();
    private long _lastId;

    public OperationJournal(FileBuffer buffer) => _buffer = buffer;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<DestructionBatch> History => _undo;

    /// <summary>
    /// Applique une passe déjà construite, lui donne son
    /// <see cref="DestructionBatch.Id"/> et l'empile dans l'historique. Une
    /// passe appliquée une seconde fois reçoit un nouveau numéro.
    /// </summary>
    public void Apply(DestructionBatch batch)
    {
        foreach (var edit in batch.Edits)
            _buffer.Write(edit.Offset, edit.NewBytes);
        batch.Id = ++_lastId;
        _undo.Add(batch);
        _redo.Clear();
    }

    public DestructionBatch? Undo()
    {
        if (_undo.Count == 0) return null;
        var batch = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        // rétablir dans l'ordre inverse au cas où des plages se chevauchent
        for (int i = batch.Edits.Count - 1; i >= 0; i--)
            _buffer.Write(batch.Edits[i].Offset, batch.Edits[i].OldBytes);
        _redo.Add(batch);
        return batch;
    }

    public DestructionBatch? Redo()
    {
        if (_redo.Count == 0) return null;
        var batch = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var edit in batch.Edits)
            _buffer.Write(edit.Offset, edit.NewBytes);
        _undo.Add(batch);
        return batch;
    }

    /// <summary>Revient à l'original et vide tout l'historique.</summary>
    public void ResetAll()
    {
        _buffer.ResetToOriginal();
        _undo.Clear();
        _redo.Clear();
    }
}
