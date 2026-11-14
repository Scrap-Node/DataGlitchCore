namespace DataGlitchCore.Core.Tests;

/// <summary>Lectures complètes, réservées aux tests : le cœur, lui, ne copie jamais un état en entier.</summary>
internal static class TestBytes
{
    public static byte[] ReadAll(this ByteView view)
    {
        var bytes = new byte[view.Length];
        view.Read(0, bytes);
        return bytes;
    }
}

/// <summary>Des passes écrites à la main, et ce qu'une passe écrit : réservé aux tests.</summary>
internal static class TestPasses
{
    /// <summary>Écrit <paramref name="bytes"/> à <paramref name="offset"/> dans l'état courant, comme une passe.</summary>
    public static PassInfo Write(this OperationJournal journal, FileBuffer buffer, long offset, byte[] bytes, string label = "t")
    {
        var before = new byte[bytes.Length];
        buffer.Current.Read(offset, before);
        using var builder = buffer.BeginPass();
        builder.WriteDiff(offset, before, bytes, mergeGap: 0);
        return journal.Apply(builder.Build(label));
    }

    /// <summary>Les plages d'une passe et leurs nouveaux octets, lus dans son après.</summary>
    public static List<(long Offset, byte[] Bytes)> Writes(this DestructionBatch batch)
    {
        var after = batch.After;
        return batch.Runs().Select(r =>
        {
            var bytes = new byte[r.Length];
            after.Read(r.Start, bytes);
            return (r.Start, bytes);
        }).ToList();
    }
}
