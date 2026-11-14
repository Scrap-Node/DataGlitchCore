using System.Security.Cryptography;

namespace DataGlitchCore.Core.Tests;

/// <summary>La copie de l'original dans la session (spec §4.2, §10.1).</summary>
public sealed class SourceFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dgc-source-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Un flux d'upload : ne dit pas sa taille, ne se positionne pas, rend des morceaux de taille quelconque.</summary>
    private sealed class UploadStream(byte[] data, int chunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, chunk), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset));
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Data(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 31)).ToArray();

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1000, 1000)]
    [InlineData(1000, 5000)]
    [InlineData(3 * 1024 * 1024 + 7, 4 * 1024 * 1024)]
    public async Task Copy_Keeps_Everything_Under_The_Cap(int size, long cap)
    {
        var data = Data(size);
        string path = Path.Combine(_dir, "source.bin");

        var copy = await SourceFile.CopyAsync(new UploadStream(data, 70_000), path, cap);

        Assert.Equal(new SourceCopy(size, false, Convert.ToHexStringLower(SHA256.HashData(data))), copy);
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(1001, 1000)]
    [InlineData(5 * 1024 * 1024, 1024 * 1024 + 3)]
    [InlineData(10, 0)]
    public async Task Copy_Cuts_At_The_Cap_And_Says_So(int size, long cap)
    {
        var data = Data(size);
        string path = Path.Combine(_dir, "source.bin");

        var copy = await SourceFile.CopyAsync(new UploadStream(data, 70_000), path, cap);

        var kept = data[..(int)cap];
        Assert.Equal(new SourceCopy(cap, true, Convert.ToHexStringLower(SHA256.HashData(kept))), copy);
        Assert.Equal(kept, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_Cancelled_Copy_Leaves_No_File()
    {
        string path = Path.Combine(_dir, "source.bin");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SourceFile.CopyAsync(new UploadStream(Data(1000), 10), path, 10_000, new CancellationToken(canceled: true)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task The_Copy_Opens_As_A_Buffer()
    {
        var data = Data(100_000);
        string path = Path.Combine(_dir, "source.bin");
        await SourceFile.CopyAsync(new MemoryStream(data), path, long.MaxValue);

        using var buffer = FileBuffer.Open(path, _dir);

        Assert.Equal("source.bin", buffer.FileName);
        Assert.Equal(data.Length, buffer.Length);
        Assert.Equal(data, buffer.Current.ReadAll());
    }
}
