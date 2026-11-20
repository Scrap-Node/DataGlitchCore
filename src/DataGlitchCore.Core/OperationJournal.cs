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
/// plus anciennes fusionnent dans une base et ne s'annulent plus une à une. La
/// dernière passe reste toujours annulable.
///
/// La fusion se fait en tâche de fond (D14). <see cref="Apply"/> rend la main
/// tout de suite : les passes à fusionner quittent aussitôt l'historique, mais
/// restent des couches de l'état jusqu'à ce que leur base soit écrite. L'état
/// change alors d'agencement, pas d'octets ni de numéro. Les fusions passent
/// une à la fois, dans l'ordre ; une passe appliquée pendant une fusion attend
/// la suivante. Annuler et rétablir ne touchent pas aux passes en fusion ;
/// revenir à l'original interrompt la fusion en cours et l'attend.
///
/// Les écritures (passes, annulations, retours) ne sont pas concurrentes entre
/// elles : l'appelant les sérialise. La tâche de fond, elle, est synchronisée
/// par le journal.
/// </summary>
public sealed class OperationJournal
{
    private readonly FileBuffer _buffer;
    private readonly JournalOptions _options;
    private readonly object _gate = new();

    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    // fusionnées pour l'historique, encore des couches de l'état, les plus anciennes d'abord
    private readonly List<Entry> _merging = new();
    private BaseLayer? _base;
    private long _lastId;

    private long _mergedPasses;
    private string? _mergedAppData;
    private long _lastMergedId;

    private Task? _worker;
    private CancellationTokenSource _cancel = new();
    private int _jobCount;        // passes de la fusion en cours, les premières de _merging
    private long _reserved;       // la base en cours d'écriture, comptée au pire
    // ce qu'une fusion a remplacé ou abandonné, encore sur le disque jusqu'à sa suppression (étape 3)
    private readonly List<IBlob> _dropping = new();
    private long _lastEstimate;   // la place demandée par la dernière passe acceptée
    private Exception? _mergeError;

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

