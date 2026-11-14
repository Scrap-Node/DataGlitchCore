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
/// L'historique des passes d'un <see cref="FileBuffer"/> (spec §3.1 et §7).
/// Annuler et rétablir ne réécrivent aucun octet : ils déplacent le sommet de
/// la pile de segments, ce qui ne coûte rien, quelle que soit la taille de la
/// passe. Une nouvelle passe supprime ce qui était à rétablir ; revenir à
/// l'original supprime tout et ne s'annule pas.
///
/// L'historique est borné (<see cref="JournalOptions"/>) : au-delà de
/// <see cref="JournalOptions.MaxUndoSteps"/> passes, ou du budget disque, les
/// plus anciennes fusionnent dans une base, dans <see cref="Apply"/>, et ne
/// s'annulent plus une à une. La dernière passe reste toujours annulable.
/// </summary>
public sealed class OperationJournal
{
    private readonly FileBuffer _buffer;
    private readonly JournalOptions _options;
    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    private BaseLayer? _base;
    private long _lastId;

    public OperationJournal(FileBuffer buffer, JournalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        options ??= new JournalOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxUndoSteps, 1, nameof(options));
        if (options.DiskBudgetBytes is long budget) ArgumentOutOfRangeException.ThrowIfNegative(budget, nameof(options));
        _buffer = buffer;
        _options = options;
        buffer.Journal = this;
    }

    public JournalOptions Options => _options;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Les passes annulables, la plus ancienne d'abord. Les passes fusionnées n'y sont plus.</summary>
    public IReadOnlyList<PassInfo> History => _undo.Select(e => e.Info).ToList();

    /// <summary>Passes fusionnées dans la base (§7), qui ne s'annulent plus une à une. Revenir à l'original les efface.</summary>
    public long MergedPasses { get; private set; }

    /// <summary>
    /// Le numéro de la dernière passe fusionnée, 0 sans fusion. Avec les
    /// numéros de <see cref="History"/>, il identifie l'état courant : de quoi
    /// en faire une clé de cache, même une fois toutes les passes annulables annulées.
    /// </summary>
    public long LastMergedId => _base?.Id ?? 0;

    /// <summary>
    /// La donnée de l'app pour les passes fusionnées : la fusion de leurs
    /// <see cref="PassInfo.AppData"/> par <see cref="JournalOptions.MergeAppData"/>.
    /// Nulle sans passe fusionnée.
    /// </summary>
    public string? MergedAppData => _base?.AppData;

    /// <summary>La place de la session sur le disque : l'original, la base et les segments, à rétablir compris.</summary>
    public long DiskBytes =>
        _buffer.Length + (_base?.DiskBytes ?? 0) + _undo.Concat(_redo).Sum(e => e.Segment.Blob.Length);

    /// <summary>
    /// Vérifie qu'une passe d'environ <paramref name="segmentBytes"/> octets
    /// tient dans le budget disque, une fois appliquée : ce qui est à
    /// rétablir aura disparu, et toutes les passes actives pourront fusionner.
    /// Le moteur l'appelle avant de construire une passe, avec son estimation
    /// (<see cref="DestructionEngine.EstimateSegmentBytes"/>). Sans budget, ne fait rien.
    /// </summary>
    /// <exception cref="DiskBudgetExceededException">La passe ne tiendrait pas : rien n'a été modifié.</exception>
    public void EnsureRoom(long segmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentBytes);
        if (_options.DiskBudgetBytes is not long budget) return;
        // la passe, une fois appliquée, sera la dernière : toutes les autres pourront fusionner
        List<Weight> active = [.. _undo.Select(e => Weight.Of(e.Segment)), new Weight(segmentBytes, segmentBytes, segmentBytes)];
        if (FirstFit(active, budget) is null)
            throw new DiskBudgetExceededException(Enumerable.Range(0, active.Count).Min(k => Predict(active, k)), budget);
    }

    /// <summary>
    /// Applique une passe construite sur l'état courant, lui donne son numéro
    /// et l'empile. Son segment est forcé sur le disque et rangé dans
    /// <c>segments/</c>. Ce qui était à rétablir est supprimé. Au-delà des
    /// bornes du journal, les passes les plus anciennes fusionnent dans la base.
    /// </summary>
    /// <param name="batch">La passe, construite par <see cref="FileBuffer.BeginPass"/> ou le moteur.</param>
    /// <param name="appData">Donnée opaque de l'app, rangée avec la passe.</param>
    /// <exception cref="InvalidOperationException">La passe vient d'un autre buffer, est déjà
    /// appliquée ou disposée, ou a été construite sur un autre état que l'état courant.</exception>
    /// <exception cref="IOException">La fusion a échoué, faute de place par exemple : la passe est
    /// abandonnée, rien n'a été modifié.</exception>
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
        var info = new PassInfo(id, batch.Label, batch.EditCount, batch.BytesChanged, batch.FirstOffset, batch.LastOffset, appData);
        var entry = new Entry(info, segment);

        // La fusion s'écrit avant de toucher à l'état (§7, étape 1) : si elle
        // échoue, la passe est abandonnée et rien n'a changé.
        List<Entry> active = [.. _undo, entry];
        int count = MergeCount(active);
        BaseLayer? merged = null;
        if (count > 0)
        {
            try
            {
                merged = Merge(active.Take(count).ToList());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                batch.Dispose();
                throw new IOException($"La fusion de l'historique a échoué : {ex.Message} La passe est abandonnée, rien n'a été modifié.", ex);
            }
        }

        _lastId = id;
        batch.Id = id;
        batch.Applied = true;
        var dropped = new List<IBlob>();
        dropped.AddRange(_redo.Select(e => e.Segment.Blob));
        _redo.Clear();
        _undo.Add(entry);
        _buffer.AddDiffs(segment, +1);
        if (merged is not null)
        {
            dropped.AddRange(_undo.Take(count).Select(e => e.Segment.Blob));
            if (_base is not null) dropped.AddRange(_base.Files);
            _undo.RemoveRange(0, count);
            _base = merged;
            MergedPasses += count;
        }
        Publish();

        // ensuite seulement, ce que l'état ne référence plus (§7, étape 3)
        foreach (var blob in dropped) _buffer.Store.Delete(blob);
        return info;
    }

    public PassInfo? Undo()
    {
        if (_undo.Count == 0) return null;
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        _buffer.AddDiffs(entry.Segment, -1);
        Publish();
        return entry.Info;
    }

    public PassInfo? Redo()
    {
        if (_redo.Count == 0) return null;
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        _buffer.AddDiffs(entry.Segment, +1);
        Publish();
        return entry.Info;
    }

    /// <summary>Revient à l'original : supprime toutes les passes, rétablir et base compris. Ne s'annule pas.</summary>
    public void ResetAll()
    {
        var files = _undo.Concat(_redo).Select(e => e.Segment.Blob).Concat(_base?.Files ?? []).ToList();
        _undo.Clear();
        _redo.Clear();
        _base = null;
        MergedPasses = 0;
        _buffer.SetState(_buffer.Store.Source, []);
        _buffer.ClearDiffs();
        foreach (var blob in files) _buffer.Store.Delete(blob);
    }

    /// <summary>
    /// Octets de <c>[start, start + length)</c> qui diffèrent de l'original,
    /// exactement : les tranches entières par les compteurs des segments, les
    /// bords par comparaison directe (spec §6.3).
    /// </summary>
    public long ChangedBytes(long start, long length) => _buffer.ChangedBytes(start, length);

    /// <summary>Les fichiers que l'état référence : base, passes actives et à rétablir. Pour les tests.</summary>
    internal IEnumerable<IBlob> Files =>
        _undo.Concat(_redo).Select(e => e.Segment.Blob).Concat(_base?.Files ?? []);

    /// <summary>La base, s'il y en a une. Pour les tests.</summary>
    internal BaseLayer? Base => _base;

    /// <summary>Le nouvel état : la base au fond, éparse ou dense, puis les passes actives.</summary>
    private void Publish()
    {
        var layers = _undo.Select(e => e.Segment);
        if (_base?.Sparse is { } sparse) layers = layers.Prepend(sparse);
        _buffer.SetState(_base?.Dense ?? _buffer.Store.Source, layers.ToArray());
    }

    /// <summary>
    /// Combien des passes actives, les plus anciennes, doivent fusionner : ce
    /// qui dépasse <see cref="JournalOptions.MaxUndoSteps"/>, et au-delà du
    /// budget disque, le moins qui y ramène, au pire toutes sauf la dernière.
    /// </summary>
    private int MergeCount(List<Entry> active)
    {
        int count = Math.Max(0, active.Count - _options.MaxUndoSteps);
        if (_options.DiskBudgetBytes is not long budget) return count;
        return FirstFit(active.Select(e => Weight.Of(e.Segment)).ToList(), budget) ?? Math.Max(count, active.Count - 1);
    }

    /// <summary>
    /// Le plus petit nombre de passes à fusionner, parmi
    /// <paramref name="active"/> (la dernière ne fusionne jamais), pour que
    /// la session tienne dans <paramref name="budget"/> ; null si aucun.
    /// </summary>
    private int? FirstFit(List<Weight> active, long budget)
    {
        for (int count = Math.Max(0, active.Count - _options.MaxUndoSteps); count < Math.Max(1, active.Count); count++)
            if (Predict(active, count) <= budget) return count;
        return null;
    }

    /// <summary>
    /// La place de la session, au pire, une fois les <paramref name="count"/>
    /// plus anciennes de <paramref name="active"/> fusionnées. Une base qui
    /// pourrait couvrir la moitié du fichier (écarts comblés compris, 11
    /// octets au plus par plage) est comptée dense ; sinon, la somme de ce
    /// qu'elle fusionne, qui ne peut que se recouvrir.
    /// </summary>
    private long Predict(List<Weight> active, int count)
    {
        long dense = BaseLayer.DenseBytes(_buffer);
        var old = _base is null ? new Weight(0, 0, 0)
            : _base.IsDense ? new Weight(_base.DiskBytes, _buffer.Length, 0)
            : Weight.Of(_base.Sparse!);
        long bytes = old.Bytes, data = old.Data, runs = old.Runs, rest = 0;
        for (int i = 0; i < active.Count; i++)
        {
            if (i < count)
            {
                bytes += active[i].Bytes;
                data += active[i].Data;
                runs += active[i].Runs;
            }
            else
            {
                rest += active[i].Bytes;
            }
        }
        long merged = count == 0 ? old.Bytes
            : (data + (SegmentFormat.RunHeaderSize - 1) * runs) * 2 >= _buffer.Length ? dense
            : Math.Min(bytes, dense);
        return _buffer.Length + merged + rest;
    }

    /// <summary>Ce qu'une passe pèse pour le budget : sa place, ses octets écrits, ses plages.</summary>
    private readonly record struct Weight(long Bytes, long Data, long Runs)
    {
        public static Weight Of(Segment segment) => new(segment.Blob.Length, segment.DataBytes, segment.RunCount);
    }

    private BaseLayer Merge(List<Entry> entries)
    {
        string? appData = null;
        if (_options.MergeAppData is { } merge)
        {
            var data = new List<string?>();
            if (_base is not null) data.Add(_base.AppData);
            data.AddRange(entries.Select(e => e.Info.AppData));
            appData = merge(data);
        }
        return BaseLayer.Merge(_buffer, _base, entries.Select(e => e.Segment).ToList(), entries[^1].Info.Id, appData);
    }

    private sealed record Entry(PassInfo Info, Segment Segment);
}
