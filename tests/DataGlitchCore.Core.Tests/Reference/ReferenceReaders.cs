using System.Buffers.Binary;
using System.Text;

namespace DataGlitchCore.Core.Tests.Reference;

// ORACLE — ne pas modifier.
//
// DiffMap et Riff de DataGlitchCore 0.2.0 (24c3d18), recopiés tels quels
// (spec dataglitchcore-buffer, §13.1) : les lectures par ByteView doivent
// rendre exactement ce que rendaient les lectures sur tout le fichier.

internal static class ReferenceDiffMap
{
    public static float[] Compute(ReadOnlySpan<byte> original, ReadOnlySpan<byte> current, int buckets, int start = 0, int length = -1)
    {
        buckets = Math.Max(1, buckets);
        var result = new float[buckets];

        int n = Math.Min(original.Length, current.Length);
        if (length < 0) length = n - start;
        start = Math.Clamp(start, 0, n);
        length = Math.Clamp(length, 0, n - start);
        if (length == 0) return result;

        double perBucket = (double)length / buckets;
        for (int b = 0; b < buckets; b++)
        {
            int o0 = start + (int)(b * perBucket);
            int o1 = b == buckets - 1 ? start + length : start + (int)((b + 1) * perBucket);
            if (o1 <= o0) o1 = Math.Min(o0 + 1, start + length);

            int changed = 0;
            for (int i = o0; i < o1; i++)
                if (original[i] != current[i]) changed++;

            result[b] = (float)changed / (o1 - o0);
        }
        return result;
    }
}

internal sealed record ReferenceRiffChunk(string Id, string? ListType, int Start, int Size)
{
    private bool IsList => ListType is not null;
    public int DataStart => Start + 8 + (IsList ? 4 : 0);
    public int DataSize => Size - (IsList ? 4 : 0);
    public int NextStart => Start + 8 + Size + (Size & 1);
}

internal static class ReferenceRiff
{
    public static IReadOnlyList<ReferenceRiffChunk> Walk(ReadOnlySpan<byte> data, int from, int to, bool tolerateTruncation = false)
    {
        var result = new List<ReferenceRiffChunk>();
        WalkInto(data, from, to, result, tolerateTruncation);
        return result;
    }

    private static void WalkInto(ReadOnlySpan<byte> data, int from, int to, List<ReferenceRiffChunk> into, bool tolerate)
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
            long end = (long)pos + 8 + size;
            if (end > to)
            {
                if (!tolerate)
                {
                    throw new RiffFormatException(
                        $"Chunk '{id}' à l'offset {pos} (taille déclarée {size}) déborde de sa région " +
                        $"(fin à {end}, région jusqu'à {to}).");
                }

                size = to - pos - 8;
                if (isList && size < 4) return;
            }

            string? listType = isList ? ReadFourCc(data, pos + 8) : null;
            var chunk = new ReferenceRiffChunk(id, listType, pos, size);

            into.Add(chunk);
            if (isList) WalkInto(data, chunk.DataStart, chunk.DataStart + chunk.DataSize, into, tolerate);

            pos = chunk.NextStart;
        }
    }

    private static string ReadFourCc(ReadOnlySpan<byte> data, int pos) =>
        Encoding.ASCII.GetString(data.Slice(pos, 4));
}
