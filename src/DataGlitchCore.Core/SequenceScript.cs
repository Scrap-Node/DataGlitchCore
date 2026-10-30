namespace DataGlitchCore.Core;

/// <summary>
/// Une opération de montage sur une suite d'unités numérotées de 0 à N-1 : les
/// frames d'une vidéo, des blocs d'échantillons d'un son. Le core ne sait pas
/// ce qu'est une unité ; c'est le format qui sait la découper et la réécrire.
///
/// Les unités sont toujours désignées dans la numérotation D'ORIGINE, stable
/// quelles que soient les opérations déjà appliquées, contrairement à une
/// numérotation « après édition » qui se décale à chaque suppression.
///
/// Tous les champs sont des scalaires : <see cref="SequenceScript.ComputeHash"/>
/// se fonde sur le texte des opérations, où une liste n'apparaîtrait que par le
/// nom de son type, et deux collages différents y deviendraient identiques.
/// </summary>
/// <param name="Unit">L'unité visée ; pour un collage, celle après laquelle il joue.</param>
public abstract record SequenceOperation(int Unit);

/// <summary>Supprime l'unité : elle n'apparaît plus dans la sortie.</summary>
public sealed record DeleteUnit(int Unit) : SequenceOperation(Unit);

/// <summary>Répète l'unité <paramref name="Times"/> fois de suite : « ×20 » veut
/// dire 20 occurrences au total dans la sortie, pas 20 copies EN PLUS de
/// l'originale.</summary>
public sealed record RepeatUnit(int Unit, int Times) : SequenceOperation(Unit);

/// <summary>Rend à l'unité son état d'origine (ni supprimée, ni répétée). Une
/// vraie entrée de l'historique, qu'on annule et rétablit comme les autres. Ne
/// touche pas aux collages qui suivent l'unité : voir <see cref="ClearInsertions"/>.</summary>
public sealed record ClearUnit(int Unit) : SequenceOperation(Unit);

/// <summary>
/// Colle les unités d'origine <c>[First, First + Count)</c>, <paramref name="Times"/>
/// fois de suite, juste après les occurrences de l'unité <paramref name="After"/>.
/// La sortie s'allonge d'autant ; rien n'est remplacé.
///
/// Ce sont les unités telles qu'importées qui sont collées, pas leur montage :
/// une unité supprimée ou répétée dans la plage copiée n'y est ni absente ni
/// répétée. Plusieurs collages après la même unité s'enchaînent dans l'ordre où
/// ils ont été appliqués.
/// </summary>
/// <param name="After">L'unité d'origine après laquelle le collage joue.</param>
/// <param name="First">La première unité d'origine copiée.</param>
/// <param name="Count">Le nombre d'unités copiées.</param>
/// <param name="Times">Le nombre de copies collées à la suite.</param>
/// <param name="SkipKeyUnits">Laisse de côté les unités clés de la plage, celles
/// dont les suivantes dépendent (les I-frames d'une vidéo) : le format dit
/// lesquelles au moment du déroulé, voir <see cref="SequencePlan.Expand"/>.</param>
public sealed record InsertUnits(int After, int First, int Count, int Times, bool SkipKeyUnits)
    : SequenceOperation(After)
{
    /// <summary>Les unités d'une copie, dans l'ordre, sans les unités clés si
    /// <see cref="SkipKeyUnits"/> le demande.</summary>
    public IEnumerable<int> SourceUnits(Func<int, bool>? isKeyUnit = null)
    {
        for (int u = First; u < First + Count; u++)
        {
            if (SkipKeyUnits && isKeyUnit is not null && isKeyUnit(u)) continue;
            yield return u;
        }
    }

    /// <summary>Nombre d'unités que ce collage ajoute à la sortie, toutes copies comprises.</summary>
    public int SlotCount(Func<int, bool>? isKeyUnit = null) => SourceUnits(isKeyUnit).Count() * Times;
}

/// <summary>Retire tous les collages qui suivent l'unité <paramref name="After"/>,
/// sans annuler ce qui a été fait depuis.</summary>
public sealed record ClearInsertions(int After) : SequenceOperation(After);

/// <summary>
/// Le montage d'une suite d'unités : la liste ordonnée des opérations
/// appliquées. Il ne touche jamais aux octets ; c'est le format qui reconstruit
/// la sortie à partir du plan (<see cref="EffectivePlan"/>). Supprimer, répéter
/// ou coller change la longueur du fichier, ce qu'une <see cref="ByteEdit"/>
/// à longueur constante ne sait pas exprimer : les deux couches restent
/// séparées, mais suivent le même principe d'historique que
/// <see cref="OperationJournal"/>.
/// </summary>
public sealed class SequenceScript
{
    private readonly List<SequenceOperation> _applied = [];
    private readonly List<SequenceOperation> _redo = [];

