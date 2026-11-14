using System.Buffers;
using System.Buffers.Binary;

namespace DataGlitchCore.Core;

/// <summary>
/// La base d'un journal (spec §5.2 et §7) : les passes fusionnées, qui ne
/// s'annulent plus une à une. Éparse, c'est un segment comme les autres,
/// au fond de la pile, par-dessus l'original. Dense, c'est une copie complète
/// de l'état, sans en-têtes de plage, qui remplace l'original au fond ; ses
/// compteurs vont dans un fichier à part. L'original reste la référence de
/// <see cref="FileBuffer.Original"/> et des différences.
/// </summary>
internal sealed class BaseLayer
{
    private BaseLayer(long id, string? appData, Segment? sparse, IBlob? dense, IBlob? diffFile, int[]? denseDiffs)
    {
        Id = id;
        AppData = appData;
        Sparse = sparse;
        Dense = dense;
        DiffFile = diffFile;
        DenseDiffs = denseDiffs;
    }

    /// <summary>Le numéro de la dernière passe fusionnée : la base s'appelle <c>base-&lt;id&gt;</c>.</summary>
    public long Id { get; }

    /// <summary>La fusion, par la fonction de l'app, des <c>AppData</c> des passes fusionnées.</summary>
    public string? AppData { get; }

    /// <summary>La base éparse : un segment, au fond de la pile.</summary>
    public Segment? Sparse { get; }

    /// <summary>La base dense : l'état complet, au fond à la place de l'original.</summary>
    public IBlob? Dense { get; }

    /// <summary>Les compteurs de la base dense, <c>base-&lt;id&gt;.diff</c>.</summary>
    public IBlob? DiffFile { get; }

    /// <summary>Les compteurs de la base dense : un par tranche du fichier.</summary>
    public int[]? DenseDiffs { get; }

    public bool IsDense => Dense is not null;

    /// <summary>La place de la base sur le disque.</summary>
    public long DiskBytes => Sparse is not null ? Sparse.Blob.Length : Dense!.Length + DiffFile!.Length;

    public IEnumerable<IBlob> Files => Sparse is not null ? [Sparse.Blob] : [Dense!, DiffFile!];

    /// <summary>La place d'une base dense pour ce buffer : le fichier, plus ses compteurs.</summary>
    public static long DenseBytes(FileBuffer buffer) => buffer.Length + DiffFileFormat.Size(ChunkCount(buffer));

    private static long ChunkCount(FileBuffer buffer) =>
        buffer.Length == 0 ? 0 : ((buffer.Length - 1) >> buffer.Options.ChunkShift) + 1;

    /// <summary>
    /// Écrit la base qui remplace <paramref name="previous"/> une fois
    /// <paramref name="passes"/> fusionnées, de la plus ancienne à la plus
    /// récente (§7, étape 1) : dans <c>tmp/</c>, forcée sur le disque, puis
    /// rangée dans <c>segments/</c>. L'état courant n'en change pas ; c'est au
    /// journal de basculer, puis de supprimer ce qui a été fusionné. En cas
    /// d'échec, les fichiers déjà écrits sont supprimés et rien n'a changé.
    ///
    /// La base devient dense dès qu'elle couvre la moitié du fichier, ou
    /// qu'éparse, elle prendrait plus de place que dense.
    /// </summary>
    public static BaseLayer Merge(FileBuffer buffer, BaseLayer? previous, IReadOnlyList<Segment> passes, long id, string? appData)
    {
        var store = buffer.Store;
        var written = new List<IBlob>();
        try
        {
            if (previous is { IsDense: true })
            {
                // déjà dense : un clone de l'ancienne base, et les passes recopiées par-dessus
                var diffs = (int[])previous.DenseDiffs!.Clone();
                foreach (var pass in passes) AddDiffs(diffs, pass);
                var dense = WriteDense(buffer, previous.Dense!, passes, id, written);
                var diffFile = WriteDiffFile(buffer, diffs, id, written);
                return new BaseLayer(id, appData, null, dense, diffFile, diffs);
            }

            IReadOnlyList<Segment> layers = previous?.Sparse is { } old ? [old, .. passes] : passes;
            var tmp = store.CreateTemp($"base-{id:D8}.seg");
            written.Add(tmp);
            var sparse = WriteSparse(buffer, layers, tmp);
            if (sparse.DataBytes * 2 < buffer.Length && tmp.Length <= DenseBytes(buffer))
            {
                sparse.WriteId(id);
                sparse.MovedTo(Publish(store, tmp, $"base-{id:D8}.seg", written));
                return new BaseLayer(id, appData, sparse, null, null, null);
            }

            // la moitié du fichier est couverte : l'original cloné, la base éparse recopiée par-dessus
            var counters = new int[ChunkCount(buffer)];
            AddDiffs(counters, sparse);
            var full = WriteDense(buffer, store.Source, [sparse], id, written);
            var counterFile = WriteDiffFile(buffer, counters, id, written);
            store.Delete(tmp);
            written.Remove(tmp);
            return new BaseLayer(id, appData, null, full, counterFile, counters);
        }
        catch
        {
            foreach (var blob in written) store.Delete(blob);
            throw;
        }
    }

