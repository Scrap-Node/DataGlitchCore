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
    /// segments, à rétablir compris, et pendant une fusion la nouvelle base
    /// aussi. Une passe n'est acceptée que si elle laisse la place d'écrire une
    /// base (une copie du fichier au plus) : le budget doit donc couvrir au
    /// moins trois fois le fichier, plus les passes annulables. Quand cette
    /// marge se resserre, les passes les plus anciennes fusionnent ; une passe
    /// qui ne tiendrait pas est refusée avant d'être construite. Sans budget,
    /// seul <see cref="MaxUndoSteps"/> borne l'historique.
    /// </summary>
    public long? DiskBudgetBytes { get; init; }

    /// <summary>
    /// Un budget qui change : le budget disque global des sessions, moins ce
    /// que prennent les autres (<see cref="SessionStore{TSession}.BudgetFor"/>).
    /// Lu à chaque vérification, hors du verrou du journal ; prime sur
    /// <see cref="DiskBudgetBytes"/>. Mêmes règles que lui.
    /// </summary>
    public Func<long>? DiskBudget { get; init; }

    /// <summary>
    /// Attend les suppressions en cours des autres sessions
    /// (<see cref="SessionStore{TSession}.WaitForDeletionsAsync"/>). Ce qu'un
    /// retour à l'original ou une passe abandonnent est supprimé en tâche de
    /// fond, et compte dans la place de sa session jusque-là : sous un budget
    /// global, il réduit celui des autres. Avant de refuser une passe faute de
    /// place, le journal attend cette fonction, une fois, hors de son verrou,
    /// puis recompte. Sans fonction, la passe est refusée sans attendre.
    /// </summary>
    public Func<Task>? WaitForOtherDeletions { get; init; }

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
        : base($"Pas assez de place dans le budget disque de la session : avec cette passe, et la place de réécrire sa base, " +
               $"la session demanderait jusqu'à {Units(neededBytes)}, pour un budget de {Units(budgetBytes)}. Rien n'a été modifié.")
    {
        NeededBytes = neededBytes;
        BudgetBytes = budgetBytes;
    }

    /// <summary>La place que demanderait la session : son disque, la passe, et la place de réécrire sa base.</summary>
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
