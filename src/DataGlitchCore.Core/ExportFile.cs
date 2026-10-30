namespace DataGlitchCore.Core;

/// <summary>
/// Un export vers un fichier choisi par l'utilisateur, tout ou rien. Il
/// s'écrit d'abord à côté de la cible, sous un nom temporaire caché, puis
/// prend sa place d'un coup avec <see cref="Commit"/>. Une erreur ou une
/// annulation en route ne laisse donc ni fichier à moitié écrit, ni fichier
/// existant écrasé : <see cref="Dispose"/> sans <see cref="Commit"/> supprime
/// le temporaire et ne touche pas à la cible.
///
/// Le temporaire est dans le dossier de la cible : sur le même volume, le
/// renommage final est atomique, là où un déplacement depuis le dossier
/// temporaire du système deviendrait une copie.
/// </summary>
public sealed class ExportFile : IDisposable
{
    private bool _committed;

    private ExportFile(string destination, string tempPath)
    {
        Destination = destination;
        TempPath = tempPath;
    }

    /// <summary>Le fichier final, en chemin absolu.</summary>
    public string Destination { get; }

    /// <summary>
    /// Où écrire, avant <see cref="Commit"/>. Un outil externe (ffmpeg…) peut
    /// y écrire directement, mais ne peut pas déduire le format de son
    /// extension (<c>.tmp</c>) : il faut le lui préciser.
    /// </summary>
    public string TempPath { get; }

    /// <summary>Prépare l'export ; rien n'est encore écrit.</summary>
    /// <param name="destination">Le fichier final. Son dossier doit exister.</param>
    public static ExportFile Create(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string full = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(full)
            ?? throw new ArgumentException("La destination doit être un fichier, pas une racine.", nameof(destination));
        string name = Path.GetFileName(full);
        if (name.Length == 0)
            throw new ArgumentException("La destination doit être un fichier, pas un dossier.", nameof(destination));

        // point en tête : caché dans le Finder pendant l'écriture
        string temp = Path.Combine(directory, $".{name}.{Guid.NewGuid().ToString("N")[..8]}.tmp");
        return new ExportFile(full, temp);
    }

    /// <summary>Ouvre le temporaire en écriture, vide.</summary>
    public FileStream OpenWrite() =>
        new(TempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 16, useAsync: true);

    /// <summary>Le temporaire prend la place de la cible, en la remplaçant si elle existe.</summary>
    public void Commit()
    {
        if (_committed) throw new InvalidOperationException("Cet export est déjà en place.");
        File.Move(TempPath, Destination, overwrite: true);
        _committed = true;
    }

    /// <summary>Sans <see cref="Commit"/>, supprime le temporaire.</summary>
    public void Dispose()
    {
        if (_committed) return;
        try
        {
            File.Delete(TempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // un temporaire caché qui reste vaut mieux qu'une exception qui masque la vraie erreur
        }
    }

    /// <summary>
    /// Écrit <paramref name="destination"/> d'un seul tenant : la cible
    /// n'apparaît, ou n'est remplacée, qu'une fois <paramref name="write"/>
    /// terminé sans erreur ni annulation.
    /// </summary>
    /// <param name="destination">Le fichier final.</param>
    /// <param name="write">Écrit le contenu dans le flux qu'on lui donne.</param>
    /// <param name="ct">Transmis à <paramref name="write"/>, et vérifié avant de mettre le fichier en place.</param>
    public static async Task WriteAsync(string destination, Func<Stream, CancellationToken, Task> write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        using var export = Create(destination);
        await using (var stream = export.OpenWrite())
        {
            await write(stream, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        export.Commit();
    }
}