    private static void AddDiffs(int[] diffs, Segment segment)
    {
        for (int i = 0; i < segment.Diffs.Length; i++) diffs[segment.FirstChunk + i] += segment.Diffs[i];
    }

    private static IBlob Publish(IBlobStore store, IBlob tmp, string name, List<IBlob> written)
    {
        var published = store.Publish(tmp, name);
        written.Remove(tmp);
        written.Add(published);
        return published;
    }

    /// <summary>
    /// Les couches fusionnées en un segment, tranche par tranche : l'original
    /// de la tranche, puis chaque couche par-dessus, en marquant les octets
    /// couverts. Les octets couverts font les plages ; deux plages à moins de
    /// 12 octets l'une de l'autre n'en font qu'une, l'écart gardant les octets
    /// de l'original, ce qui borne la base éparse à la taille du fichier.
    /// Les compteurs s'additionnent.
    /// </summary>
    private static Segment WriteSparse(FileBuffer buffer, IReadOnlyList<Segment> layers, IBlob blob)
    {
        int shift = buffer.Options.ChunkShift;
        var writer = new SegmentWriter(blob, shift, buffer.Options.PassBufferBytes);
        var (first, last) = Span(layers);
        int size = (int)Math.Min(1L << shift, Math.Max(1, buffer.Length));
        var window = ArrayPool<byte>.Shared.Rent(size);
        var covered = ArrayPool<bool>.Shared.Rent(size);
        try
        {
            Span<byte> gap = stackalloc byte[SegmentFormat.RunHeaderSize];
            int gapLength = 0;
            bool inRun = false;
            long previousEnd = -1;
            for (long c = first; c <= last; c++)
            {
                if (!Touched(layers, c)) continue;
                long at = c << shift;
                int n = (int)Math.Min(1L << shift, buffer.Length - at);
                var bytes = window.AsSpan(0, n);
                var marks = covered.AsSpan(0, n);

                // une tranche sautée est un écart d'au moins une tranche : la plage en cours s'arrête
                if (inRun && at != previousEnd)
                {
                    writer.EndRun();
                    inRun = false;
                }
                buffer.Store.Source.Read(at, bytes);
                marks.Clear();
                foreach (var layer in layers) layer.Overlay(at, bytes, marks);

                for (int i = 0; i < n;)
                {
                    if (marks[i])
                    {
                        int j = marks[i..].IndexOf(false);
                        int end = j < 0 ? n : i + j;
                        if (!inRun)
                        {
                            writer.StartRun(at + i);
                            inRun = true;
                        }
                        else if (gapLength > 0)
                        {
                            writer.Append(gap[..gapLength]);
                        }
                        gapLength = 0;
                        writer.Append(bytes[i..end]);
                        i = end;
                    }
                    else
                    {
                        int j = marks[i..].IndexOf(true);
                        int end = j < 0 ? n : i + j;
                        if (inRun)
                        {
                            if (gapLength + (end - i) < SegmentFormat.RunHeaderSize)
                            {
                                bytes[i..end].CopyTo(gap[gapLength..]);
                                gapLength += end - i;
                            }
                            else
                            {
                                writer.EndRun();
                                inRun = false;
                                gapLength = 0;
                            }
                        }
                        i = end;
                    }
                }
                previousEnd = at + n;

                long delta = 0;
                foreach (var layer in layers) delta += layer.DiffAt(c);
                if (delta != 0) writer.AddDelta(c, checked((int)delta));
            }
            writer.Finish();
            return Segment.Open(blob);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
            ArrayPool<bool>.Shared.Return(covered);
        }
    }

