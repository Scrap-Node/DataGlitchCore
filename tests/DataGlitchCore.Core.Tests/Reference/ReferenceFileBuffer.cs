namespace DataGlitchCore.Core.Tests.Reference;

// ORACLE — ne pas modifier.
//
// FileBuffer de DataGlitchCore 0.2.0 (24c3d18), recopié pour servir de
// référence aux « mêmes octets » (spec dataglitchcore-buffer, §13.1) : deux
// byte[], l'original et l'état courant. Seul le chargement depuis un flux
// a été retiré : il ne change rien aux octets, et les tests partent de
// FromBytes.

/// <summary>Le buffer de 24c3d18 : l'original et l'état courant, en entier en mémoire.</summary>
internal sealed class ReferenceFileBuffer
{
    private readonly byte[] _original;
    private readonly byte[] _current;

    private ReferenceFileBuffer(string fileName, byte[] data)
    {
        FileName = fileName;
        _original = data;
        _current = (byte[])data.Clone();
    }

    public string FileName { get; }

    public int Length => _current.Length;

    public ReadOnlySpan<byte> Original => _original;

    public ReadOnlySpan<byte> Current => _current;

    public static ReferenceFileBuffer FromBytes(string fileName, byte[] data) => new(fileName, data);

    internal byte[] Read(int offset, int length)
    {
        var slice = new byte[length];
        Array.Copy(_current, offset, slice, 0, length);
        return slice;
    }

    internal void ReadInto(int offset, Span<byte> destination) => _current.AsSpan(offset, destination.Length).CopyTo(destination);

    internal void Write(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(_current.AsSpan(offset, bytes.Length));

    public byte[] SnapshotCurrent() => (byte[])_current.Clone();

    public void ResetToOriginal() => Array.Copy(_original, _current, _current.Length);
}
