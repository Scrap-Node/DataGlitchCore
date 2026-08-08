using System.Text;

namespace DataGlitchCore.Core.Tests;

public class RiffTests
{
    // ---- constructeur de fixture synthétique ----

    /// <summary>
    /// Construit un chunk RIFF brut : FourCC (4 car.) + taille (uint32 LE) +
    /// contenu, avec le bourrage d'alignement si la taille est impaire.
    /// </summary>
    private static byte[] Chunk(string id, byte[] content)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(id));
        bytes.AddRange(BitConverter.GetBytes((uint)content.Length));
        bytes.AddRange(content);
        if (content.Length % 2 != 0) bytes.Add(0);
        return bytes.ToArray();
    }

    /// <summary>Un LIST est un chunk dont le contenu commence par un FourCC
    /// de sous-type.</summary>
    private static byte[] List(string listType, params byte[][] children)
    {
        var content = new List<byte>();
        content.AddRange(Encoding.ASCII.GetBytes(listType));
        foreach (var c in children) content.AddRange(c);
        return Chunk("LIST", content.ToArray());
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ---- tests ----

    [Fact]
    public void Walk_Reads_A_Flat_Chunk_Like_Wav_Data()
    {
        // RIFF/WAVE : fmt  + data, tous deux directement au niveau racine,
        // sans LIST — la structure du WAV déjà géré par MediaProbe.
        var fmt = Chunk("fmt ", new byte[] { 1, 2, 3, 4 });
        var data = Chunk("data", new byte[] { 9, 9, 9 }); // taille impaire → 1 octet de bourrage
        // Un "RIFF" est un chunk comme un autre, juste avec un FourCC
        // réservé : on ne réutilise pas List() (qui produit "LIST") pour la
        // racine, on rejoue sa construction à la main.
        var content = Concat(Encoding.ASCII.GetBytes("WAVE"), fmt, data);
        var bytes = Chunk("RIFF", content);

        var chunks = Riff.Walk(bytes, 0, bytes.Length);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(("RIFF", "WAVE"), (chunks[0].Id, chunks[0].ListType));
        Assert.Equal(("fmt ", (string?)null), (chunks[1].Id, chunks[1].ListType));
        Assert.Equal(4, chunks[1].DataSize);
        Assert.Equal(("data", (string?)null), (chunks[2].Id, chunks[2].ListType));
        Assert.Equal(3, chunks[2].DataSize);
    }

    [Fact]
    public void Walk_Descends_Into_Nested_Lists_Depth_First()
    {
        // hdrl contenant un strl contenant strh : une descente à plat (sans
        // récursion dans LIST) sauterait complètement strh, exactement le
        // bug reproché au walker existant de MediaProbe pour l'AVI.
        var strh = Chunk("strh", new byte[] { 7, 7 });
        var strl = List("strl", strh);
        var hdrl = List("hdrl", strl);
        var content = Concat(Encoding.ASCII.GetBytes("AVI "), hdrl);
        var bytes = Chunk("RIFF", content);

        var chunks = Riff.Walk(bytes, 0, bytes.Length);

        Assert.Equal(new[] { "RIFF", "LIST", "LIST", "strh" }, chunks.Select(c => c.Id));
        Assert.Equal(new[] { "AVI ", "hdrl", "strl", null }, chunks.Select(c => c.ListType));
    }

    [Fact]
    public void Walk_Handles_Several_Chunks_At_The_Same_Level_After_A_List()
    {
        // Reproduit la forme d'un AVI réel : hdrl (LIST), puis movi (LIST),
        // puis idx1, tous frères au niveau racine.
        var hdrl = List("hdrl", Chunk("avih", new byte[] { 1 }));
        var movi = List("movi", Chunk("00dc", new byte[] { 2, 2 }));
        var idx1 = Chunk("idx1", new byte[] { 3, 3, 3, 3 });
        var content = Concat(Encoding.ASCII.GetBytes("AVI "), hdrl, movi, idx1);
        var bytes = Chunk("RIFF", content);

        var chunks = Riff.Walk(bytes, 0, bytes.Length);

        Assert.Equal(new[] { "RIFF", "LIST", "avih", "LIST", "00dc", "idx1" }, chunks.Select(c => c.Id));
        var idx1Chunk = chunks.Single(c => c.Id == "idx1");
        Assert.Equal(4, idx1Chunk.DataSize);
    }

    [Fact]
    public void Walk_Applies_Two_Byte_Alignment_Padding_Between_Siblings()
    {
        var odd = Chunk("odd ", new byte[] { 1, 2, 3 });   // 3 octets + 1 de bourrage
        var next = Chunk("next", new byte[] { 9 });
        var bytes = Concat(odd, next);

        var chunks = Riff.Walk(bytes, 0, bytes.Length);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("next", chunks[1].Id);
        Assert.Equal(odd.Length, chunks[1].Start); // le padding a bien été sauté
    }

    [Fact]
    public void Walk_Throws_When_A_Chunk_Overflows_Its_Region()
    {
        var bytes = Concat(Encoding.ASCII.GetBytes("data"), BitConverter.GetBytes((uint)1000));
        // taille déclarée (1000) très supérieure aux octets réellement présents

        Assert.Throws<RiffFormatException>(() => Riff.Walk(bytes, 0, bytes.Length));
    }

    [Fact]
    public void Walk_Throws_On_A_List_Too_Short_For_Its_Subtype_FourCc()
    {
        // "LIST" avec une taille déclarée de 2 : pas la place pour les 4
        // octets de FourCC de sous-type qu'un LIST doit toujours porter.
        var bytes = Concat(Encoding.ASCII.GetBytes("LIST"), BitConverter.GetBytes((uint)2), new byte[] { 0, 0 });

        Assert.Throws<RiffFormatException>(() => Riff.Walk(bytes, 0, bytes.Length));
    }

    [Fact]
    public void Walk_Tolerates_A_Missing_Trailing_Padding_Byte_At_True_Eof()
    {
        // Une taille impaire sans octet de bourrage après le tout dernier
        // chunk d'une région n'est pas une vraie corruption : il n'y a
        // simplement rien après à sauter. Rejeter ce cas romprait sur des
        // fichiers par ailleurs valides mais légèrement non conformes.
        var content = new byte[] { 1, 2, 3 }; // taille impaire, aucun octet de trop dans le buffer
        var bytes = Concat(Encoding.ASCII.GetBytes("last"), BitConverter.GetBytes((uint)content.Length), content);

        var chunks = Riff.Walk(bytes, 0, bytes.Length);

        var chunk = Assert.Single(chunks);
        Assert.Equal("last", chunk.Id);
        Assert.Equal(3, chunk.DataSize);
    }

    [Fact]
    public void Walk_Empty_Region_Yields_No_Chunks()
    {
        var chunks = Riff.Walk(ReadOnlySpan<byte>.Empty, 0, 0);
        Assert.Empty(chunks);
    }
}