    public bool CanUndo
    {
        get
        {
            lock (_gate) return _undo.Count > 0;
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate) return _redo.Count > 0;
        }
    }

    /// <summary>Les passes annulables, la plus ancienne d'abord. Les passes fusionnées n'y sont plus, même en cours de fusion.</summary>
    public IReadOnlyList<PassInfo> History
    {
        get
        {
            lock (_gate) return _undo.Select(e => e.Info).ToList();
        }
    }

    /// <summary>
    /// Passes fusionnées (§7), qui ne s'annulent plus une à une, y compris
    /// celles dont la base s'écrit encore. Revenir à l'original les efface.
    /// </summary>
    public long MergedPasses
    {
        get
        {
            lock (_gate) return _mergedPasses;
        }
    }

    /// <summary>
    /// Le numéro de la dernière passe fusionnée, 0 sans fusion. Avec les
    /// numéros de <see cref="History"/>, il identifie l'état courant : de quoi
    /// en faire une clé de cache, même une fois toutes les passes annulables annulées.
    /// </summary>
    public long LastMergedId
    {
        get
        {
            lock (_gate) return _lastMergedId;
        }
    }

    /// <summary>
    /// La donnée de l'app pour les passes fusionnées : la fusion de leurs
    /// <see cref="PassInfo.AppData"/> par <see cref="JournalOptions.MergeAppData"/>,
    /// à jour dès qu'elles quittent l'historique. Nulle sans passe fusionnée.
    /// </summary>
    public string? MergedAppData
    {
        get
        {
            lock (_gate) return _mergedAppData;
        }
    }

    /// <summary>
    /// Une fusion est prévue ou en cours : des passes fusionnées sont encore
    /// des couches de l'état, ou ce qu'une fusion a remplacé n'est pas encore
    /// supprimé (§7, étape 3).
    /// </summary>
    public bool IsMerging
    {
        get
        {
            lock (_gate) return _merging.Count > 0 || _dropping.Count > 0;
        }
    }

    /// <summary>
    /// L'échec de la dernière fusion, nul si elle a réussi. Après un échec,
    /// l'état reste juste (les passes restent des couches) ; la prochaine passe
    /// relance la fusion, et <see cref="WaitForMergesAsync"/> lève l'erreur.
    /// </summary>
    public Exception? MergeError
    {
        get
        {
            lock (_gate) return _mergeError;
        }
    }

    /// <summary>
    /// La place de la session sur le disque : l'original, la base et les
    /// segments, à rétablir compris. Pendant une fusion, l'ancienne base et la
    /// nouvelle comptent toutes les deux, la nouvelle à sa taille au pire.
    /// </summary>
    public long DiskBytes
    {
        get
        {
            lock (_gate) return Usage() + _redo.Sum(e => e.Segment.Blob.Length);
        }
    }

    /// <summary>
    /// Attend que les fusions prévues soient écrites, et les fichiers
    /// fusionnés supprimés. Pour les tests, les bancs et un arrêt propre :
    /// l'app n'en a pas besoin, l'état est juste à tout moment.
    /// </summary>
    /// <exception cref="IOException">La fusion a échoué ; l'état reste juste.</exception>
    public async Task WaitForMergesAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task worker;
            lock (_gate)
            {
                if (_worker is { IsCompleted: false } running)
                {
                    worker = running;
                }
                else
                {
                    if (_merging.Count == 0) return;
                    if (_mergeError is { } error)
                        throw new IOException($"La fusion de l'historique a échoué : {error.Message}", error);
                    StartWorker();
                    worker = _worker!;
                }
            }
            await worker.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Vérifie qu'une passe d'environ <paramref name="segmentBytes"/> octets
    /// tient dans le budget disque : la session, ce qui est à rétablir en
    /// moins, plus la passe, doit laisser la place d'écrire une base (une copie
    /// dense au plus), pour que les fusions à venir tiennent elles aussi.
    /// Pendant une fusion, l'ancienne base et la nouvelle comptent toutes les
    /// deux ; si la passe ne tient qu'une fois cette fusion aboutie, on
    /// l'attend. Le moteur l'appelle avant de construire une passe, avec son
    /// estimation (<see cref="DestructionEngine.EstimateSegmentBytes"/>). Sans
    /// budget, ne fait rien.
    /// </summary>
    /// <exception cref="DiskBudgetExceededException">La passe ne tiendrait pas : rien n'a été modifié.</exception>
    public void EnsureRoom(long segmentBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentBytes);
        if (_options.DiskBudgetBytes is not long budget) return;
        while (true)
        {
            Task running;
            lock (_gate)
            {
                long needed = Usage() + segmentBytes + BaseLayer.DenseBytes(_buffer);
                if (needed <= budget)
                {
                    _lastEstimate = segmentBytes;
                    return;
                }
                if (_worker is not { IsCompleted: false } worker) throw new DiskBudgetExceededException(needed, budget);
                running = worker;
            }
            // une fusion en cours libérera de la place en aboutissant
            try
            {
                running.Wait();
            }
            catch (AggregateException)
            {
                // son échec est rangé dans MergeError ; la place, elle, n'a pas changé
            }
        }
    }

    /// <summary>
    /// Applique une passe construite sur l'état courant, lui donne son numéro
    /// et l'empile. Son segment est forcé sur le disque et rangé dans
    /// <c>segments/</c>. Ce qui était à rétablir est supprimé. Au-delà des
    /// bornes du journal, les passes les plus anciennes quittent l'historique
    /// et fusionnent en tâche de fond : la méthode n'attend pas la fusion.
    /// </summary>
    /// <param name="batch">La passe, construite par <see cref="FileBuffer.BeginPass"/> ou le moteur.</param>
    /// <param name="appData">Donnée opaque de l'app, rangée avec la passe.</param>
    /// <exception cref="InvalidOperationException">La passe vient d'un autre buffer, est déjà
    /// appliquée ou disposée, ou a été construite sur un autre état que l'état courant.</exception>
    public PassInfo Apply(DestructionBatch batch, string? appData = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        List<IBlob> dropped;
        PassInfo info;
        lock (_gate)
        {
            if (batch.Buffer != _buffer) throw new InvalidOperationException("Cette passe a été construite sur un autre fichier.");
            if (batch.Applied) throw new InvalidOperationException("Cette passe est déjà appliquée.");
            if (batch.Disposed) throw new InvalidOperationException("Cette passe a été abandonnée.");
            // une fusion qui aboutit change l'agencement de l'état, pas son numéro
            if (batch.BaseState.Number != _buffer.State.Number)
                throw new InvalidOperationException("Cette passe a été construite sur un autre état que l'état courant : rien n'a été modifié.");

            long id = _lastId + 1;
            var segment = batch.Segment;
            segment.WriteId(id);
            segment.MovedTo(_buffer.Store.Publish(segment.Blob, $"{id:D8}.seg"));
            info = new PassInfo(id, batch.Label, batch.EditCount, batch.BytesChanged, batch.FirstOffset, batch.LastOffset, appData);
            var entry = new Entry(info, segment);

            int count = MergeCount([.. _undo.Select(e => Weight.Of(e.Segment)), Weight.Of(segment)]);
            _lastId = id;
            batch.Id = id;
            batch.Applied = true;
            dropped = _redo.Select(e => e.Segment.Blob).ToList();
            _redo.Clear();
            _undo.Add(entry);
            _buffer.AddDiffs(segment, +1);
            if (count > 0) Schedule(count);
            // après un échec, chaque passe relance la fusion de ce qui attend encore
            StartWorker();
            Publish();
        }

        // ensuite seulement, ce que l'état ne référence plus
        foreach (var blob in dropped) _buffer.Store.Delete(blob);
        return info;
    }

    public PassInfo? Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0) return null;
            var entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
            _buffer.AddDiffs(entry.Segment, -1);
            Publish();
            return entry.Info;
        }
    }

    public PassInfo? Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0) return null;
            var entry = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
            _buffer.AddDiffs(entry.Segment, +1);
            Publish();
            return entry.Info;
        }
    }

    /// <summary>
    /// Revient à l'original : supprime toutes les passes, rétablir et base
    /// compris. Une fusion en cours est interrompue, et attendue : ce qu'elle
    /// avait écrit est supprimé. Ne s'annule pas.
    /// </summary>
    public void ResetAll()
    {
        StopWorker();
        List<IBlob> files;
        lock (_gate)
        {
            files = Segments().Select(e => e.Segment.Blob).Concat(_base?.Files ?? []).ToList();
            _undo.Clear();
            _redo.Clear();
            _merging.Clear();
            _base = null;
            _mergedPasses = 0;
            _mergedAppData = null;
            _lastMergedId = 0;
            _mergeError = null;
            _buffer.SetState(_buffer.Store.Source, []);
            _buffer.ClearDiffs();
        }
        foreach (var blob in files) _buffer.Store.Delete(blob);
    }

    /// <summary>
    /// Octets de <c>[start, start + length)</c> qui diffèrent de l'original,
    /// exactement : les tranches entières par les compteurs des segments, les
    /// bords par comparaison directe (spec §6.3).
    /// </summary>
    public long ChangedBytes(long start, long length) => _buffer.ChangedBytes(start, length);

    /// <summary>Interrompt la fusion en cours et l'attend : le buffer se ferme. Les passes restent des couches.</summary>
    internal void Shutdown() => StopWorker();

    /// <summary>
    /// Pour les tests : appelée par la tâche de fond à chaque étape d'une
    /// fusion (« début » avant d'écrire, « écrite » avant de basculer,
    /// « basculée » avant de supprimer ce qu'elle remplace), elle peut la
    /// retenir, ou lever pour simuler une panne.
    /// </summary>
    internal Func<string, CancellationToken, Task>? MergeHook { get; set; }

    /// <summary>Les fichiers que l'état référence : base, passes en fusion, annulables et à rétablir. Pour les tests.</summary>
    internal IEnumerable<IBlob> Files
    {
        get
        {
            lock (_gate) return Segments().Select(e => e.Segment.Blob).Concat(_base?.Files ?? []).ToList();
        }
    }

    /// <summary>
    /// La base, l'état, les fichiers référencés et la place comptée, pris
    /// ensemble sous le verrou : une fusion qui aboutit ne peut pas passer
    /// entre deux. Pour les tests.
    /// </summary>
    internal JournalSnapshot Snapshot()
    {
        lock (_gate)
        {
            // ce qu'une fusion a remplacé compte tant qu'il est sur le disque
            var files = Segments().Select(e => e.Segment.Blob).Concat(_base?.Files ?? []).Concat(_dropping).ToList();
            long disk = Usage() + _redo.Sum(e => e.Segment.Blob.Length);
            return new JournalSnapshot(_base, _buffer.State, files, _merging.Count > 0 || _dropping.Count > 0, _reserved, disk);
        }
    }

    /// <summary>La base écrite, s'il y en a une. Pour les tests.</summary>
    internal BaseLayer? Base
    {
        get
        {
            lock (_gate) return _base;
        }
    }

    private IEnumerable<Entry> Segments() => _merging.Concat(_undo).Concat(_redo);

    // ---------- l'état ----------

    /// <summary>Le nouvel état : la base au fond, éparse ou dense, puis les passes en fusion, puis les annulables.</summary>
    private void Publish()
    {
        var (bottom, layers) = Layout();
        _buffer.SetState(bottom, layers);
    }

    private (IBlob Bottom, Segment[] Layers) Layout()
    {
        var segments = _merging.Concat(_undo).Select(e => e.Segment);
        if (_base?.Sparse is { } sparse) segments = segments.Prepend(sparse);
        return (_base?.Dense ?? _buffer.Store.Source, segments.ToArray());
    }

    /// <summary>
    /// Les <paramref name="count"/> passes les plus anciennes quittent
    /// l'historique : elles sont fusionnées pour l'app dès maintenant, et la
    /// tâche de fond écrira leur base.
    /// </summary>
    private void Schedule(int count)
    {
        var entries = _undo.GetRange(0, count);
        _undo.RemoveRange(0, count);
        if (_options.MergeAppData is { } merge)
        {
            var data = new List<string?>();
            if (_mergedPasses > 0) data.Add(_mergedAppData);
            data.AddRange(entries.Select(e => e.Info.AppData));
            _mergedAppData = merge(data);
        }
        _merging.AddRange(entries);
        _mergedPasses += count;
        _lastMergedId = entries[^1].Info.Id;
        StartWorker();
    }

    // ---------- la tâche de fond ----------

    private void StartWorker()
    {
        if (_worker is { IsCompleted: false } || _merging.Count == 0) return;
        _mergeError = null;
        var token = _cancel.Token;
        _worker = Task.Factory.StartNew(() => MergeLoop(token), CancellationToken.None,
                                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void StopWorker()
    {
        Task? worker;
        lock (_gate)
        {
            _cancel.Cancel();
            worker = _worker;
        }
        try
        {
            worker?.Wait();
        }
        catch (AggregateException)
        {
            // la tâche de fond range ses erreurs dans _mergeError ; rien d'autre à attendre
        }
        lock (_gate)
        {
            _worker = null;
            _cancel.Dispose();
            _cancel = new CancellationTokenSource();
        }
    }

    /// <summary>
    /// Fusionne, une fusion à la fois, tout ce qui attend : écrit la base à
    /// côté (§7, étape 1), bascule l'agencement de l'état sous le verrou, puis
    /// supprime l'ancienne base et les segments fusionnés (étape 3). En cas
    /// d'échec, les fichiers à moitié écrits sont supprimés, l'état ne change
    /// pas et l'erreur est rangée.
    /// </summary>
    private void MergeLoop(CancellationToken ct)
    {
        Background.LowerPriority();
        while (true)
        {
            BaseLayer? old;
            List<Entry> job;
            string? appData;
            lock (_gate)
            {
                if (_merging.Count == 0 || ct.IsCancellationRequested) return;
                old = _base;
                job = _merging.ToList();
                _jobCount = job.Count;
                _reserved = Bound(old is null ? null : WeightOf(old), job.Select(e => Weight.Of(e.Segment)));
                // la fusion prend tout ce qui attend : sa base porte les AppData de toutes les passes fusionnées
                appData = _mergedAppData;
            }

            BaseLayer merged;
            try
            {
                Step("début", ct);
                merged = BaseLayer.Merge(_buffer, old, job.Select(e => e.Segment).ToList(), job[^1].Info.Id, appData, ct);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _reserved = 0;
                    _jobCount = 0;
                    if (!ct.IsCancellationRequested) _mergeError = ex;
                }
                return;
            }

            List<IBlob> dropped;
            try
            {
                Step("écrite", ct);
            }
            catch (Exception ex)
            {
                // interrompue entre l'écriture et la bascule : la base écrite ne sert plus
                var abandoned = merged.Files.ToList();
                lock (_gate)
                {
                    _reserved = 0;
                    _jobCount = 0;
                    _dropping.AddRange(abandoned);
                    if (!ct.IsCancellationRequested) _mergeError = ex;
                }
                Drop(abandoned);
                return;
            }

            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    dropped = merged.Files.ToList();
                }
                else
                {
                    dropped = job.Select(e => e.Segment.Blob).Concat(old?.Files ?? []).ToList();
                    _merging.RemoveRange(0, job.Count);
                    _base = merged;
                    var (bottom, layers) = Layout();
                    _buffer.Relayout(bottom, layers);
                }
                _reserved = 0;
                _jobCount = 0;
                // compté jusqu'à sa suppression : la fusion n'est finie qu'une fois l'étape 3 faite
                _dropping.AddRange(dropped);
            }
            try
            {
                Step("basculée", ct);
            }
            catch (OperationCanceledException)
            {
                // interrompue après la bascule : il reste à supprimer ce qu'elle a remplacé
            }
            finally
            {
                Drop(dropped);
            }
        }
    }

    /// <summary>Supprime ce qu'une fusion a remplacé ou abandonné, puis cesse de le compter.</summary>
    private void Drop(List<IBlob> files)
    {
        try
        {
            foreach (var blob in files) _buffer.Store.Delete(blob);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var blob in files) _dropping.Remove(blob);
            }
        }
    }

    private void Step(string step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (MergeHook is { } hook) hook(step, ct).GetAwaiter().GetResult();
        ct.ThrowIfCancellationRequested();
    }

    // ---------- budget ----------

    /// <summary>
    /// Combien des passes annulables, les plus anciennes, doivent fusionner
    /// après une passe : ce qui dépasse <see cref="JournalOptions.MaxUndoSteps"/>,
    /// et, sous budget, assez pour qu'une fois les fusions faites il reste la
    /// place d'une autre passe comme celle-ci et d'une base à réécrire. Au
    /// pire, toutes sauf la dernière.
    /// </summary>
    private int MergeCount(List<Weight> active)
    {
        int count = Math.Max(0, active.Count - _options.MaxUndoSteps);
        if (_options.DiskBudgetBytes is not long budget) return count;
        // une autre passe comme celle-ci, comptée comme EnsureRoom l'a comptée avant de la construire
        long margin = BaseLayer.DenseBytes(_buffer) + Math.Max(active[^1].Bytes, _lastEstimate);
        if (Usage() - _undo.Sum(e => e.Segment.Blob.Length) + active.Sum(w => w.Bytes) + margin <= budget) return count;
        for (int k = count; k < active.Count - 1; k++)
            if (Steady(active, k) + margin <= budget) return k;
        return Math.Max(count, active.Count - 1);
    }

    /// <summary>
    /// La place de la session sur le disque, sans ce qui est à rétablir :
    /// l'original, la base, la base en cours d'écriture, ce qu'une fusion a
    /// remplacé et qui n'est pas encore supprimé, les segments en fusion et annulables.
    /// </summary>
    private long Usage() =>
        _buffer.Length + (_base?.DiskBytes ?? 0) + _reserved + _dropping.Sum(b => b.Length)
        + _merging.Concat(_undo).Sum(e => e.Segment.Blob.Length);

    /// <summary>La place de la session, au pire, une fois écrite la base qui fusionne tout ce qui attend et les <paramref name="count"/> plus anciennes de <paramref name="active"/>.</summary>
    private long Steady(List<Weight> active, int count) =>
        _buffer.Length + NextBase(active, count) + active.Skip(count).Sum(w => w.Bytes);

    /// <summary>La base, au pire, une fois fusionnés tout ce qui attend et les <paramref name="count"/> plus anciennes de <paramref name="active"/>.</summary>
    private long NextBase(List<Weight> active, int count)
    {
        var pending = _merging.Select(e => Weight.Of(e.Segment)).Concat(active.Take(count)).ToList();
        var old = _base is null ? (Weight?)null : WeightOf(_base);
        return pending.Count == 0 ? old?.Bytes ?? 0 : Bound(old, pending);
    }

    /// <summary>
    /// La place, au pire, d'une base qui fusionne <paramref name="old"/> et
    /// <paramref name="passes"/>. Une base qui pourrait couvrir la moitié du
    /// fichier (écarts comblés compris, 11 octets au plus par plage) est
    /// comptée dense ; sinon, la somme de ce qu'elle fusionne, qui ne peut que
    /// se recouvrir.
    /// </summary>
    private long Bound(Weight? old, IEnumerable<Weight> passes)
    {
        long dense = BaseLayer.DenseBytes(_buffer);
        long bytes = old?.Bytes ?? 0, data = old?.Data ?? 0, runs = old?.Runs ?? 0;
        foreach (var w in passes)
        {
            bytes += w.Bytes;
            data += w.Data;
            runs += w.Runs;
        }
        return (data + (SegmentFormat.RunHeaderSize - 1) * runs) * 2 >= _buffer.Length ? dense : Math.Min(bytes, dense);
    }

    private Weight WeightOf(BaseLayer layer) =>
        layer.IsDense ? new Weight(layer.DiskBytes, _buffer.Length, 0) : Weight.Of(layer.Sparse!);

    /// <summary>Ce qu'une passe pèse pour le budget : sa place, ses octets écrits, ses plages.</summary>
    private readonly record struct Weight(long Bytes, long Data, long Runs)
    {
        public static Weight Of(Segment segment) => new(segment.Blob.Length, segment.DataBytes, segment.RunCount);
    }

    private sealed record Entry(PassInfo Info, Segment Segment);
}

