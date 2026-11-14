using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace DataGlitchCore.Core;

/// <summary>
/// Carte de différence au niveau octet entre l'original et l'état courant :
/// pour chaque colonne, la fraction d'octets réellement modifiés (0..1).
/// Contrairement à une comparaison d'enveloppes (qui sature vite), c'est une
/// mesure exacte de ce que la destruction a touché — et elle marche sur
/// n'importe quel format.
/// </summary>
public static class DiffMap
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

    /// <summary>
    /// La même carte, lue par fenêtres d'1 Mio dans les deux états : rien n'est
    /// copié en entier, et le résultat est identique à celui de la surcharge
    /// sur les spans.
    /// </summary>
    /// <param name="original">Typiquement <see cref="FileBuffer.OriginalView"/>.</param>
    /// <param name="current">Typiquement <see cref="FileBuffer.CurrentView"/>.</param>
    /// <param name="buckets">Nombre de colonnes.</param>
    /// <param name="start">Premier octet comparé.</param>
    /// <param name="length">Nombre d'octets comparés ; négatif : jusqu'à la fin.</param>
    public static float[] Compute(ByteView original, ByteView current, int buckets, long start = 0, long length = -1)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);

        buckets = Math.Max(1, buckets);
        var result = new float[buckets];

        long n = Math.Min(original.Length, current.Length);
        if (length < 0) length = n - start;
        start = Math.Clamp(start, 0, n);
        length = Math.Clamp(length, 0, n - start);
        if (length == 0) return result;

        using var a = new ByteWindow(original, WindowSize);
        using var b = new ByteWindow(current, WindowSize);
        long end = start + length;
        double perBucket = (double)length / buckets;
        for (int k = 0; k < buckets; k++)
        {
            long o0 = start + (long)(k * perBucket);
            long o1 = k == buckets - 1 ? end : start + (long)((k + 1) * perBucket);
            if (o1 <= o0) o1 = Math.Min(o0 + 1, end);

            long changed = 0;
            for (long pos = o0; pos < o1;)
            {
                int max = (int)Math.Min(o1 - pos, int.MaxValue);
                var x = a.From(pos, max);
                var y = b.From(pos, max);
                int count = Math.Min(x.Length, y.Length);
                changed += CountDifferences(x[..count], y[..count]);
                pos += count;
            }

            result[k] = (float)changed / (o1 - o0);
        }
        return result;
    }

    private const int WindowSize = 1024 * 1024;

    /// <summary>
    /// La carte d'un buffer, entre son original et son état courant (spec §6.3).
    /// Tant qu'une case fait moins de 4 Mio, elle compare les octets, comme la
    /// surcharge sur deux vues. Au-delà, elle déduit chaque case des compteurs
    /// que tient le journal, tranche d'1 Mio par tranche : une vue dézoomée
    /// d'un gros fichier ne relit plus tout le fichier. Une tranche à cheval
    /// sur deux cases leur est partagée au prorata.
    /// </summary>
    /// <param name="buffer">Le buffer.</param>
    /// <param name="buckets">Nombre de colonnes.</param>
    /// <param name="start">Premier octet comparé.</param>
    /// <param name="length">Nombre d'octets comparés ; négatif : jusqu'à la fin.</param>
    public static float[] Compute(FileBuffer buffer, int buckets, long start = 0, long length = -1)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        long n = buffer.Length;
        long span = length < 0 ? n - start : length;
        long clampedStart = Math.Clamp(start, 0, n);
        span = Math.Clamp(span, 0, n - clampedStart);
        if ((double)span / Math.Max(1, buckets) < buffer.Options.DirectDiffBucketBytes)
            return Compute(buffer.Original, buffer.Current, buckets, start, length);

        buckets = Math.Max(1, buckets);
        var result = new float[buckets];
        int shift = buffer.Options.ChunkShift;
        long chunkSize = 1L << shift;
        var changed = buffer.ChangedPerChunk;
        double perBucket = (double)span / buckets;
        for (int k = 0; k < buckets; k++)
        {
            long o0 = clampedStart + (long)(k * perBucket);
            long o1 = k == buckets - 1 ? clampedStart + span : clampedStart + (long)((k + 1) * perBucket);
            if (o1 <= o0) continue;
            double total = 0;
            for (long c = o0 >> shift; c <= (o1 - 1) >> shift; c++)
            {
                long c0 = c << shift, c1 = Math.Min(c0 + chunkSize, n);
                long overlap = Math.Min(o1, c1) - Math.Max(o0, c0);
                total += (double)changed[(int)c] * overlap / (c1 - c0);
            }
            result[k] = (float)(total / (o1 - o0));
        }
        return result;
    }

    /// <summary>Nombre d'octets qui diffèrent entre deux vues sur <c>[start, start + length)</c>, lus par fenêtres d'1 Mio.</summary>
    internal static long CountChanged(ByteView a, ByteView b, long start, long length)
    {
        if (length <= 0) return 0;
        using var x = new ByteWindow(a, WindowSize);
        using var y = new ByteWindow(b, WindowSize);
        long changed = 0;
        for (long pos = start, end = start + length; pos < end;)
        {
            int max = (int)Math.Min(end - pos, int.MaxValue);
            var p = x.From(pos, max);
            var q = y.From(pos, max);
            int count = Math.Min(p.Length, q.Length);
            changed += CountDifferences(p[..count], q[..count]);
            pos += count;
        }
        return changed;
    }

    /// <summary>Nombre de positions où les deux plages, de même longueur, diffèrent.</summary>
    internal static long CountDifferences(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        long count = 0;
        int i = 0;
        if (Vector128.IsHardwareAccelerated && a.Length >= Vector128<byte>.Count)
        {
            ref byte ra = ref MemoryMarshal.GetReference(a);
            ref byte rb = ref MemoryMarshal.GetReference(b);
            int last = a.Length - Vector128<byte>.Count;
            for (; i <= last; i += Vector128<byte>.Count)
            {
                var equal = Vector128.Equals(Vector128.LoadUnsafe(ref ra, (nuint)i), Vector128.LoadUnsafe(ref rb, (nuint)i));
                count += Vector128<byte>.Count - BitOperations.PopCount(equal.ExtractMostSignificantBits());
            }
        }
        for (; i < a.Length; i++)
            if (a[i] != b[i]) count++;
        return count;
    }
}
