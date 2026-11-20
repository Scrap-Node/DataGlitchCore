using System.Text.Json;

namespace DataGlitchCore.Core;

/// <summary>
/// Les réglages globaux d'une app (spec §8, D11), à la place du curseur par
/// fichier : ils valent pour toutes les sessions, et se gardent d'un lancement
/// à l'autre dans les préférences de l'app.
/// </summary>
/// <param name="MaxFileBytes">La taille maximale d'un fichier importé.</param>
/// <param name="DiskBudgetBytes">Le budget disque de toutes les sessions (D9 : 20 Go).</param>
/// <param name="MaxUndoSteps">Les passes annulables d'une session (D7 : 20).</param>
/// <param name="Retention">La durée de conservation d'une session fermée (D9 : 30 jours ; lot 5).</param>
public sealed record SessionSettings(long MaxFileBytes, long DiskBudgetBytes, int MaxUndoSteps, TimeSpan Retention)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Les réglages recommandés par la spec, avec la taille maximale propre à l'app.</summary>
    public static SessionSettings Defaults(long maxFileBytes) =>
        new(maxFileBytes, StoreOptions.DefaultDiskBudgetBytes, new JournalOptions().MaxUndoSteps, StoreOptions.DefaultRetention);

    /// <summary>Des réglages valides : ce que l'app reçoit de l'utilisateur est vérifié ici.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Un réglage hors bornes, avec sa raison.</exception>
    public SessionSettings Validate()
    {
        if (MaxFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxFileBytes), "La taille maximale d'un fichier doit être positive.");
        if (DiskBudgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(DiskBudgetBytes), "Le budget disque ne peut pas être négatif.");
        if (MaxUndoSteps < 1) throw new ArgumentOutOfRangeException(nameof(MaxUndoSteps), "Il faut au moins une passe annulable.");
        if (Retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Retention), "La durée de conservation ne peut pas être négative.");
        return this;
    }

    /// <summary>
    /// Lit les réglages enregistrés. Un fichier absent, illisible ou hors
    /// bornes rend <paramref name="defaults"/> : un réglage perdu ne doit pas
    /// empêcher l'app de démarrer.
    /// </summary>
    public static SessionSettings Load(string path, SessionSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        try
        {
            if (!File.Exists(path)) return defaults;
            return (JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(path), Json) ?? defaults).Validate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentOutOfRangeException or NotSupportedException)
        {
            return defaults;
        }
    }

    /// <summary>Enregistre les réglages, tout ou rien : un plantage laisse les anciens ou les nouveaux, jamais un mélange.</summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await ExportFile.WriteAsync(path, (stream, token) => JsonSerializer.SerializeAsync(stream, this, Json, token), ct).ConfigureAwait(false);
    }
}
