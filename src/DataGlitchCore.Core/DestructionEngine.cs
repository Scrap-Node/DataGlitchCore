namespace DataGlitchCore.Core;

/// <summary>
/// Construit une passe de destruction (<see cref="DestructionBatch"/>) à partir
/// d'un <see cref="FileBuffer"/> et de <see cref="DestructionOptions"/>. Ne mute
/// pas le buffer : il lit l'état courant (état pré-passe cohérent) et renvoie les
/// écritures à appliquer via le <see cref="OperationJournal"/>. Avec une seed
/// fixée, la même config produit exactement le même rendu.
///
/// Entièrement agnostique au format du fichier — aucune notion de PCM, de trame
/// vidéo ou de tout autre domaine. Un projet qui connaît son format (WAV, AVI…)
/// construit le <see cref="HeaderInfo"/> qu'il lui passe, et peut affiner le
/// résultat après coup (par ex. confiner la dégradation à une bande de fréquences)
/// en repartant des <see cref="DestructionBatch.Edits"/> renvoyés — voir
/// <see cref="ToRuns"/>, conçu justement pour ce genre de post-traitement.
/// </summary>
public static class DestructionEngine
{
    public static DestructionBatch BuildBatch(FileBuffer buffer, DestructionOptions options, HeaderInfo? header = null)
    {
        int length = buffer.Length;
        int blockSize = Math.Max(1, options.BlockSize);

        // zone ciblée, en octets
        int start = (int)Math.Clamp(options.RegionStart * length, 0, length);
        int end = (int)Math.Clamp(options.RegionEnd * length, 0, length);
        if (options.ProtectHeader && header is not null)
            start = Math.Max(start, header.PayloadStart);
        if (end < start) (start, end) = (end, start);

        var rng = options.Seed is int s ? new Random(s) : new Random();
        int maxShift = Math.Max(1, options.MaxShiftBlocks);
        List<ByteEdit> edits = new();

        for (int off = start; off < end; off += blockSize)
        {
            int len = Math.Min(blockSize, end - off);
            if (len <= 0) break;
            if (rng.NextDouble() >= options.Density) continue;

            var oldBytes = buffer.Read(off, len);
            var newBytes = Apply(buffer, options, rng, off, len, start, maxShift);
            edits.Add(new ByteEdit(off, oldBytes, newBytes));
        }

        return new DestructionBatch(DescribeLabel(options), edits);
    }

    /// <summary>
    /// Découpe un résultat en plages réellement modifiées, en comparant deux
    /// tampons octet à octet. Sert à construire un <see cref="DestructionBatch"/>
    /// à la main quand on post-traite le résultat de <see cref="BuildBatch"/>
    /// (ex. filtrage fréquentiel côté audio) : sans ça, l'historique stockerait
    /// toute la zone même là où le post-traitement n'a rien laissé passer.
    /// </summary>
    /// <param name="baseOffset">Offset, dans le fichier, du premier octet de <paramref name="before"/>/<paramref name="after"/>.</param>
    public static List<ByteEdit> ToRuns(int baseOffset, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, int mergeGap = 64)
    {
        var runs = new List<ByteEdit>();
        int i = 0, n = before.Length;

        while (i < n)
        {
            while (i < n && before[i] == after[i]) i++;
            if (i >= n) break;

            int start = i, lastDiff = i, same = 0;
            while (i < n && same <= mergeGap)
            {
                if (before[i] == after[i]) same++;
                else { same = 0; lastDiff = i; }
                i++;
            }

            int len = lastDiff - start + 1;
            var oldSlice = before.Slice(start, len).ToArray();
            var newSlice = after.Slice(start, len).ToArray();
            runs.Add(new ByteEdit(baseOffset + start, oldSlice, newSlice));
        }

        return runs;
    }

    private static byte[] Apply(FileBuffer buffer, DestructionOptions o, Random rng, int off, int len, int rangeStart, int maxShift)
    {
        var dst = new byte[len];
        switch (o.Operator)
        {
            case DestructionOperator.Zero:
                // dst déjà à zéro
                break;

            case DestructionOperator.Fixed:
                Array.Fill(dst, o.FixedValue);
                break;

            case DestructionOperator.Xor:
            {
                buffer.ReadInto(off, dst);
                byte mask = o.XorMask;
                for (int i = 0; i < len; i++) dst[i] ^= mask;
                break;
            }

            case DestructionOperator.BitShift:
            {
                buffer.ReadInto(off, dst);
                int k = Math.Clamp(o.BitRotate, 1, 7);
                for (int i = 0; i < len; i++)
                {
                    byte b = dst[i];
                    dst[i] = (byte)((b << k) | (b >> (8 - k)));
                }
                break;
            }

            case DestructionOperator.Reverse:
            {
                var src = buffer.Read(off, len);
                for (int i = 0; i < len; i++) dst[i] = src[len - 1 - i];
                break;
            }

            case DestructionOperator.ShiftBlock:
            {
                int shift = rng.Next(1, maxShift + 1) * len;
                int srcOff = Math.Max(rangeStart, off - shift);
                buffer.ReadInto(srcOff, dst); // recopie des octets antérieurs (écho/traînée)
                break;
            }

            case DestructionOperator.DuplicateBlock:
            {
                var src = buffer.Read(off, len);
                int motif = Math.Min(4, len);
                for (int i = 0; i < len; i++) dst[i] = src[i % motif];
                break;
            }
        }
        return dst;
    }

    private static string DescribeLabel(DestructionOptions o)
    {
        string seed = o.Seed is int s ? s.ToString() : "aléatoire";
        return $"{o.Operator} · densité {o.Density:P0} · bloc {o.BlockSize}o · seed {seed}";
    }
}
