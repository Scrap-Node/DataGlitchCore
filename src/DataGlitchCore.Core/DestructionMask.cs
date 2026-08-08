namespace DataGlitchCore.Core;

/// <summary>Plage d'octets demi-ouverte <c>[Start, End)</c> — même convention
/// que la boucle de <see cref="DestructionEngine"/> et que
/// <c>RiffChunk.NextStart</c> côté format concret.</summary>
public readonly record struct ByteRange(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>
/// Où une passe de destruction a le droit d'écrire. Une classe, pas un
/// record : une liste de <see cref="ByteRange"/> compare par référence dans
/// l'égalité générée pour un record — piège garanti pour qui voudrait un
/// jour comparer deux masques (une clé de cache, par exemple) et obtiendrait
/// une inégalité alors que le contenu est identique.
///
/// Remplace le plancher unique de <c>HeaderInfo.PayloadStart</c> pour les
/// formats où le destructible et le protégé s'entrelacent (l'AVI : des
/// payloads de frame coupés par des en-têtes de chunk de 8 octets). Pour un
/// format à en-tête unique (le WAV), <see cref="FromPayloadStart"/>
/// reproduit exactement le comportement historique — <see cref="WholeBuffer"/>
/// et <see cref="FromPayloadStart"/> sont d'ailleurs le même mécanisme : un
/// plancher n'est jamais qu'une région unique, non bornée à droite
/// (<see cref="int.MaxValue"/>, ramenée à la taille réelle du buffer par
/// <see cref="ClampedTo"/> au moment de l'utiliser).
/// </summary>
public sealed class DestructionMask
{
    private DestructionMask(IReadOnlyList<ByteRange> regions) => Regions = regions;

    /// <summary>Régions triées par <see cref="ByteRange.Start"/>, disjointes
    /// (jamais adjacentes ni chevauchantes — fusionnées à la construction),
    /// toutes de longueur strictement positive.</summary>
    public IReadOnlyList<ByteRange> Regions { get; }

    /// <summary>Aucune protection : tout le buffer est destructible.</summary>
    public static DestructionMask WholeBuffer { get; } = FromPayloadStart(0);

    /// <summary>Un seul plancher : rien avant <paramref name="payloadStart"/>
    /// n'est destructible, tout le reste l'est.</summary>
    public static DestructionMask FromPayloadStart(int payloadStart) =>
        new([new ByteRange(Math.Max(0, payloadStart), int.MaxValue)]);

    /// <summary>
    /// Régions explicites, potentiellement entrelacées avec du protégé — le
    /// cas AVI. Normalise : jette les plages vides ou inversées (<c>End &lt;= Start</c>),
    /// trie par <see cref="ByteRange.Start"/>, fusionne les plages
    /// adjacentes ou qui se chevauchent. Rien n'est supposé côté appelant
    /// (ordre, disjonction) — c'est cette méthode qui le garantit.
    /// </summary>
    public static DestructionMask FromRegions(IEnumerable<ByteRange> regions)
    {
        var sorted = regions.Where(r => r.End > r.Start).OrderBy(r => r.Start).ToList();
        var merged = new List<ByteRange>(sorted.Count);
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                var last = merged[^1];
                if (r.End > last.End) merged[^1] = new ByteRange(last.Start, r.End);
            }
            else
            {
                merged.Add(r);
            }
        }
        return new DestructionMask(merged);
    }

    /// <summary>Régions ramenées à l'intérieur de <c>[0, bufferLength)</c> —
    /// c'est ce qui résout le plancher non borné de <see cref="WholeBuffer"/>
    /// et <see cref="FromPayloadStart"/> en une plage réelle. Les régions
    /// entièrement hors bornes disparaissent.</summary>
    public IReadOnlyList<ByteRange> ClampedTo(int bufferLength)
    {
        var result = new List<ByteRange>(Regions.Count);
        foreach (var r in Regions)
        {
            int start = Math.Clamp(r.Start, 0, bufferLength);
            int end = Math.Clamp(r.End, 0, bufferLength);
            if (end > start) result.Add(new ByteRange(start, end));
        }
        return result;
    }

    /// <summary>Somme des longueurs des régions, TELLES QUE STOCKÉES — sans
    /// borner à un buffer réel. N'a de sens que pour un masque construit à
    /// partir de régions finies explicites (<see cref="FromRegions"/>) ;
    /// pour <see cref="WholeBuffer"/>/<see cref="FromPayloadStart"/> (non
    /// bornés), appeler <see cref="ClampedTo"/> d'abord et sommer soi-même.</summary>
    public int TotalBytes
    {
        get
        {
            int total = 0;
            foreach (var r in Regions) total += r.Length;
            return total;
        }
    }
}
