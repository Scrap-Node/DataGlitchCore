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
