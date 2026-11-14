namespace DataGlitchCore.Core;

/// <summary>Une passe de l'historique, telle que l'app l'affiche et la range.</summary>
/// <param name="Id">Numéro donné à l'application, croissant dans la session.</param>
/// <param name="Label">Libellé de la passe.</param>
/// <param name="EditCount">Nombre d'écritures.</param>
/// <param name="BytesChanged">Total des octets de ces écritures.</param>
/// <param name="FirstOffset">Premier octet détruit.</param>
/// <param name="LastOffset">Juste après le dernier octet détruit.</param>
/// <param name="AppData">Donnée opaque de l'app, rangée avec la passe (DataMosher y met les frames touchées).</param>
public sealed record PassInfo(long Id, string Label, long EditCount, long BytesChanged,
                              long FirstOffset, long LastOffset, string? AppData);

/// <summary>
/// L'historique des passes d'un <see cref="FileBuffer"/> (spec §3.1). Annuler
/// et rétablir ne réécrivent aucun octet : ils déplacent le sommet de la pile
/// de segments, ce qui ne coûte rien, quelle que soit la taille de la passe.
/// Une nouvelle passe supprime ce qui était à rétablir ; revenir à l'original
/// supprime tout et ne s'annule pas.
/// </summary>
public sealed class OperationJournal
{
    private readonly FileBuffer _buffer;
    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    private long _lastId;

    public OperationJournal(FileBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Les passes annulables, la plus ancienne d'abord.</summary>
    public IReadOnlyList<PassInfo> History => _undo.Select(e => e.Info).ToList();

    /// <summary>Passes fusionnées, plus annulables une à une : la fusion arrive au lot 3.</summary>
    public long MergedPasses => 0;

    /// <summary>
    /// Applique une passe construite sur l'état courant, lui donne son numéro
    /// et l'empile. Son segment est forcé sur le disque et rangé dans
    /// <c>segments/</c>. Ce qui était à rétablir est supprimé.
    /// </summary>
    /// <param name="batch">La passe, construite par <see cref="FileBuffer.BeginPass"/> ou le moteur.</param>
    /// <param name="appData">Donnée opaque de l'app, rangée avec la passe.</param>
    /// <exception cref="InvalidOperationException">La passe vient d'un autre buffer, est déjà
    /// appliquée ou disposée, ou a été construite sur un autre état que l'état courant.</exception>
    public PassInfo Apply(DestructionBatch batch, string? appData = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Buffer != _buffer) throw new InvalidOperationException("Cette passe a été construite sur un autre fichier.");
        if (batch.Applied) throw new InvalidOperationException("Cette passe est déjà appliquée.");
        if (batch.Disposed) throw new InvalidOperationException("Cette passe a été abandonnée.");
        var state = _buffer.State;
        if (batch.BaseState.Number != state.Number)
            throw new InvalidOperationException("Cette passe a été construite sur un autre état que l'état courant : rien n'a été modifié.");

        long id = _lastId + 1;
        var segment = batch.Segment;
        segment.WriteId(id);
        segment.MovedTo(_buffer.Store.Publish(segment.Blob, $"{id:D8}.seg"));

        _lastId = id;
        batch.Id = id;
        batch.Applied = true;
        var info = new PassInfo(id, batch.Label, batch.EditCount, batch.BytesChanged, batch.FirstOffset, batch.LastOffset, appData);

        foreach (var dropped in _redo) _buffer.Store.Delete(dropped.Segment.Blob);
        _redo.Clear();
        _undo.Add(new Entry(info, segment));
        _buffer.AddDiffs(segment, +1);
        _buffer.SetLayers([.. state.Layers, segment]);
        return info;
    }

    public PassInfo? Undo()
    {
        if (_undo.Count == 0) return null;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        _buffer.AddDiffs(entry.Segment, -1);
        _buffer.SetLayers(_undo.Select(e => e.Segment).ToArray());
        return entry.Info;
    }

    public PassInfo? Redo()
    {
        if (_redo.Count == 0) return null;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        _buffer.AddDiffs(entry.Segment, +1);
        _buffer.SetLayers(_undo.Select(e => e.Segment).ToArray());
        return entry.Info;
    }

    /// <summary>Revient à l'original : supprime toutes les passes, rétablir compris. Ne s'annule pas.</summary>
    public void ResetAll()
    {
        _buffer.SetLayers([]);
        _buffer.ClearDiffs();
        foreach (var entry in _undo.Concat(_redo)) _buffer.Store.Delete(entry.Segment.Blob);
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>
    /// Octets de <c>[start, start + length)</c> qui diffèrent de l'original,
    /// exactement : les tranches entières par les compteurs des segments, les
    /// bords par comparaison directe (spec §6.3).
    /// </summary>
    public long ChangedBytes(long start, long length) => _buffer.ChangedBytes(start, length);

    private sealed record Entry(PassInfo Info, Segment Segment);
}
