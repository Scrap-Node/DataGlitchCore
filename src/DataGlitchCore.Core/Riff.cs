using System.Buffers.Binary;
using System.Text;

namespace DataGlitchCore.Core;

/// <summary>
/// Un chunk RIFF rencontré par <c>Riff.Walk</c>, en profondeur : un
/// LIST est renvoyé lui-même, immédiatement suivi de ses enfants, avant le
/// frère suivant.
/// </summary>
/// <param name="Id">FourCC du chunk (ex. "fmt ", "data", "LIST", "idx1"...).</param>
/// <param name="ListType">Pour un chunk RIFF/LIST, son FourCC de sous-type
/// (ex. "AVI ", "movi", "hdrl") ; null pour un chunk ordinaire.</param>
/// <param name="Start">Offset, dans le buffer, du tout premier octet du
/// chunk (son FourCC "Id").</param>
/// <param name="Size">Taille déclarée dans l'entête — tout ce qui suit le
/// champ de taille. Pour un LIST, ceci inclut les 4 octets de
/// <see cref="ListType"/> ; utiliser <see cref="DataSize"/> pour la charge
/// utile réelle.</param>
public sealed record RiffChunk(string Id, string? ListType, long Start, long Size)
{
    private bool IsList => ListType is not null;

    /// <summary>Offset du premier octet de charge utile réelle — après
    /// id+taille (8 octets), et après le FourCC de sous-type pour un LIST
    /// (4 octets de plus).</summary>
    public long DataStart => Start + 8 + (IsList ? 4 : 0);

    /// <summary>Taille de la charge utile réelle, hors FourCC de sous-type
    /// pour un LIST.</summary>
    public long DataSize => Size - (IsList ? 4 : 0);

    /// <summary>Offset du chunk frère suivant — padding d'alignement 2
    /// octets inclus (RIFF impose qu'un chunk de taille impaire soit suivi
    /// d'un octet de bourrage).</summary>
    public long NextStart => Start + 8 + Size + (Size & 1);
}

public sealed class RiffFormatException(string message) : Exception(message);

/// <summary>
/// Parcours RIFF récursif, agnostique au format qui l'utilise (WAV comme
/// AVI partagent le même conteneur — seul ce qu'il y a dans les chunks
/// diffère). Contrairement à un walker "à plat", il descend dans chaque
/// LIST rencontré au lieu de le sauter comme un chunk opaque — indispensable
/// pour atteindre les chunks de trame sous "LIST movi" d'un AVI.
/// </summary>
public static class Riff
{
    /// <summary>
    /// Parcourt une région RIFF en profondeur. <paramref name="from"/> est
    /// l'offset du premier chunk de la région (son FourCC, pas son contenu),
    /// <paramref name="to"/> la fin de la région (exclusif) — pour un fichier
    /// entier : <c>Walk(data, 0, data.Length)</c>, ce qui renvoie le chunk
    /// "RIFF" lui-même en premier, puis descend dedans.
    /// </summary>
    /// <param name="data">Le fichier, ou au moins la région parcourue.</param>
    /// <param name="from">Offset du premier chunk de la région.</param>
    /// <param name="to">Fin de la région, exclusive.</param>
    /// <param name="tolerateTruncation">
    /// Accepter un fichier coupé net. Un chunk qui déborde de sa région est
    /// alors ramené à ce qui est réellement présent au lieu de faire lever.
    /// Indispensable en aval de <see cref="FileBuffer"/>, qui tronque au cap
    /// RAM : passé le cap, le chunk de charge utile annonce forcément plus
    /// d'octets qu'il n'en reste, et le fichier serait rejeté alors qu'il est
    /// parfaitement exploitable jusqu'à la coupure. À laisser à <c>false</c>
    /// quand la troncature n'est pas attendue — c'est sinon un symptôme de
    /// corruption qu'il vaut mieux voir.
    /// </param>
    /// <exception cref="RiffFormatException">
    /// Taille de chunk hors de portée d'un offset 32 bits (probablement un
    /// AVI OpenDML de plus de 2 Go, hors périmètre v1), LIST trop court pour
    /// porter son FourCC de sous-type, ou — hors mode tolérant — chunk qui
    /// déborde de sa région : signaux de fichier corrompu ou atypique qu'il
    /// vaut mieux refuser proprement que mal interpréter.
    /// </exception>
    public static IReadOnlyList<RiffChunk> Walk(ReadOnlySpan<byte> data, int from, int to, bool tolerateTruncation = false)
    {
        var result = new List<RiffChunk>();
        WalkInto(new SpanSource(data), from, to, result, tolerateTruncation);
        return result;
    }

