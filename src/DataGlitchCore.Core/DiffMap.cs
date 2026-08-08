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
}
