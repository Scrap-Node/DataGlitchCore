using System.Collections.Concurrent;

namespace DataGlitchCore.Core;

/// <summary>
/// Une session gardée par un <see cref="SessionStore{TSession}"/> : un fichier
/// ouvert par l'app, son dossier, et ce qu'il coûte sur le disque (spec §8).
/// La fermer (<see cref="IDisposable.Dispose"/>) attend la requête en cours,
/// lâche ses fichiers et la RAM ; le store supprime ensuite son dossier.
/// </summary>
public interface IStoredSession : IDisposable
{
    string Id { get; }

    /// <summary>Le dossier de la session, sous la racine du store.</summary>
    string Directory { get; }

    /// <summary>Dernier usage, en horodatage de <see cref="TimeProvider"/> : le store le met à jour à chaque accès.</summary>
    long LastUse { get; set; }

    /// <summary>
    /// La place de la session sur le disque : l'original, la base, les
    /// segments et les caches. Pendant une fusion, la nouvelle base compte
    /// aussi (<see cref="OperationJournal.DiskBytes"/>).
    /// </summary>
    long DiskBytes { get; }

    /// <summary>
    /// Supprime ce qui se recalcule (aperçus, rendus) : la première place
    /// qu'on libère sous pression. Rend les octets libérés.
    /// </summary>
    long DropCaches();

    /// <summary>
    /// Attend que soit supprimé ce que <see cref="DiskBytes"/> compte encore
    /// sans que la session s'en serve : ce que son journal supprime en tâche
    /// de fond (<see cref="OperationJournal.WaitForDeletionsAsync"/>). Le store
    /// l'attend avant de libérer des caches ou de fermer des sessions faute de
    /// place, et les journaux avant de refuser une passe
    /// (<see cref="SessionStore{TSession}.WaitForDeletionsAsync"/>). Ne doit pas
    /// prendre le verrou des écritures de l'app (son Gate) : une passe d'une
    /// autre session l'attend en tenant le sien. Par défaut, rien à attendre.
    /// </summary>
    Task WaitForDeletionsAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Les réglages d'un <see cref="SessionStore{TSession}"/> (spec §8).
/// </summary>
/// <param name="Root">Le dossier sous lequel chaque session a le sien.</param>
/// <param name="MaxOpenSessions">Sessions ouvertes au plus ; au-delà, la moins récemment utilisée est fermée.</param>
/// <param name="IdleTimeout">Au-delà sans accès, une session est fermée par <see cref="SessionStore{TSession}.Sweep"/>.</param>
/// <param name="DiskBudgetBytes">Le budget disque de toutes les sessions : sources, segments et caches (D9).</param>
/// <param name="Retention">La durée de conservation d'une session fermée (D9). Sans effet avant le lot 5 : fermer une session la supprime.</param>
public sealed record StoreOptions(string Root, int MaxOpenSessions, TimeSpan IdleTimeout, long DiskBudgetBytes, TimeSpan Retention)
{
    /// <summary>Le budget disque recommandé (D9) : 20 Go.</summary>
    public const long DefaultDiskBudgetBytes = 20_000_000_000;

    /// <summary>La durée de conservation recommandée (D9) : 30 jours.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);

    /// <summary>Les réglages par défaut : 3 sessions ouvertes, fermées après 30 minutes sans accès, 20 Go, 30 jours.</summary>
    public static StoreOptions Defaults(string root) => new(root, 3, TimeSpan.FromMinutes(30), DefaultDiskBudgetBytes, DefaultRetention);
}

/// <summary>
/// Les sessions ouvertes d'une app, commun à DataBender et DataMosher (spec §8,
/// D4) : un dossier par session sous <see cref="StoreOptions.Root"/>, un
/// nombre de sessions ouvertes borné, un budget disque global.
///
/// Avant le lot 5, fermer une session supprime aussi son dossier : elle ne se
/// rouvre pas. Le store ne passe jamais le balai de lui-même : l'app appelle
/// <see cref="Sweep"/> (pas de <c>Microsoft.Extensions.Hosting</c> dans le cœur).
/// </summary>
public sealed class SessionStore<TSession> : IDisposable where TSession : class, IStoredSession
{
    private readonly ConcurrentDictionary<string, TSession> _sessions = new();
    private readonly Lock _evict = new();
    private readonly TimeProvider _time;
    private StoreOptions _options;

    public SessionStore(StoreOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _options = Validate(options);
        _time = time;
        System.IO.Directory.CreateDirectory(options.Root);
    }

    /// <summary>
    /// Les réglages en vigueur. Les changer (réglages globaux de l'app, D11)
    /// vaut pour la suite : moins de sessions ouvertes ou un budget plus serré
    /// s'appliquent au prochain <see cref="Add"/>, <see cref="TryReserve"/> ou
    /// <see cref="Sweep"/>. La racine, elle, ne change pas.
    /// </summary>
    public StoreOptions Options
    {
        get => Volatile.Read(ref _options);
        set
        {
            Validate(value);
            if (!string.Equals(Path.GetFullPath(value.Root), Path.GetFullPath(Root), StringComparison.Ordinal))
                throw new ArgumentException("La racine du store ne change pas en cours de route.", nameof(value));
            Volatile.Write(ref _options, value);
        }
    }