    /// <summary>Les opérations appliquées, dans l'ordre, avant réduction (voir <see cref="EffectivePlan"/>).</summary>
    public IReadOnlyList<SequenceOperation> Applied => _applied;

    public bool CanUndo => _applied.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Apply(SequenceOperation operation)
    {
        Validate(operation);
        _applied.Add(operation);
        _redo.Clear(); // toute branche « refaire » devient invalide dès qu'on repart de l'avant
    }

    public void Undo()
    {
        if (_applied.Count == 0) return;
        var last = _applied[^1];
        _applied.RemoveAt(_applied.Count - 1);
        _redo.Add(last);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var op = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _applied.Add(op);
    }

    public void ResetAll()
    {
        _applied.Clear();
        _redo.Clear();
    }

    /// <summary>
    /// Hex court et stable, dérivé du contenu de <see cref="Applied"/> : une clé
    /// de cache pour un rendu. Deux scripts avec la même suite d'opérations ont
    /// la même clé, sans dépendre de l'identité de l'objet ni de
    /// <see cref="object.GetHashCode"/>, qui n'est pas stable.
    /// </summary>
    public string ComputeHash()
    {
        string text = string.Join(';', _applied);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// Réduit la liste ordonnée en un plan. Pour les occurrences propres d'une
    /// unité, la dernière opération l'emporte, et <see cref="ClearUnit"/> retire
    /// l'entrée (une unité rendue joue exactement comme une unité jamais
    /// touchée). Les collages s'accumulent après leur unité, jusqu'à un
    /// <see cref="ClearInsertions"/>.
    /// </summary>
    public SequencePlan EffectivePlan()
    {
        var units = new Dictionary<int, SequenceOperation>();
        var insertions = new Dictionary<int, List<InsertUnits>>();
        foreach (var op in _applied)
        {
            switch (op)
            {
                case ClearUnit:
                    units.Remove(op.Unit);
                    break;
                case DeleteUnit or RepeatUnit:
                    units[op.Unit] = op;
                    break;
                case InsertUnits insert:
                    if (!insertions.TryGetValue(insert.After, out var list))
                        insertions[insert.After] = list = [];
                    list.Add(insert);
                    break;
                case ClearInsertions:
                    insertions.Remove(op.Unit);
                    break;
            }
        }
        return new SequencePlan(units, insertions.ToDictionary(p => p.Key, p => (IReadOnlyList<InsertUnits>)p.Value));
    }

    private static void Validate(SequenceOperation operation)
    {
        if (operation.Unit < 0)
            throw new ArgumentOutOfRangeException(nameof(operation), "Une unité est numérotée à partir de 0.");
        switch (operation)
        {
            case RepeatUnit { Times: < 2 }:
                throw new ArgumentOutOfRangeException(nameof(operation),
                    "Times doit être >= 2 : au moins l'originale et une copie (1 ne répète rien).");
            case InsertUnits { First: < 0 }:
                throw new ArgumentOutOfRangeException(nameof(operation), "La plage collée commence avant l'unité 0.");
            case InsertUnits { Count: < 1 }:
                throw new ArgumentOutOfRangeException(nameof(operation), "La plage collée doit contenir au moins une unité.");
            case InsertUnits { Times: < 1 }:
                throw new ArgumentOutOfRangeException(nameof(operation), "Un collage se fait au moins une fois.");
        }
    }
}

/// <summary>
/// Le plan d'un <see cref="SequenceScript"/> : ce que devient chaque unité
/// d'origine, et ce qui est collé après elle.
/// </summary>
public sealed class SequencePlan
{
    internal SequencePlan(
        IReadOnlyDictionary<int, SequenceOperation> units,
        IReadOnlyDictionary<int, IReadOnlyList<InsertUnits>> insertions)
    {
        Units = units;
        Insertions = insertions;
    }

    /// <summary>Les unités d'origine dont les occurrences changent : un
    /// <see cref="DeleteUnit"/> ou un <see cref="RepeatUnit"/> par unité au plus.</summary>
    public IReadOnlyDictionary<int, SequenceOperation> Units { get; }

    /// <summary>Les collages actifs, rangés par unité après laquelle ils jouent,
    /// dans l'ordre où ils ont été appliqués.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<InsertUnits>> Insertions { get; }

