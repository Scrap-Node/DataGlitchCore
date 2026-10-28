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
/// construit le <see cref="DestructionMask"/> qu'il lui passe, et peut affiner le
/// résultat après coup (par ex. confiner la dégradation à une bande de fréquences)
/// en repartant des <see cref="DestructionBatch.Edits"/> renvoyés — voir
/// <see cref="ToRuns"/>, conçu justement pour ce genre de post-traitement.
/// </summary>
public static class DestructionEngine
{
    /// <summary>
    /// Surcharge historique : un plancher unique (<see cref="HeaderInfo.PayloadStart"/>),
    /// adaptée en un <see cref="DestructionMask"/> équivalent. Conservée telle
    /// quelle pour ne rien casser côté appelants existants (le WAV n'a qu'un
    /// seul en-tête à protéger) — l'AVI, qui a besoin de plusieurs régions
    /// entrelacées, passe directement par la surcharge à
    /// <see cref="DestructionMask"/>.
    /// </summary>
    public static DestructionBatch BuildBatch(FileBuffer buffer, DestructionOptions options, HeaderInfo? header = null)
    {
        var mask = options.ProtectHeader && header is not null
            ? DestructionMask.FromPayloadStart(header.PayloadStart)
            : DestructionMask.WholeBuffer;
        return BuildBatch(buffer, options, mask);
    }

    /// <summary>
    /// Construit une passe dans les limites d'un <see cref="DestructionMask"/> :
    /// la zone ciblée par <see cref="DestructionOptions.RegionStart"/>/<see cref="DestructionOptions.RegionEnd"/>
    /// est intersectée avec les régions du masque, ce qui donne une liste de
    /// fenêtres disjointes — la boucle de blocs tourne à l'intérieur de
    /// chacune, jamais en dehors. Avec un masque à une seule région non
    /// bornée (<see cref="DestructionMask.WholeBuffer"/>,
    /// <see cref="DestructionMask.FromPayloadStart"/>), le résultat est
    /// identique bit à bit à l'ancien modèle à un seul plancher — ce n'est
    /// pas une coïncidence, c'est la même formule qui dégénère en la même
    /// plage unique.
    /// </summary>
    public static DestructionBatch BuildBatch(FileBuffer buffer, DestructionOptions options, DestructionMask mask)
    {
        int length = buffer.Length;
        int blockSize = Math.Max(MinBlockSize(options.Operator), options.BlockSize);

        // zone ciblée, en octets — le masque, pas ce plancher, protège
        int start = (int)Math.Clamp(options.RegionStart * length, 0, length);
        int end = (int)Math.Clamp(options.RegionEnd * length, 0, length);
        if (end < start) (start, end) = (end, start);

        var windows = IntersectWindows(start, end, mask, length);
        if (windows.Count == 0) return new DestructionBatch(DescribeLabel(options), []);

        // Espace-payload : les fenêtres mises bout à bout comme un buffer
        // virtuel contigu, pour que ShiftBlock (l'écho) ne puisse jamais lire
        // un octet protégé situé dans un trou entre deux fenêtres — voir
        // PayloadSpace plus bas.
        var cumulative = new int[windows.Count + 1];
        for (int i = 0; i < windows.Count; i++) cumulative[i + 1] = cumulative[i] + windows[i].Length;
        var space = new PayloadSpace(windows, cumulative);

        var rng = options.Seed is int s ? new Random(s) : new Random();
        int maxShift = Math.Max(1, options.MaxShiftBlocks);
        List<ByteEdit> edits = new();

        for (int w = 0; w < windows.Count; w++)
        {
            var window = windows[w];
            int windowVirtualStart = cumulative[w];

            for (int off = window.Start; off < window.End; off += blockSize)
            {
                int len = Math.Min(blockSize, window.End - off);
                if (rng.NextDouble() >= options.Density) continue;

                int virtualOff = windowVirtualStart + (off - window.Start);
                var oldBytes = buffer.Read(off, len);
                var newBytes = Apply(buffer, options, rng, off, len, virtualOff, space, maxShift);

                // Fenêtres par frame ⇒ beaucoup de blocs courts où l'opérateur
                // ne change en fait rien (Reverse sur 1 octet, écho qui retombe
                // sur lui-même…) : sans ce garde-fou, un ByteEdit sans le
                // moindre changement gonflerait BytesChanged, allumerait
                // DiffMap à tort et polluerait l'undo pour rien.
                if (newBytes.AsSpan().SequenceEqual(oldBytes)) continue;

                edits.Add(new ByteEdit(off, oldBytes, newBytes));
            }
        }

        return new DestructionBatch(DescribeLabel(options), edits);
    }

    /// <summary>
    /// Intersecte la zone ciblée <c>[start, end)</c> (déjà normalisée par
    /// l'appelant — <see cref="BuildBatch(FileBuffer,DestructionOptions,DestructionMask)"/>
    /// y échange les bornes si <c>RegionStart &gt; RegionEnd</c>) avec les
    /// régions du masque, ramenées à <c>[0, length)</c>. Le résultat est une
    /// liste de fenêtres disjointes, potentiellement vide si la zone ciblée
    /// et les régions protégées ne se recouvrent pas du tout — une intersection
    /// vide est la bonne réponse dans ce cas, ce qui écarte le bug de l'ancien
    /// modèle : là où un plancher appliqué APRÈS le calcul de la zone pouvait,
    /// combiné à l'ancien échange de bornes, faire tomber la destruction À
    /// L'INTÉRIEUR d'un en-tête prétendument protégé.
    /// </summary>
    private static List<ByteRange> IntersectWindows(int start, int end, DestructionMask mask, int length)
    {
        var windows = new List<ByteRange>();
        foreach (var region in mask.ClampedTo(length))
        {
            int lo = Math.Max(start, region.Start);
            int hi = Math.Min(end, region.End);
            if (hi > lo) windows.Add(new ByteRange(lo, hi));
        }
        return windows;
    }