    /// <summary>
    /// Une base dense : <paramref name="bottom"/> cloné dans <c>tmp/</c>, puis
    /// chaque tranche que touchent les segments relue, recouverte et réécrite.
    /// Les autres tranches restent celles du clone, sans être copiées sous APFS.
    /// </summary>
    private static IBlob WriteDense(FileBuffer buffer, IBlob bottom, IReadOnlyList<Segment> segments, long id, List<IBlob> written)
    {
        var store = buffer.Store;
        var blob = store.CreateTempCopy(bottom, $"base-{id:D8}.dense");
        written.Add(blob);
        int shift = buffer.Options.ChunkShift;
        var (first, last) = Span(segments);
        var window = ArrayPool<byte>.Shared.Rent((int)Math.Min(1L << shift, Math.Max(1, buffer.Length)));
        try
        {
            for (long c = first; c <= last; c++)
            {
                if (!Touched(segments, c)) continue;
                long at = c << shift;
                var bytes = window.AsSpan(0, (int)Math.Min(1L << shift, buffer.Length - at));
                blob.Read(at, bytes);
                foreach (var segment in segments) segment.CopyInto(at, bytes);
                blob.Write(at, bytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
        return Publish(store, blob, $"base-{id:D8}.seg", written);
    }

    private static IBlob WriteDiffFile(FileBuffer buffer, int[] diffs, long id, List<IBlob> written)
    {
        var blob = buffer.Store.CreateTemp($"base-{id:D8}.diff");
        written.Add(blob);
        DiffFileFormat.Write(blob, buffer.Options.ChunkShift, diffs);
        return Publish(buffer.Store, blob, $"base-{id:D8}.diff", written);
    }

    /// <summary>Les tranches que touchent les segments, de la première à la dernière ; vide (1, 0) s'ils n'écrivent rien.</summary>
    private static (long First, long Last) Span(IReadOnlyList<Segment> segments)
    {
        long first = long.MaxValue, last = -1;
        foreach (var segment in segments)
        {
            if (segment.LastChunk < segment.FirstChunk) continue;
            first = Math.Min(first, segment.FirstChunk);
            last = Math.Max(last, segment.LastChunk);
        }
        return last < 0 ? (1, 0) : (first, last);
    }

    private static bool Touched(IReadOnlyList<Segment> segments, long chunk)
    {
        foreach (var segment in segments) if (segment.Touches(chunk)) return true;
        return false;
    }
}

/// <summary>
/// Les compteurs d'une base dense, <c>base-&lt;id&gt;.diff</c> (spec §5.2) :
///
///   en-tête  16 o  "DGCD" · version u16 · tranche (log2) u8 · réservé · tranches i64
///   compteurs      un i32 par tranche du fichier
///   pied     12 o  longueur totale i64 · "DGCD"
/// </summary>
internal static class DiffFileFormat
{
    public const int HeaderSize = 16;
    public const ushort Version = 1;
    public static ReadOnlySpan<byte> Magic => "DGCD"u8;

    public static long Size(long chunks) => HeaderSize + 4 * chunks + SegmentFormat.FooterSize;

    public static void Write(IBlob blob, int shift, int[] diffs)
    {
        var bytes = new byte[Size(diffs.Length)];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), Version);
        bytes[6] = (byte)shift;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), diffs.Length);
        for (int c = 0; c < diffs.Length; c++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HeaderSize + 4 * c), diffs[c]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(bytes.Length - 12), bytes.Length);
        Magic.CopyTo(bytes.AsSpan(bytes.Length - 4));
        blob.Write(0, bytes);
    }

    /// <summary>Relit les compteurs ; un fichier tronqué ou d'une autre tranche est refusé.</summary>
    public static int[] Read(IBlob blob, int shift)
    {
        long length = blob.Length;
        if (length < HeaderSize + SegmentFormat.FooterSize)
            throw new InvalidDataException($"{blob.Name} : trop court pour des compteurs ({length} octets).");
        var bytes = new byte[length];
        blob.Read(0, bytes);
        if (!bytes.AsSpan(0, 4).SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != Version)
            throw new InvalidDataException($"{blob.Name} : en-tête de compteurs inconnu.");
        if (!bytes.AsSpan((int)length - 4).SequenceEqual(Magic) || BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan((int)length - 12)) != length)
            throw new InvalidDataException($"{blob.Name} : pied absent ou longueur fausse, fichier tronqué.");
        if (bytes[6] != shift)
            throw new InvalidDataException($"{blob.Name} : compteurs par tranches de 2^{bytes[6]} octets, 2^{shift} attendus.");
        long chunks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8));
        if (Size(chunks) != length) throw new InvalidDataException($"{blob.Name} : nombre de tranches faux.");
        var diffs = new int[chunks];
        for (int c = 0; c < chunks; c++) diffs[c] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(HeaderSize + 4 * c));
        return diffs;
    }
}
