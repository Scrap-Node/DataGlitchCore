namespace DataGlitchCore.Core;

/// <summary>
/// Charge un fichier entièrement en mémoire (dans la limite du cap RAM) et garde
/// deux copies : l'originale (immuable) pour la comparaison/reset, et la courante
/// que le moteur de destruction modifie. Les offsets sont des <see cref="int"/>,
/// donc le cap ne peut pas dépasser <see cref="MaxSupportedBytes"/> — c'est la
/// borne de l'indexation tableau, pas un choix de confort. Au-delà, il faudra
/// passer en memory-map.
///
/// Compter DEUX fois le cap en mémoire vive (originale + courante), et davantage
/// pendant le chargement si la taille du flux n'est pas connue d'avance : c'est
/// ce que le paramètre <c>expectedLength</c> des <c>Load</c> sert à éviter.
/// </summary>
public sealed class FileBuffer
{
    /// <summary>Cap par défaut quand l'appelant n'en impose pas. C'est un
    /// défaut, pas un plafond : voir <see cref="MaxSupportedBytes"/>.</summary>
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Plafond réel, au-delà duquel un <c>byte[]</c> ne peut pas aller. Rend
    /// explicite la borne qui était jusqu'ici implicite dans le choix d'offsets
    /// <see cref="int"/> — utile dès qu'un réglage laisse l'utilisateur monter
    /// le cap : sans elle, l'UI n'a aucun moyen de savoir où s'arrêter.
    /// </summary>
    public static readonly long MaxSupportedBytes = Array.MaxLength;

    private readonly byte[] _original;
    private readonly byte[] _current;

    private FileBuffer(string fileName, byte[] data, bool truncated)
    {
        FileName = fileName;
        _original = data;
        _current = (byte[])data.Clone();
        Truncated = truncated;
    }

    public string FileName { get; }

    /// <summary>Le fichier dépassait le cap RAM et a été tronqué au chargement.</summary>
    public bool Truncated { get; }

    public int Length => _current.Length;

    /// <summary>Octets d'origine, jamais modifiés (base de la diff et du reset).</summary>
    public ReadOnlySpan<byte> Original => _original;

    /// <summary>Octets courants, tels que modifiés par les passes de destruction.</summary>
    public ReadOnlySpan<byte> Current => _current;

    public static FileBuffer FromBytes(string fileName, byte[] data) => new(fileName, data, truncated: false);

    public static FileBuffer Load(string path, long maxBytes = DefaultMaxBytes)
    {
        using var fs = File.OpenRead(path);
        return Load(Path.GetFileName(path), fs, maxBytes);
    }

    /// <param name="fileName">Nom du fichier, gardé pour l'affichage et l'export.</param>
    /// <param name="stream">Flux lu jusqu'au plafond.</param>
    /// <param name="maxBytes">Plafond en octets : au-delà, le fichier est coupé et
    /// <see cref="Truncated"/> le signale.</param>
    /// <param name="expectedLength">Taille attendue quand l'appelant la connaît
    /// alors que le flux, lui, ne la connaît pas (un flux d'upload navigateur
    /// n'est pas seekable, mais le fichier annonce sa taille). Sert uniquement
    /// à allouer juste — voir <see cref="ReadCapped"/>.</param>
    public static FileBuffer Load(string fileName, Stream stream, long maxBytes = DefaultMaxBytes, long? expectedLength = null)
    {
        var (data, truncated) = ReadCapped(stream, maxBytes, expectedLength);
        return new FileBuffer(fileName, data, truncated);
    }

    /// <inheritdoc cref="Load(string,Stream,long,long?)"/>
    public static async Task<FileBuffer> LoadAsync(string fileName, Stream stream, long maxBytes = DefaultMaxBytes, long? expectedLength = null, CancellationToken ct = default)
    {
        var (data, truncated) = await ReadCappedAsync(stream, maxBytes, expectedLength, ct).ConfigureAwait(false);
        return new FileBuffer(fileName, data, truncated);
    }

    // --- accès bas niveau utilisés par le journal (apply/undo) ---

    internal byte[] Read(int offset, int length)
    {
        var slice = new byte[length];
        Array.Copy(_current, offset, slice, 0, length);
        return slice;
    }

    /// <summary>Lit dans l'état COURANT sans muter — utilisé par le moteur pour construire une passe.</summary>
    internal void ReadInto(int offset, Span<byte> destination) => _current.AsSpan(offset, destination.Length).CopyTo(destination);