/// <summary>Le journal vu d'un coup, pour les tests (<see cref="OperationJournal.Snapshot"/>).</summary>
internal sealed record JournalSnapshot(BaseLayer? Base, BufferState State, IReadOnlyList<IBlob> Files, bool Merging,
                                       long ReservedBytes, long DiskBytes);

/// <summary>
/// La tâche de fond passe après l'app : son fil a une priorité basse, et sous
/// macOS ses lectures et écritures disque aussi (<c>setiopolicy_np</c>,
/// <c>IOPOL_UTILITY</c>). Une fusion plus lente gêne moins la passe suivante,
/// et en regroupe davantage.
/// </summary>
internal static class Background
{
    private const int DiskIo = 0;        // IOPOL_TYPE_DISK
    private const int ThisThread = 1;    // IOPOL_SCOPE_THREAD
    private const int Utility = 4;       // IOPOL_UTILITY

    /// <summary>Faux pour mesurer l'effet de la priorité basse : le banc la compare à la priorité normale.</summary>
    internal static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("DGC_MERGE_PRIORITY") != "normale";

    public static void LowerPriority()
    {
        if (!Enabled) return;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            setiopolicy_np(DiskIo, ThisThread, Utility);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // sans politique d'E/S, la fusion garde la priorité normale
        }
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int setiopolicy_np(int type, int scope, int policy);
}
