namespace DataGlitchCore.Ui;

/// <summary>Tracés d'une icône du set Scrap UI : au trait, plein, ou en pointillé.</summary>
public sealed record ScrapGlyph(string? D, string? Fill, string? Dash);

/// <summary>
/// Sous-ensemble du set d'icônes de @scrap-node/scrap-ui (grille de 20, trait de
/// 1,6), recopié de src/lib/icon.ts pour les hôtes Blazor. Mêmes noms, mêmes
/// tracés : une icône ajoutée ici se prend dans la lib, pas se redessine.
/// </summary>
public static class ScrapIcons
{
    private static readonly Dictionary<string, ScrapGlyph> Glyphs = new()
    {
        ["play"] = new(null, "M6.5 4.2v11.6L16 10z", null),
        ["pause"] = new(null, "M5.5 4.5h3v11h-3zM11.5 4.5h3v11h-3z", null),
        ["waveform"] = new("M3 10h1.5M6 7v6M8.5 4.5v11M11 8v4M13.5 5.5v9M16 9v2", null, null),
        ["film"] = new("M4.5 3.5h11a1.5 1.5 0 0 1 1.5 1.5v10a1.5 1.5 0 0 1 -1.5 1.5h-11a1.5 1.5 0 0 1 -1.5 -1.5v-10a1.5 1.5 0 0 1 1.5 -1.5ZM6.5 3.5v13M13.5 3.5v13M3 7h3.5M3 10h3.5M3 13h3.5M13.5 7H17M13.5 10H17M13.5 13H17", null, null),
        ["upload"] = new("M10 13V4M6.5 7.5L10 4l3.5 3.5M4 13.5v2.5h12v-2.5", null, null),
        ["download"] = new("M10 4v9M6.5 9.5L10 13l3.5 -3.5M4 13.5v2.5h12v-2.5", null, null),
        ["sun"] = new("M6.5 10a3.5 3.5 0 1 0 7 0a3.5 3.5 0 1 0 -7 0ZM10 2v2M10 16v2M2 10h2M16 10h2M4.3 4.3l1.4 1.4M14.3 14.3l1.4 1.4M4.3 15.7l1.4 -1.4M14.3 5.7l1.4 -1.4", null, null),
        ["moon"] = new("M16.5 12.2A7 7 0 0 1 7.8 3.5a7 7 0 1 0 8.7 8.7z", null, null),
        ["undo"] = new("M7.5 4.5L4 8l3.5 3.5M4.5 8H12a4 4 0 0 1 0 8h-2", null, null),
        ["redo"] = new("M12.5 4.5L16 8l-3.5 3.5M15.5 8H8a4 4 0 0 0 0 8h2", null, null),
        ["refresh"] = new("M15.5 8A6 6 0 0 0 4.6 7M4.5 12a6 6 0 0 0 10.9 1M15.5 3.5V8H11M4.5 16.5V12H9", null, null),
        ["minus"] = new("M5 10h10", null, null),
        ["plus"] = new("M10 4v12M4 10h12", null, null),
        ["chevron-right"] = new("M7.5 4.5l5.5 5.5l-5.5 5.5", null, null),
        ["warning"] = new("M10 3L18 17H2zM10 8.5v3.5M10 14.6v0.1", null, null),
        ["info"] = new("M2.5 10a7.5 7.5 0 1 0 15 0a7.5 7.5 0 1 0 -15 0ZM10 9v5M10 6.4v0.1", null, null),
        ["bolt"] = new("M11 2.5L4.5 11.5h4.5L8.5 17.5L15.5 8.5H11z", null, null),
        ["close"] = new("M5.5 5.5l9 9M14.5 5.5l-9 9", null, null),
        ["check-circle"] = new("M2.5 10a7.5 7.5 0 1 0 15 0a7.5 7.5 0 1 0 -15 0ZM6.8 10.2l2.2 2.2l4.4 -4.6", null, null),
        ["x-circle"] = new("M1.875 10a8.125 8.125 0 1 0 16.25 0a8.125 8.125 0 1 0 -16.25 0ZM7.25 7.25l5.5 5.5M12.75 7.25l-5.5 5.5", null, null),
        ["loop"] = new("M4 9V8a3 3 0 0 1 3 -3h9l-2.5 -2.5M16 11v1a3 3 0 0 1 -3 3H4l2.5 2.5", null, null),
        ["keyboard"] = new("M4 5h12a1.5 1.5 0 0 1 1.5 1.5v7a1.5 1.5 0 0 1 -1.5 1.5h-12a1.5 1.5 0 0 1 -1.5 -1.5v-7a1.5 1.5 0 0 1 1.5 -1.5ZM5.5 8h0.1M8.5 8h0.1M11.5 8h0.1M14.5 8h0.1M6.5 12h7", null, null),
        ["zoom-in"] = new("M3 8.5a5.5 5.5 0 1 0 11 0a5.5 5.5 0 1 0 -11 0ZM13 13l4.5 4.5M6 8.5h5M8.5 6v5", null, null),
        ["expand"] = new("M11.5 3.5h5v5M16.5 3.5L11 9M8.5 16.5h-5v-5M3.5 16.5L9 11", null, null),
        ["frame"] = new("M5 4.5h10a1.5 1.5 0 0 1 1.5 1.5v8a1.5 1.5 0 0 1 -1.5 1.5h-10a1.5 1.5 0 0 1 -1.5 -1.5v-8a1.5 1.5 0 0 1 1.5 -1.5ZM3.5 8h13", null, null),
        ["layers"] = new("M10 3L17 7l-7 4l-7 -4zM3 10.5l7 4l7 -4M3 14l7 4l7 -4", null, null),
        ["trash"] = new("M4 5.5h12M8 5.5V4h4v1.5M5.5 5.5l0.8 10.5h7.4l0.8 -10.5M8.5 8.5v5M11.5 8.5v5", null, null),
        ["restore"] = new("M4.5 10a5.5 5.5 0 1 0 1.8 -4.1M4 3.5v3h3", null, null),
        ["copy"] = new("M8.5 7h6.5a1.5 1.5 0 0 1 1.5 1.5v6.5a1.5 1.5 0 0 1 -1.5 1.5h-6.5a1.5 1.5 0 0 1 -1.5 -1.5v-6.5a1.5 1.5 0 0 1 1.5 -1.5ZM13 7V4.5a1 1 0 0 0 -1 -1H4.5a1 1 0 0 0 -1 1V12a1 1 0 0 0 1 1H7", null, null),
        ["skip-back"] = new("M5 4.5v11M15.5 4.5L8 10l7.5 5.5z", null, null),
        ["step-back"] = new("M6 5v10M15 5l-6.5 5l6.5 5z", null, null),
        ["step-forward"] = new("M14 5v10M5 5l6.5 5L5 15z", null, null),
        ["target"] = new("M3.5 10a6.5 6.5 0 1 0 13 0a6.5 6.5 0 1 0 -13 0ZM7.5 10a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0 -5 0Z", null, null),
        ["range"] = new("M3 4v12M17 4v12M6 10h8M8.5 7.5L6 10l2.5 2.5M11.5 7.5L14 10l-2.5 2.5", null, null),
        ["zone"] = new("M6.5 4H4v12h2.5M13.5 4H16v12h-2.5M8 10h4", null, null),
        ["file"] = new("M5 2.5h6.5l3.5 3.5v11.5H5zM11.5 2.5V6H15", null, null),
        ["spectrum"] = new("M3 16v-4M6 16V8M9 16V4.5M12 16V9M15 16v-5M18 16v-2", null, null),
        ["sidebar"] = new("M3.75 3.125h12.5a1.875 1.875 0 0 1 1.875 1.875v10a1.875 1.875 0 0 1 -1.875 1.875h-12.5a1.875 1.875 0 0 1 -1.875 -1.875v-10a1.875 1.875 0 0 1 1.875 -1.875ZM7.5 3.125v13.75", null, null),
        ["more"] = new(null, "M3.85 10a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0ZM8.85 10a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0ZM13.85 10a1.2 1.2 0 1 0 2.4 0a1.2 1.2 0 1 0 -2.4 0Z", null),
        ["image"] = new("M4.5 4h11a1.5 1.5 0 0 1 1.5 1.5v9a1.5 1.5 0 0 1 -1.5 1.5h-11a1.5 1.5 0 0 1 -1.5 -1.5v-9a1.5 1.5 0 0 1 1.5 -1.5ZM6 8.5a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0 -3 0ZM3.5 14.5l4 -4l3 3l2 -2l4 4", null, null),
        ["scissors"] = new("M3.8 6a2.2 2.2 0 1 0 4.4 0a2.2 2.2 0 1 0 -4.4 0ZM3.8 14a2.2 2.2 0 1 0 4.4 0a2.2 2.2 0 1 0 -4.4 0ZM7.8 7.3L16 14M7.8 12.7L16 6", null, null),
        ["node"] = new("M10 2.5L3.5 6.2v7.6L10 17.5l6.5 -3.7V6.2zM3.5 6.2L10 10l6.5 -3.8M10 10v7.5", null, null),
    };

    /// <summary>Tracés de l'icône ; un nom inconnu lève, pour qu'une faute de frappe se voie au premier rendu.</summary>
    public static ScrapGlyph Get(string name) =>
        Glyphs.TryGetValue(name, out var glyph)
            ? glyph
            : throw new ArgumentException($"Icône Scrap UI inconnue : « {name} ».", nameof(name));

    public static IEnumerable<string> Names => Glyphs.Keys;
}