    /// <summary>
    /// Le même parcours, sur un état lu par fenêtres : seuls les en-têtes de
    /// chunk sont lus, jamais les charges utiles. Pour un fichier entier :
    /// <c>Walk(view, 0, view.Length)</c>.
    /// </summary>
    /// <param name="data">L'état à parcourir, typiquement <see cref="FileBuffer.CurrentView"/>.</param>
    /// <param name="from">Offset du premier chunk de la région.</param>
    /// <param name="to">Fin de la région, exclusive.</param>
    /// <param name="tolerateTruncation">Voir <see cref="Walk(ReadOnlySpan{byte}, int, int, bool)"/>.</param>
    /// <exception cref="RiffFormatException">Voir <see cref="Walk(ReadOnlySpan{byte}, int, int, bool)"/>.</exception>
    public static IReadOnlyList<RiffChunk> Walk(ByteView data, long from, long to, bool tolerateTruncation = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = new List<RiffChunk>();
        using var window = new ByteWindow(data, HeaderWindowSize);
        WalkInto(new ViewSource(window), from, to, result, tolerateTruncation);
        return result;
    }

    /// <summary>
    /// Fenêtre de lecture des en-têtes : assez grande pour servir d'un coup
    /// les en-têtes rapprochés (petites frames, chunks de hdrl), assez petite
    /// pour ne pas relire beaucoup plus que l'en-tête d'une grosse frame.
    /// </summary>
    private const int HeaderWindowSize = 16 * 1024;

    private static void WalkInto<TSource>(TSource data, long from, long to, List<RiffChunk> into, bool tolerate)
        where TSource : IHeaderSource, allows ref struct
    {
        long pos = from;
        while (pos + 8 <= to)
        {
            string id = data.ReadFourCc(pos);
            uint declaredSize = data.ReadUInt32(pos + 4);
            if (declaredSize > int.MaxValue)
            {
                throw new RiffFormatException(
                    $"Chunk '{id}' à l'offset {pos} déclare une taille de {declaredSize} octets, " +
                    "hors de portée (offsets 32 bits) — probablement un AVI OpenDML (> 2 Go), hors périmètre.");
            }
            long size = declaredSize;

            bool isList = id is "RIFF" or "LIST";
            if (isList && size < 4)
            {
                throw new RiffFormatException(
                    $"Chunk {id} à l'offset {pos} trop court ({size} octets) pour contenir un FourCC de sous-type.");
            }
            // En long : une taille proche de int.MaxValue ferait déborder la
            // somme en négatif et passer ce test sans rien rejeter ni raboter.
            long end = (long)pos + 8 + size;
            if (end > to)
            {
                if (!tolerate)
                {
                    throw new RiffFormatException(
                        $"Chunk '{id}' à l'offset {pos} (taille déclarée {size}) déborde de sa région " +
                        $"(fin à {end}, région jusqu'à {to}).");
                }

                // Fichier coupé net (typiquement au cap RAM) : on ramène le
                // chunk au réel. Rien à parcourir après lui par construction —
                // la boucle s'arrêtera d'elle-même, NextStart tombant hors
                // région. Sur un RIFF racine tronqué, la descente récursive
                // rejoue exactement le même raisonnement un cran plus bas, si
                // bien que le chunk de charge utile est raboté à son tour.
                size = to - pos - 8;

                // tronqué avant même d'avoir de quoi lire le sous-type : il ne
                // reste rien d'exploitable dans cette région
                if (isList && size < 4) return;
            }

            string? listType = isList ? data.ReadFourCc(pos + 8) : null;
            var chunk = new RiffChunk(id, listType, pos, size);

            into.Add(chunk);
            if (isList) WalkInto(data, chunk.DataStart, chunk.DataStart + chunk.DataSize, into, tolerate);

            pos = chunk.NextStart;
        }
    }

    /// <summary>Ce que le parcours lit : un FourCC et une taille, aux offsets des en-têtes.</summary>
    private interface IHeaderSource
    {
        string ReadFourCc(long pos);
        uint ReadUInt32(long pos);
    }

    private readonly ref struct SpanSource(ReadOnlySpan<byte> data) : IHeaderSource
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public string ReadFourCc(long pos) => Encoding.ASCII.GetString(_data.Slice(checked((int)pos), 4));

        public uint ReadUInt32(long pos) => BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(checked((int)pos), 4));
    }

    private readonly struct ViewSource(ByteWindow window) : IHeaderSource
    {
        public string ReadFourCc(long pos) => Encoding.ASCII.GetString(window.Exactly(pos, 4));

        public uint ReadUInt32(long pos) => BinaryPrimitives.ReadUInt32LittleEndian(window.Exactly(pos, 4));
    }
}