    internal void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_current.AsSpan(offset, bytes.Length));

    public byte[] SnapshotCurrent() => (byte[])_current.Clone();

    /// <summary>Taille des écritures de <see cref="WriteCurrentToAsync(Stream, int, int, CancellationToken)"/> :
    /// autant de points où une annulation peut l'arrêter.</summary>
    internal const int WriteChunkSize = 1024 * 1024;

    /// <summary>
    /// Écrit l'état courant dans un flux, sans le cloner : un export ne coûte
    /// plus une seconde fois la taille du fichier en mémoire, comme avec
    /// <see cref="SnapshotCurrent"/>. Le buffer n'est pas verrouillé : à
    /// l'appelant d'empêcher toute passe de l'écrire pendant la copie.
    /// </summary>
    public Task WriteCurrentToAsync(Stream destination, CancellationToken ct = default) =>
        WriteCurrentToAsync(destination, 0, _current.Length, ct);

    /// <summary>Écrit une plage de l'état courant dans un flux, sans la cloner.</summary>
    /// <param name="destination">Flux où écrire.</param>
    /// <param name="offset">Premier octet de la plage.</param>
    /// <param name="count">Nombre d'octets à écrire.</param>
    /// <param name="ct">Annule entre deux morceaux.</param>
    public async Task WriteCurrentToAsync(Stream destination, int offset, int count, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _current.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(count), "La plage dépasse la fin du buffer.");

        for (int done = 0; done < count;)
        {
            int n = Math.Min(WriteChunkSize, count - done);
            await destination.WriteAsync(_current.AsMemory(offset + done, n), ct).ConfigureAwait(false);
            done += n;
        }
    }

    public void ResetToOriginal() => Array.Copy(_original, _current, _current.Length);

    /// <summary>
    /// Ramène un cap demandé dans le domaine du représentable. L'ancienne
    /// version retombait sur <see cref="DefaultMaxBytes"/> dès que le cap
    /// dépassait <see cref="int.MaxValue"/> : inoffensif tant que personne ne
    /// dépassait le défaut, mais dès qu'un réglage laisse monter le cap, ça
    /// tronquait un fichier de 2 Go à 200 Mo SANS RIEN DIRE. Un cap trop grand
    /// est une demande à satisfaire au mieux, pas une erreur à punir.
    /// </summary>
    private static long ClampCap(long maxBytes) =>
        maxBytes <= 0 ? DefaultMaxBytes : Math.Min(maxBytes, MaxSupportedBytes);

    /// <summary>Taille connue d'avance, ou null s'il faut lire pour savoir.</summary>
    private static long? KnownLength(Stream stream, long? expectedLength)
    {
        if (expectedLength is long n && n >= 0) return n;
        if (!stream.CanSeek) return null;
        long remaining = stream.Length - stream.Position;
        return remaining >= 0 ? remaining : null;
    }

    private static (byte[] data, bool truncated) ReadCapped(Stream stream, long maxBytes, long? expectedLength)
    {
        maxBytes = ClampCap(maxBytes);

        // Chemin dimensionné : quand la taille est connue, une seule allocation
        // à la bonne taille. Le chemin par MemoryStream ci-dessous fait grossir
        // son tampon par doublement PUIS le recopie dans ToArray(), soit un pic
        // à ~3x la taille du fichier, auquel le Clone() du constructeur ajoute
        // encore une copie. Invisible à 200 Mo, rédhibitoire près du plafond.
        if (KnownLength(stream, expectedLength) is long known)
        {
            int want = (int)Math.Min(known, maxBytes);
            var exact = new byte[want];
            int got = stream.ReadAtLeast(exact, want, throwOnEndOfStream: false);
            // le flux a menti sur sa taille : on garde ce qu'on a vraiment lu
            if (got < want) Array.Resize(ref exact, got);
            return (exact, known > maxBytes);
        }

        using var ms = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        bool truncated = false;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (total + read > maxBytes)
            {
                ms.Write(chunk, 0, (int)(maxBytes - total));
                truncated = true;
                break;
            }
            ms.Write(chunk, 0, read);
            total += read;
        }
        return (ms.ToArray(), truncated);
    }

    private static async Task<(byte[] data, bool truncated)> ReadCappedAsync(Stream stream, long maxBytes, long? expectedLength, CancellationToken ct)
    {
        maxBytes = ClampCap(maxBytes);

        if (KnownLength(stream, expectedLength) is long known)
        {
            int want = (int)Math.Min(known, maxBytes);
            var exact = new byte[want];
            int got = await stream.ReadAtLeastAsync(exact, want, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            if (got < want) Array.Resize(ref exact, got);
            return (exact, known > maxBytes);
        }

        using var ms = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        bool truncated = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (total + read > maxBytes)
            {
                await ms.WriteAsync(chunk.AsMemory(0, (int)(maxBytes - total)), ct).ConfigureAwait(false);
                truncated = true;
                break;
            }
            await ms.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
        }
        return (ms.ToArray(), truncated);
    }
}
