using System.Buffers;
using System.Security.Cryptography;

namespace DataGlitchCore.Core;

/// <summary>Ce que <see cref="SourceFile.CopyAsync"/> a copié.</summary>
/// <param name="Length">Octets copiés.</param>
/// <param name="Truncated">Le flux en avait plus que <c>maxBytes</c> : la copie est coupée.</param>
/// <param name="Sha256">Empreinte SHA-256 de ce qui a été copié, en hexadécimal minuscule.</param>
public sealed record SourceCopy(long Length, bool Truncated, string Sha256);

/// <summary>
/// La copie de l'original dans le dossier de la session (spec §4.2, §10.1) :
/// l'upload devient le fichier que <see cref="FileBuffer.Open(string, string)"/>
/// lit, sans jamais passer en entier par la mémoire.
/// </summary>
public static class SourceFile
{
    private const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// Copie un flux dans <paramref name="path"/>, coupé à
    /// <paramref name="maxBytes"/>, en calculant son SHA-256 au passage. Le
    /// fichier est forcé sur le disque à la fin. Une erreur ou une annulation
    /// le supprime.
    /// </summary>
    public static async Task<SourceCopy> CopyAsync(Stream source, string path, long maxBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        long copied = 0;
        bool truncated = false;
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                while (true)
                {
                    // une fois le plafond atteint, un octet de plus suffit à savoir que la copie est coupée
                    int want = (int)Math.Min(ChunkSize, Math.Max(1, maxBytes - copied));
                    int read = await source.ReadAsync(chunk.AsMemory(0, want), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    int keep = (int)Math.Min(read, maxBytes - copied);
                    if (keep < read) truncated = true;
                    if (keep > 0)
                    {
                        hash.AppendData(chunk, 0, keep);
                        await file.WriteAsync(chunk.AsMemory(0, keep), ct).ConfigureAwait(false);
                        copied += keep;
                    }
                    if (truncated) break;
                }
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            return new SourceCopy(copied, truncated, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // une copie à moitié faite qui reste ne doit pas masquer la vraie erreur
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