    /// <summary>
    /// Déroule le plan sur les unités <c>0..unitCount-1</c>, dans l'ordre :
    /// chaque unité produit 0 (supprimée), 1 ou N (répétée) occurrences, puis
    /// les unités collées après elle.
    /// </summary>
    /// <param name="unitCount">Le nombre d'unités de la suite d'origine.</param>
    /// <param name="isKeyUnit">Dit quelles unités sont des unités clés, pour les
    /// collages qui les laissent de côté ; sans lui, aucune ne l'est.</param>
    /// <exception cref="ArgumentOutOfRangeException">Une opération vise une
    /// unité au-delà de <paramref name="unitCount"/> : le plan a été fait pour
    /// une autre suite.</exception>
    public SequenceOutput Expand(int unitCount, Func<int, bool>? isKeyUnit = null)
    {
        foreach (int unit in Units.Keys)
            if (unit >= unitCount)
                throw new ArgumentOutOfRangeException(nameof(unitCount), $"Le plan vise l'unité {unit}, au-delà des {unitCount} unités.");
        foreach (var (after, list) in Insertions)
        {
            if (after >= unitCount)
                throw new ArgumentOutOfRangeException(nameof(unitCount), $"Un collage suit l'unité {after}, au-delà des {unitCount} unités.");
            foreach (var insert in list)
                if (insert.First + insert.Count > unitCount)
                    throw new ArgumentOutOfRangeException(nameof(unitCount), $"Un collage copie au-delà des {unitCount} unités.");
        }

        var slots = new List<SequenceSlot>(unitCount);
        for (int unit = 0; unit < unitCount; unit++)
        {
            int own = Units.GetValueOrDefault(unit) switch
            {
                DeleteUnit => 0,
                RepeatUnit repeat => repeat.Times,
                _ => 1,
            };
            for (int i = 0; i < own; i++) slots.Add(new SequenceSlot(unit, unit, Inserted: false));

            if (!Insertions.TryGetValue(unit, out var list)) continue;
            foreach (var insert in list)
            {
                var sources = insert.SourceUnits(isKeyUnit).ToList();
                for (int t = 0; t < insert.Times; t++)
                    foreach (int source in sources)
                        slots.Add(new SequenceSlot(source, unit, Inserted: true));
            }
        }
        return new SequenceOutput(slots);
    }
}

/// <summary>Une unité de la sortie.</summary>
/// <param name="Source">L'unité d'origine dont elle recopie le contenu.</param>
/// <param name="Position">L'unité d'origine à la place de laquelle, ou après
/// laquelle, elle joue : sa place sur une timeline numérotée comme l'original.
/// Pour une occurrence propre, c'est <paramref name="Source"/> ; pour une unité
/// collée, c'est l'unité après laquelle le collage joue.</param>
/// <param name="Inserted">Elle vient d'un collage.</param>
public readonly record struct SequenceSlot(int Source, int Position, bool Inserted);

/// <summary>
/// La sortie déroulée, et la correspondance entre une position de la timeline
/// d'origine et ce qui joue à cet endroit. Les positions ne décroissent jamais
/// d'une unité de sortie à la suivante, par construction : une unité, puis ce
/// qui est collé après elle, puis l'unité suivante.
/// </summary>
public sealed class SequenceOutput
{
    internal SequenceOutput(IReadOnlyList<SequenceSlot> slots) => Slots = slots;

    public IReadOnlyList<SequenceSlot> Slots { get; }

    public int Count => Slots.Count;

    /// <summary>
    /// Indice, dans la sortie, de la première unité qui joue à la position
    /// <paramref name="position"/> ou après.
    ///
    /// Le cas qui compte est celui d'une unité SUPPRIMÉE : supprimer décale tout
    /// ce qui suit vers la gauche, ça ne fige pas l'unité précédente. Ce qui
    /// joue à cette place est la première unité qui suit : en vidéo, celle que
    /// le décodeur reconstruit sur une référence devenue fausse, donc celle qui
    /// porte la bavure. Montrer l'unité d'avant laisserait croire que la
    /// suppression n'a rien fait. Si l'unité supprimée est suivie d'un collage,
    /// c'est la première unité collée qui joue à sa place.
    /// </summary>
    /// <returns>-1 si la sortie est vide ; sinon un indice valide, borné au
    /// dernier quand rien ne joue à cette position ni après.</returns>
    public int FindSlot(int position)
    {
        if (Slots.Count == 0) return -1;
        int lo = 0, hi = Slots.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Slots[mid].Position < position) lo = mid + 1;
            else hi = mid;
        }
        return Math.Min(lo, Slots.Count - 1);
    }

    /// <summary>Nombre d'unités de sortie qui jouent à la position
    /// <paramref name="position"/> : ses occurrences propres et ce qui est collé
    /// après elle. Zéro pour une unité supprimée que rien ne suit.</summary>
    public int CountAt(int position)
    {
        int start = FindSlot(position);
        if (start < 0) return 0;
        int n = 0;
        for (int i = start; i < Slots.Count && Slots[i].Position == position; i++) n++;
        return n;
    }
}
