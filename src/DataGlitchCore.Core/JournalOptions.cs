namespace DataGlitchCore.Core;

/// <summary>
/// Les bornes d'un <see cref="OperationJournal"/> (spec §7) : au-delà, les
/// passes les plus anciennes fusionnent dans la base et ne s'annulent plus
/// une à une. La dernière passe reste toujours annulable.
/// </summary>
public sealed class JournalOptions
{
    /// <summary>Passes annulables au plus (D7) : 20 par défaut, 1 au moins.</summary>
    public int MaxUndoSteps { get; init; } = 20;

    /// <summary>
    /// Budget disque de la session, en octets : l'original, la base et les
    /// segments, à rétablir compris. Au-delà, les passes les plus anciennes
    /// fusionnent ; une passe qui ne tiendrait pas, même après fusion, est
    /// refusée avant d'être construite. Sans budget, seul
    /// <see cref="MaxUndoSteps"/> borne l'historique.
    /// </summary>
    public long? DiskBudgetBytes { get; init; }

    /// <summary>
    /// Fusionne les <see cref="PassInfo.AppData"/> de passes qui entrent dans
    /// la base, de la plus ancienne à la plus récente ; la première est celle
    /// de la base, s'il y en a déjà une. Le résultat devient
    /// <see cref="OperationJournal.MergedAppData"/>. Sans fonction, la base n'en a pas.
    /// </summary>
    public Func<IReadOnlyList<string?>, string?>? MergeAppData { get; init; }
}

/// <summary>Une passe refusée parce qu'elle dépasserait le budget disque de la session. Rien n'a été modifié.</summary>
public sealed class DiskBudgetExceededException : InvalidOperationException
{
    public DiskBudgetExceededException(long neededBytes, long budgetBytes)
        : base($"Pas assez de place dans le budget disque de la session : cette passe demande jusqu'à {Units(neededBytes)}, " +
               $"pour un budget de {Units(budgetBytes)}. Rien n'a été modifié.")
    {
        NeededBytes = neededBytes;
        BudgetBytes = budgetBytes;
    }

    /// <summary>La place que la session occuperait au mieux, après fusion, avec la passe.</summary>
    public long NeededBytes { get; }

    public long BudgetBytes { get; }

    private static string Units(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} Gio",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} Mio",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} Kio",
        _ => $"{bytes} o",
    };
}