    public string Root => _options.Root;

    public int Count => _sessions.Count;

    /// <summary>Les sessions ouvertes, la plus récemment utilisée d'abord.</summary>
    public IReadOnlyList<TSession> Sessions => _sessions.Values.OrderByDescending(s => s.LastUse).ToList();

    /// <summary>La place de toutes les sessions ouvertes sur le disque.</summary>
    public long DiskBytes => _sessions.Values.Sum(s => s.DiskBytes);

    /// <summary>Un dossier neuf pour la prochaine session, et son identifiant.</summary>
    public (string Id, string Directory) CreateDirectory()
    {
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Root, id);
        System.IO.Directory.CreateDirectory(directory);
        return (id, directory);
    }

    /// <summary>
    /// Ajoute une session ouverte. Au-delà de <see cref="StoreOptions.MaxOpenSessions"/>,
    /// les moins récemment utilisées sont fermées, et leur dossier supprimé.
    /// </summary>
    public TSession Add(TSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.LastUse = _time.GetTimestamp();
        lock (_evict)
        {
            while (_sessions.Count >= Options.MaxOpenSessions && Oldest(except: null) is { } oldest)
                Delete(oldest.Id);
            _sessions[session.Id] = session;
        }
        return session;
    }

    public TSession? Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return null;
        session.LastUse = _time.GetTimestamp();
        return session;
    }

    /// <summary>
    /// Ferme une session : sa requête en cours finit, ses fichiers et sa RAM
    /// sont lâchés. Avant le lot 5, son dossier est supprimé aussi : elle ne se
    /// rouvre pas.
    /// </summary>
    public bool Close(string id) => Delete(id);

    /// <summary>Ferme une session et supprime son dossier.</summary>
    public bool Delete(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return false;
        try
        {
            session.Dispose();
        }
        finally
        {
            TryDeleteDirectory(session.Directory);
        }
        return true;
    }

    /// <summary>
    /// Le budget disque d'une session : le budget global, moins ce que
    /// prennent les autres. À donner à son journal
    /// (<see cref="JournalOptions.DiskBudget"/>) : une passe s'y mesure, fusion
    /// en cours et place d'une base comprises. Avec
    /// <see cref="WaitForDeletionsAsync"/> (<see cref="JournalOptions.WaitForOtherDeletions"/>),
    /// pour que ce que les autres suppriment encore ne fasse pas refuser la passe.
    /// </summary>
    public long BudgetFor(string id) =>
        Options.DiskBudgetBytes - _sessions.Values.Where(s => s.Id != id).Sum(s => s.DiskBytes);

    /// <summary>
    /// Attend les suppressions en cours de toutes les sessions
    /// (<see cref="IStoredSession.WaitForDeletionsAsync"/>) : la place qu'elles
    /// comptent encore sans s'en servir. N'attend aucune requête.
    /// </summary>
    public async Task WaitForDeletionsAsync(CancellationToken ct = default)
    {
        foreach (var session in _sessions.Values)
            await session.WaitForDeletionsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fait de la place pour <paramref name="bytes"/> octets, avant un import
    /// (spec §8), dans le budget global et sur le disque. D'abord, on attend
    /// ce que les sessions suppriment encore en tâche de fond
    /// (<see cref="IStoredSession.WaitForDeletionsAsync"/>) ; puis les caches
    /// des sessions, les moins récemment utilisées d'abord ; puis, pour un
    /// import, les sessions elles-mêmes, fermées et supprimées dans le même
    /// ordre. Pour une passe (<paramref name="requester"/> donné), on ne
    /// libère que des caches : une session ouverte n'est jamais fermée pour
    /// la passe d'une autre.
    /// </summary>
    /// <param name="bytes">La place demandée : la taille annoncée du fichier, ou celle d'une passe.</param>
    /// <param name="requester">La session qui demande, pour une passe ; nulle pour un import.</param>
    /// <returns>Vrai si la place est faite. Rien n'est réservé : la place est libre, à l'appelant de la prendre.</returns>
    public bool TryReserve(long bytes, string? requester = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        // ce que les sessions suppriment en tâche de fond compte jusqu'à sa suppression : on attend qu'elle soit
        // faite avant de libérer des caches ou de fermer des sessions. Hors du verrou, que les suppressions ne
        // prennent pas, mais qui retiendrait inutilement les autres appels.
        if (!Fits(bytes)) WaitForDeletionsAsync().GetAwaiter().GetResult();
        lock (_evict)
        {
            if (Fits(bytes)) return true;
            foreach (var session in _sessions.Values.OrderBy(s => s.Id == requester).ThenBy(s => s.LastUse).ToList())
            {
                session.DropCaches();
                if (Fits(bytes)) return true;
            }
            if (requester is not null) return false;
            while (Oldest(except: null) is { } oldest)
            {
                Delete(oldest.Id);
                if (Fits(bytes)) return true;
            }
            return Fits(bytes);
        }
    }

    /// <summary>
    /// Le balai, appelé par l'app à intervalles réguliers : ferme les sessions
    /// restées sans accès plus longtemps que <see cref="StoreOptions.IdleTimeout"/>,
    /// et au-delà du budget, supprime les caches des moins récemment utilisées.
    /// Il ne ferme jamais une session pour le budget.
    /// </summary>
    /// <returns>Le nombre de sessions fermées.</returns>
    public int Sweep()
    {
        int closed = 0;
        foreach (var session in _sessions.Values)
        {
            if (_time.GetElapsedTime(session.LastUse) > Options.IdleTimeout && Close(session.Id))
                closed++;
        }
        lock (_evict)
        {
            foreach (var session in _sessions.Values.OrderBy(s => s.LastUse).ToList())
            {
                if (DiskBytes <= Options.DiskBudgetBytes) break;
                session.DropCaches();
            }
        }
        return closed;
    }

    /// <summary>
    /// La taille maximale d'un fichier qu'on peut importer maintenant
    /// (spec §8) : le plus petit du plafond du format, du réglage de l'app, de
    /// la place libre sur le disque et de ce qui reste du budget, une fois
    /// libérés tous les caches et fermées toutes les sessions.
    /// </summary>
    /// <param name="formatCeilingBytes">Le plafond du format : 4 Gio pour un WAV, celui d'<c>AviIndex</c> pour un AVI.</param>
    /// <param name="settingBytes">La taille maximale réglée dans l'app (D11).</param>
    public LimitsDto Limits(long formatCeilingBytes, long settingBytes) =>
        Core.Limits.Describe(formatCeilingBytes, settingBytes, FreeSpace(), Options.DiskBudgetBytes);

    /// <summary>À l'arrêt de l'application : toutes les sessions sont fermées, et la racine supprimée.</summary>
    public void Dispose()
    {
        foreach (var id in _sessions.Keys) Delete(id);
        TryDeleteDirectory(Root);
    }

    private bool Fits(long bytes) => DiskBytes + bytes <= Options.DiskBudgetBytes && bytes <= FreeSpace();

    private TSession? Oldest(string? except) =>
        _sessions.Values.Where(s => s.Id != except).MinBy(s => s.LastUse);

    /// <summary>La place libre sur le disque de la racine ; un disque qu'on ne sait pas lire ne limite rien.</summary>
    private long FreeSpace()
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(Root)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private static StoreOptions Validate(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Root, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxOpenSessions, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.DiskBudgetBytes, nameof(options));
        if (options.IdleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Le délai d'inactivité doit être positif.");
        if (options.Retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "La durée de conservation ne peut pas être négative.");
        return options;
    }

    /// <summary>Un dossier supprimé au mieux : sous Windows, un fichier encore ouvert l'en empêche.</summary>
    public static void TryDeleteDirectory(string? path)
    {
        if (path is null) return;
        try
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Ce que <c>/limits</c> rend, le même dans les deux apps (spec §8, critère 10).</summary>
/// <param name="MaxFileBytes">La taille maximale d'un fichier importé maintenant : le plus petit des quatre suivants.</param>
/// <param name="FormatCeilingBytes">Le plafond du format.</param>
/// <param name="SettingBytes">La taille maximale réglée dans l'app.</param>
/// <param name="DiskFreeBytes">La place libre sur le disque des sessions.</param>
/// <param name="DiskBudgetBytes">Le budget disque de toutes les sessions.</param>
public sealed record LimitsDto(long MaxFileBytes, long FormatCeilingBytes, long SettingBytes, long DiskFreeBytes, long DiskBudgetBytes);

/// <summary>Le calcul des limites d'import (spec §8).</summary>
public static class Limits
{
    /// <summary>
    /// La taille maximale d'un fichier importé : le plus petit du plafond du
    /// format, du réglage, de la place libre et du budget. Le budget compte
    /// entier : <see cref="SessionStore{TSession}.TryReserve"/> libère la place
    /// des autres sessions avant l'import.
    /// </summary>
    public static LimitsDto Describe(long formatCeilingBytes, long settingBytes, long diskFreeBytes, long diskBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(formatCeilingBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(settingBytes);
        long max = Math.Max(0, Math.Min(Math.Min(formatCeilingBytes, settingBytes), Math.Min(diskFreeBytes, diskBudgetBytes)));
        return new LimitsDto(max, formatCeilingBytes, settingBytes, diskFreeBytes, diskBudgetBytes);
    }
}
