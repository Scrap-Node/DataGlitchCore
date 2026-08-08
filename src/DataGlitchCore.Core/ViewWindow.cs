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

    public ViewWindow(double start, double end)
    {
        if (end < start) (start, end) = (end, start);
        Span = Math.Clamp(end - start, DefaultMinSpan, 1);
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
        if (factor <= 0 || double.IsNaN(factor)) return this;
        minSpan = Math.Clamp(minSpan, DefaultMinSpan, 1);

        double span = Math.Clamp(Span / factor, minSpan, 1);
        // fraction de la fenêtre située à gauche de l'ancre, conservée après zoom
        double left = Math.Clamp((anchor - Start) / Span, 0, 1);
        double start = Math.Clamp(anchor - left * span, 0, 1 - span);
        return new ViewWindow(start, start + span);
    }

    /// <summary>Fait glisser la fenêtre de <paramref name="delta"/> fraction fichier.</summary>
    public ViewWindow PanBy(double delta)
    {
        double span = Span;
        double start = Math.Clamp(Start + delta, 0, 1 - span);
        return new ViewWindow(start, start + span);
    }

    /// <summary>Recentre la fenêtre sur un point, sans changer son échelle.</summary>
    public ViewWindow CenteredOn(double fileFraction)
    {
        double span = Span;
        double start = Math.Clamp(fileFraction - span / 2, 0, 1 - span);
        return new ViewWindow(start, start + span);
    }

    /// <summary>Facteur de grossissement par rapport au fichier entier.</summary>
    public double Magnification => Span <= 0 ? 1 : 1 / Span;
}
