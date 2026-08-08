using System.Buffers.Binary;
using System.Text;

namespace DataGlitchCore.Core;

/// <summary>
/// Un chunk RIFF rencontré par <see cref="Riff.Walk"/>, en profondeur : un
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
public sealed record RiffChunk(string Id, string? ListType, int Start, int Size)
{
    private bool IsList => ListType is not null;

    /// <summary>Offset du premier octet de charge utile réelle — après
    /// id+taille (8 octets), et après le FourCC de sous-type pour un LIST
    /// (4 octets de plus).</summary>
    public int DataStart => Start + 8 + (IsList ? 4 : 0);

    /// <summary>Taille de la charge utile réelle, hors FourCC de sous-type
    /// pour un LIST.</summary>
    public int DataSize => Size - (IsList ? 4 : 0);

    /// <summary>Offset du chunk frère suivant — padding d'alignement 2
    /// octets inclus (RIFF impose qu'un chunk de taille impaire soit suivi
    /// d'un octet de bourrage).</summary>
    public int NextStart => Start + 8 + Size + (Size & 1);
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
    /// <exception cref="RiffFormatException">
    /// Taille de chunk hors de portée d'un offset 32 bits (probablement un
    /// AVI OpenDML de plus de 2 Go, hors périmètre v1), ou chunk qui déborde
    /// de sa région — signaux de fichier corrompu ou atypique qu'il vaut
    /// mieux refuser proprement que mal interpréter.
    /// </exception>
    public static IReadOnlyList<RiffChunk> Walk(ReadOnlySpan<byte> data, int from, int to)
    {
        var result = new List<RiffChunk>();
        WalkInto(data, from, to, result);
        return result;
    }

    private static void WalkInto(ReadOnlySpan<byte> data, int from, int to, List<RiffChunk> into)
    {
        int pos = from;
        while (pos + 8 <= to)
        {
            string id = ReadFourCc(data, pos);
            uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos + 4, 4));
            if (declaredSize > int.MaxValue)
            {
                throw new RiffFormatException(
                    $"Chunk '{id}' à l'offset {pos} déclare une taille de {declaredSize} octets, " +
                    "hors de portée (offsets 32 bits) — probablement un AVI OpenDML (> 2 Go), hors périmètre.");
            }
            int size = (int)declaredSize;

            bool isList = id is "RIFF" or "LIST";
            if (isList && size < 4)
            {
                throw new RiffFormatException(
                    $"Chunk {id} à l'offset {pos} trop court ({size} octets) pour contenir un FourCC de sous-type.");
            }
            if (pos + 8 + size > to)
            {
                throw new RiffFormatException(
                    $"Chunk '{id}' à l'offset {pos} (taille déclarée {size}) déborde de sa région " +
                    $"(fin à {pos + 8 + size}, région jusqu'à {to}).");
            }

            string? listType = isList ? ReadFourCc(data, pos + 8) : null;
            var chunk = new RiffChunk(id, listType, pos, size);

            into.Add(chunk);
            if (isList) WalkInto(data, chunk.DataStart, chunk.DataStart + chunk.DataSize, into);

            pos = chunk.NextStart;
        }
    }

    private static string ReadFourCc(ReadOnlySpan<byte> data, int pos) =>
        Encoding.ASCII.GetString(data.Slice(pos, 4));
}
