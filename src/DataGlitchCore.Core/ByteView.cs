using System.Buffers;
using System.Numerics;

namespace DataGlitchCore.Core;

/// <summary>
/// Un état du fichier, lu par fenêtres : l'original ou l'état courant d'un
/// <see cref="FileBuffer"/>, l'avant ou l'après d'une passe. On n'y accède
/// jamais d'un bloc : on copie la plage dont on a besoin dans un tampon à soi,
/// ou on la lit en flux par <see cref="OpenRead"/>. L'original reste sur
/// disque, et l'état courant n'existe pas en entier : une lecture le recompose
/// à partir de l'original et des segments des passes (DataGlitchCore#7).
///
/// Les lectures concurrentes sont sûres. Une lecture pendant qu'une passe
/// s'applique ne l'est pas : comme pour le reste du buffer, l'appelant les
/// sérialise.
/// </summary>
public sealed class ByteView
{
    private readonly ByteSource _source;

    internal ByteView(ByteSource source) => _source = source;

    /// <summary>Taille de l'état, en octets.</summary>
    public long Length => _source.Length;

    /// <summary>
    /// Copie les octets à partir de <paramref name="offset"/> dans
    /// <paramref name="destination"/>, qu'il remplit en entier.
    /// </summary>
    /// <param name="offset">Premier octet à lire.</param>
    /// <param name="destination">Où copier : sa longueur fixe celle de la lecture.</param>
    /// <exception cref="ArgumentOutOfRangeException">La plage déborde de l'état.</exception>
    public void Read(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.Length > Length - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"La plage [{offset}, {offset + destination.Length}) déborde de l'état ({Length} octets).");
        }
        if (destination.IsEmpty) return;
        _source.Read(offset, destination);
    }

    /// <summary>
    /// Un flux en lecture seule et positionnable sur cet état, pour servir une
    /// requête de plage ou copier vers un autre flux. Chaque appel donne un
    /// flux indépendant, avec sa propre position.
    /// </summary>
    public Stream OpenRead() => new ByteViewStream(this);
}

/// <summary>Où une <see cref="ByteView"/> prend ses octets : l'original, ou l'original et des segments.</summary>
internal abstract class ByteSource
{
    public abstract long Length { get; }

    /// <summary>Lit une plage déjà vérifiée par <see cref="ByteView.Read"/>.</summary>
    public abstract void Read(long offset, Span<byte> destination);
}

/// <summary>Le flux de <see cref="ByteView.OpenRead"/>.</summary>
internal sealed class ByteViewStream(ByteView view) : Stream
{
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return view.Length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long remaining = view.Length - _position;
        if (remaining <= 0 || buffer.IsEmpty) return 0;

        int n = (int)Math.Min(buffer.Length, remaining);
        view.Read(_position, buffer[..n]);
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => view.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Position avant le début du flux.");
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("Flux en lecture seule.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Flux en lecture seule.");

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Lit une <see cref="ByteView"/> par fenêtres successives, dans un tampon
/// emprunté : les petites lectures rapprochées (en-têtes de chunk) et le
/// parcours d'une étendue ne coûtent qu'une lecture de la vue par fenêtre.
/// </summary>
internal sealed class ByteWindow : IDisposable
{
    private readonly ByteView _view;
    private readonly int _minSize;
    private readonly int _maxSize;
    private byte[]? _buffer;
    private long _start;
    private int _count;
    private long _lastRequest = -1;

    /// <summary>Une fenêtre de taille fixe.</summary>
    public ByteWindow(ByteView view, int size) : this(view, size, size)
    {
    }

    /// <summary>
    /// Une fenêtre qui s'adapte à l'écart entre deux lectures. Éloignées,
    /// elles chargent <paramref name="minSize"/> octets. Rapprochées (au plus
    /// un huitième de <paramref name="maxSize"/>), elles chargent de quoi en
    /// servir environ huit d'un coup, jusqu'à <paramref name="maxSize"/>.
    /// Chaque rechargement relit l'état à travers toutes ses couches : sur des
    /// en-têtes de chunk rapprochés, c'est le nombre de rechargements qui coûte.
    /// </summary>
    public ByteWindow(ByteView view, int minSize, int maxSize)
    {
        _view = view;
        _minSize = minSize;
        _maxSize = Math.Max(minSize, maxSize);
        _buffer = ArrayPool<byte>.Shared.Rent(_maxSize);
    }

    /// <summary>
    /// Ce que la fenêtre a à partir de <paramref name="offset"/>, au plus
    /// <paramref name="max"/> octets, et au moins un tant que
    /// <paramref name="offset"/> est dans la vue. La fenêtre est rechargée à
    /// <paramref name="offset"/> s'il n'y est pas.
    /// </summary>
    public ReadOnlySpan<byte> From(long offset, int max)
    {
        var buffer = Ensure(offset, 1);
        int at = (int)(offset - _start);
        return buffer.AsSpan(at, Math.Min(max, _count - at));
    }

    /// <summary>Exactement <paramref name="count"/> octets à partir de <paramref name="offset"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">La plage déborde de la vue.</exception>
    public ReadOnlySpan<byte> Exactly(long offset, int count) =>
        Ensure(offset, count).AsSpan((int)(offset - _start), count);

    /// <summary>Le tampon, rechargé à <paramref name="offset"/> s'il ne contient pas <paramref name="need"/> octets à partir de là.</summary>
    private byte[] Ensure(long offset, int need)
    {
        var buffer = _buffer;
        ObjectDisposedException.ThrowIf(buffer is null, this);
        long previous = _lastRequest;
        _lastRequest = offset;
        if (offset >= _start && offset + need <= _start + _count) return buffer;

        if (need > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(need), $"{need} octets d'un coup : plus que la fenêtre ({buffer.Length}).");
        long available = _view.Length - offset;
        if (offset < 0 || available < need)
        {
            // hors de la vue : c'est elle qui dit pourquoi, avec la même exception qu'une lecture directe
            _view.Read(offset, new byte[need]);
        }

        int count = (int)Math.Min(available, Math.Max(need, LoadSize(previous, offset, buffer.Length)));
        _view.Read(offset, buffer.AsSpan(0, count));
        _start = offset;
        _count = count;
        return buffer;
    }

    /// <summary>Combien charger à <paramref name="offset"/>, la lecture précédente ayant eu lieu à <paramref name="previous"/>.</summary>
    private int LoadSize(long previous, long offset, int bufferLength)
    {
        if (_maxSize == _minSize) return bufferLength;
        long gap = offset - previous;
        if (previous < 0 || gap <= 0 || gap > _maxSize / 8) return _minSize;
        return (int)Math.Clamp((long)BitOperations.RoundUpToPowerOf2((ulong)gap * 8), _minSize, _maxSize);
    }

    public void Dispose()
    {
        if (_buffer is null) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
    }
}