    /// <summary>
    /// Découpe un résultat en plages réellement modifiées, en comparant deux
    /// tampons octet à octet. Sert à construire un <see cref="DestructionBatch"/>
    /// à la main quand on post-traite le résultat de <see cref="BuildBatch(FileBuffer,DestructionOptions,DestructionMask)"/>
    /// (ex. filtrage fréquentiel côté audio) : sans ça, l'historique stockerait
    /// toute la zone même là où le post-traitement n'a rien laissé passer.
    /// </summary>
    /// <param name="baseOffset">Offset, dans le fichier, du premier octet de <paramref name="before"/>/<paramref name="after"/>.</param>
    /// <param name="before">Octets avant le traitement.</param>
    /// <param name="after">Octets après le traitement, de même longueur que <paramref name="before"/>.</param>
    /// <param name="mergeGap">Écart toléré : deux plages modifiées séparées par au plus ce nombre
    /// d'octets inchangés forment une seule écriture, plutôt qu'une poussière de petites.</param>
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

    /// <summary>Longueur du motif que <see cref="DestructionOperator.DuplicateBlock"/>
    /// recopie sur tout le bloc.</summary>
    private const int DuplicateMotifLength = 4;

    /// <summary>
    /// Plus petit bloc sur lequel l'opérateur peut changer quoi que ce soit :
    /// inverser 1 octet, ou répéter un motif de 4 octets sur 4 octets ou moins,
    /// rend le bloc à l'identique. <see cref="BuildBatch(FileBuffer,DestructionOptions,DestructionMask)"/>
    /// relève <see cref="DestructionOptions.BlockSize"/> à ce minimum ; exposé
    /// pour que l'UI puisse l'afficher plutôt que de laisser croire qu'un bloc
    /// plus petit est appliqué.
    /// </summary>
    public static int MinBlockSize(DestructionOperator op) => op switch
    {
        DestructionOperator.Reverse => 2,
        DestructionOperator.DuplicateBlock => DuplicateMotifLength + 1,
        _ => 1,
    };

    private static byte[] Apply(FileBuffer buffer, DestructionOptions o, Random rng, int off, int len, int virtualOff, PayloadSpace space, int maxShift)
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
                // L'écho travaille en espace-payload (voir PayloadSpace), pas
                // en offsets fichier bruts : sinon "off - shift" pourrait
                // tomber dans un trou protégé entre deux fenêtres et recopier
                // un en-tête de chunk dans l'image. Avec une seule fenêtre,
                // ceci dégénère exactement en Math.Max(rangeStart, off - shift)
                // — le comportement historique.
                int shift = rng.Next(1, maxShift + 1) * len;
                int virtualSrc = Math.Max(0, virtualOff - shift);
                space.ReadInto(buffer, virtualSrc, dst);
                break;
            }

            case DestructionOperator.DuplicateBlock:
            {
                var src = buffer.Read(off, len);
                int motif = Math.Min(DuplicateMotifLength, len);
                for (int i = 0; i < len; i++) dst[i] = src[i % motif];
                break;
            }
        }
        return dst;
    }

    private static string DescribeLabel(DestructionOptions o)
    {
        string seed = o.Seed is int s ? s.ToString() : "aléatoire";
        int block = Math.Max(MinBlockSize(o.Operator), o.BlockSize);
        return $"{o.Operator} · densité {o.Density:P0} · bloc {block}o · seed {seed}";
    }

    /// <summary>
    /// Vue "toutes les fenêtres mises bout à bout" d'un buffer troué par des
    /// régions protégées — l'espace dans lequel <see cref="DestructionOperator.ShiftBlock"/>
    /// calcule sa source d'écho, pour ne jamais pouvoir lire un octet hors
    /// des fenêtres destructibles. <paramref name="cumulative"/>[i] est la
    /// somme des longueurs des fenêtres avant l'indice i (donc le début, en
    /// espace virtuel, de la fenêtre i) ; <c>cumulative.Length == windows.Count + 1</c>.
    /// </summary>
    private readonly struct PayloadSpace(IReadOnlyList<ByteRange> windows, int[] cumulative)
    {
        public void ReadInto(FileBuffer buffer, int virtualStart, Span<byte> dst)
        {
            int remaining = dst.Length;
            int v = virtualStart;
            int written = 0;
            while (remaining > 0)
            {
                int idx = LocateWindowIndex(v);
                int withinWindow = v - cumulative[idx];
                int available = windows[idx].Length - withinWindow;
                int take = Math.Min(remaining, available);

                buffer.ReadInto(windows[idx].Start + withinWindow, dst.Slice(written, take));

                written += take;
                v += take;
                remaining -= take;
            }
        }

        /// <summary>Plus grand indice <c>i</c> tel que <c>cumulative[i] &lt;= virtualOffset</c>
        /// — la fenêtre qui contient cette position virtuelle.</summary>
        private int LocateWindowIndex(int virtualOffset)
        {
            int lo = 0, hi = windows.Count - 1, idx = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cumulative[mid] <= virtualOffset) { idx = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return idx;
        }
    }
}
