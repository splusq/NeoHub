// The one sanctioned use of Volatile/Interlocked (see BannedSymbols.txt).
#pragma warning disable RS0030

namespace DSC.TLink.Concurrency;

/// <summary>
/// An immutable value shared across threads without locks. This file is the only place in the
/// solution that uses <see cref="Volatile"/> or <see cref="Interlocked"/>; everything else shares
/// state through channels or through this type.
/// <para>
/// Two ways to change it: <see cref="Publish"/> when one owner (an actor's loop) is the only
/// writer, or <see cref="Update"/> with a pure function when several threads may write (it
/// retries the function on contention, so it must have no side effects). <typeparamref name="T"/>
/// must be immutable.
/// </para>
/// </summary>
public sealed class Snapshot<T>(T initial) where T : class
{
    private T _value = initial;

    /// <summary>The current value (complete, possibly a moment old).</summary>
    public T Value => Volatile.Read(ref _value);

    /// <summary>Replaces the value. For a single owner only.</summary>
    public void Publish(T value) => Volatile.Write(ref _value, value);

    /// <summary>Applies a pure <paramref name="change"/> atomically and returns the new value.</summary>
    public T Update(Func<T, T> change)
    {
        while (true)
        {
            var current = Volatile.Read(ref _value);
            var next = change(current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _value, next, current), current))
                return next;
        }
    }
}
