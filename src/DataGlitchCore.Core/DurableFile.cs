using System.Runtime.InteropServices;

namespace DataGlitchCore.Core;

/// <summary>
/// L'écriture sûre : un fichier est forcé sur le disque avant d'être renommé à
/// sa place, puis son dossier est forcé à son tour, pour que le renommage
/// survive lui aussi à une coupure de courant. <see cref="ExportFile"/> s'en
/// sert dès la 0.3.0 ; les segments et le manifeste d'une session suivront
/// (DataGlitchCore#7).
///
/// Sous macOS, <c>fsync</c> ne vide pas le cache du disque : il faut
/// <c>fcntl(F_FULLFSYNC)</c>. <see cref="FileStream.Flush(bool)"/> le fait
/// déjà pour un fichier : le <c>SystemNative_FSync</c> de .NET 9 appelle
/// <c>F_FULLFSYNC</c> sous macOS. Un dossier, que .NET refuse d'ouvrir, passe
/// par la libc. Sous Windows, il n'y a pas de dossier à forcer : le fichier
/// l'est, le renommage reste à la charge du système.
/// </summary>
internal static class DurableFile
{
    /// <summary>
    /// Pour les tests : reçoit chaque étape (<c>fichier</c>, <c>renommage</c>,
    /// <c>dossier</c>) et son chemin, dans l'ordre où elles ont lieu. Propre
    /// au flot d'exécution qui l'a posé.
    /// </summary>
    internal static readonly AsyncLocal<Action<string, string>?> Observer = new();

    /// <summary>Force sur le disque le contenu d'un fichier déjà écrit et fermé.</summary>
    public static void FlushFile(string path)
    {
        // en écriture, sans tampon : Flush(true) n'a rien à vider, il force seulement le disque
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0))
            stream.Flush(flushToDisk: true);
        Observer.Value?.Invoke("fichier", path);
    }

    /// <summary>Renomme <paramref name="source"/> à la place de <paramref name="destination"/>, d'un coup.</summary>
    public static void Move(string source, string destination)
    {
        File.Move(source, destination, overwrite: true);
        Observer.Value?.Invoke("renommage", destination);
    }

    /// <summary>Force sur le disque les entrées d'un dossier : un renommage ou une suppression qui vient d'y avoir lieu.</summary>
    public static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows()) Posix.FlushDirectory(directory);
        Observer.Value?.Invoke("dossier", directory);
    }

    private static class Posix
    {
        private const int ReadOnly = 0;        // O_RDONLY
        private const int FullFileSync = 51;   // F_FULLFSYNC, macOS

        private const int NoEntry = 2;         // ENOENT
        private const int Interrupted = 4;     // EINTR
        private const int AccessDenied = 13;   // EACCES
        private const int NotADirectory = 20;  // ENOTDIR
        private const int Invalid = 22;        // EINVAL
        private const int ReadOnlyFs = 30;     // EROFS

        // ENOTSUP n'a pas la même valeur partout
        private static int NotSupported => OperatingSystem.IsMacOS() ? 45 : 95;

        public static void FlushDirectory(string directory)
        {
            int fd;
            do fd = Open(directory, ReadOnly);
            while (fd < 0 && Marshal.GetLastPInvokeError() == Interrupted);
            if (fd < 0) throw Error(Marshal.GetLastPInvokeError(), directory);

            try
            {
                int errno = Sync(fd, full: OperatingSystem.IsMacOS());
                // F_FULLFSYNC refusé (volume réseau, exFAT…) : fsync, à défaut de mieux
                if (OperatingSystem.IsMacOS() && (errno == NotSupported || errno == Invalid))
                    errno = Sync(fd, full: false);

                // comme FileStream.Flush(true) : un système de fichiers qui ne sait pas forcer n'a rien à forcer
                if (errno != 0 && errno != Invalid && errno != ReadOnlyFs && errno != NotSupported)
                    throw Error(errno, directory);
            }
            finally
            {
                Close(fd);
            }
        }

        /// <summary>0, ou le code d'erreur.</summary>
        private static int Sync(int fd, bool full)
        {
            while (true)
            {
                int result = full ? Fcntl(fd, FullFileSync) : FSync(fd);
                if (result >= 0) return 0;
                int errno = Marshal.GetLastPInvokeError();
                if (errno != Interrupted) return errno;
            }
        }

        private static Exception Error(int errno, string path)
        {
            string message = $"{Marshal.GetPInvokeErrorMessage(errno)} : {path}";
            return errno switch
            {
                NoEntry or NotADirectory => new DirectoryNotFoundException(message),
                AccessDenied => new UnauthorizedAccessException(message),
                _ => new IOException(message),
            };
        }

        // "libc" : le runtime le résout vers la libc du système (libc.so.6, /usr/lib/libc.dylib).
        // open et fcntl sont variadiques ; seuls leurs arguments nommés sont passés ici.
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        private static extern int FSync(int fd);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        private static extern int Fcntl(int fd, int command);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);
    }
}
