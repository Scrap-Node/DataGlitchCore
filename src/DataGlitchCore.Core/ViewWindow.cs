namespace DataGlitchCore.Core;

/// <summary>
/// Fenêtre temporelle visible, en fractions [0, 1] du fichier.
///
/// Le zoom ne se contente pas d'étirer l'image : les enveloppes/rendus sont
/// recalculés sur la fenêtre, donc le même nombre de colonnes couvre moins de
/// temps et fait apparaître du détail réel.
/// </summary>
public readonly record struct ViewWindow
{
    /// <summary>Fenêtre minimale par défaut, pour ne pas zoomer jusqu'à l'absurde.</summary>
    public const double DefaultMinSpan = 1e-4;

    public static readonly ViewWindow Full = new(0, 1);

    /// <summary>
    /// La fenêtre <c>[start, end]</c>, ramenée dans le fichier. Une borne hors
    /// de <c>[0, 1]</c> est ramenée au bord : une borne infinie donne la
    /// fenêtre entière. Une borne qui n'est pas un nombre (NaN, une requête mal
    /// formée) vaut l'infini de son côté, et donne donc elle aussi la fenêtre
    /// entière, au lieu d'une fenêtre faite de NaN.
    /// </summary>
    public ViewWindow(double start, double end)
    {
        if (double.IsNaN(start)) start = double.NegativeInfinity;
        if (double.IsNaN(end)) end = double.PositiveInfinity;
        if (end < start) (start, end) = (end, start);
        double span = end - start;
        // deux bornes infinies du même côté : ∞ - ∞ n'est pas un nombre
        Span = double.IsNaN(span) ? 1 : Math.Clamp(span, DefaultMinSpan, 1);
        Start = Math.Clamp(start, 0, 1 - Span);
    }

    public double Start { get; }

    /// <summary>
    /// Stockée telle quelle, et non déduite de <see cref="End"/> : sur une fenêtre
    /// étroite en fin de fichier, la soustraction perd assez de bits pour repasser
    /// sous le minimum et faire dériver le zoom.
    /// </summary>
    public double Span { get; }

    public double End => Math.Min(1, Start + Span);
    public bool IsFull => Span >= 1 - 1e-9;

    /// <summary>Fraction fichier → position [0, 1] dans la fenêtre visible.</summary>
    public double ToView(double fileFraction) => Span <= 0 ? 0 : (fileFraction - Start) / Span;

    /// <summary>Position [0, 1] dans la fenêtre → fraction fichier.</summary>
    public double ToFile(double viewFraction) => Start + viewFraction * Span;

    public bool Contains(double fileFraction) => fileFraction >= Start && fileFraction <= End;

    /// <summary>
    /// Zoome autour d'un point d'ancrage exprimé en fraction fichier : ce point
    /// reste sous le curseur. <paramref name="factor"/> supérieur à 1 rapproche.
    /// </summary>
    public ViewWindow ZoomAt(double anchor, double factor, double minSpan = DefaultMinSpan)
    {
        // pas de facteur ou pas d'ancre : rien ne bouge
        if (factor <= 0 || double.IsNaN(factor) || double.IsNaN(anchor)) return this;
        minSpan = double.IsNaN(minSpan) ? DefaultMinSpan : Math.Clamp(minSpan, DefaultMinSpan, 1);

        double span = Math.Clamp(Span / factor, minSpan, 1);
        // fraction de la fenêtre située à gauche de l'ancre, conservée après zoom
        double left = Math.Clamp((anchor - Start) / Span, 0, 1);
        double start = Math.Clamp(anchor - left * span, 0, 1 - span);
        return new ViewWindow(start, start + span);
    }

    /// <summary>Fait glisser la fenêtre de <paramref name="delta"/> fraction fichier.</summary>
    public ViewWindow PanBy(double delta)
    {
        if (double.IsNaN(delta)) return this;
        double span = Span;
        double start = Math.Clamp(Start + delta, 0, 1 - span);
        return new ViewWindow(start, start + span);
    }

    /// <summary>Recentre la fenêtre sur un point, sans changer son échelle.</summary>
    public ViewWindow CenteredOn(double fileFraction)
    {
        if (double.IsNaN(fileFraction)) return this;
        double span = Span;
        double start = Math.Clamp(fileFraction - span / 2, 0, 1 - span);
        return new ViewWindow(start, start + span);
    }

    /// <summary>Facteur de grossissement par rapport au fichier entier.</summary>
    public double Magnification => Span <= 0 ? 1 : 1 / Span;
}
