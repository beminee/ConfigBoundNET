// Copyright (c) ConfigBoundNET contributors. Licensed under the GPL-3 License.

using System;
using System.Collections;
using System.Collections.Generic;

namespace ConfigBoundNET;

/// <summary>
/// A thin, value-equatable wrapper around <c>T[]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Incremental source generators rely on value equality of their pipeline
/// outputs to cache work across edits. <c>ImmutableArray&lt;T&gt;</c> uses
/// reference equality, which defeats caching: a new array instance with the
/// same contents would look different to the pipeline and force re-emission.
/// </para>
/// <para>
/// <see cref="EquatableArray{T}"/> fixes that by computing structural equality
/// element-wise. It is the canonical pattern recommended by the .NET team
/// for incremental generator models.
/// </para>
/// </remarks>
/// <typeparam name="T">The element type. Must itself be value-equatable.</typeparam>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array;

    /// <summary>Initializes a new <see cref="EquatableArray{T}"/> wrapping the given array.</summary>
    /// <param name="array">The backing array. <see langword="null"/> is treated as empty.</param>
    public EquatableArray(T[]? array)
    {
        _array = array;
    }

    /// <summary>Gets the number of elements in the underlying array.</summary>
    public int Length => _array?.Length ?? 0;

    /// <summary>Returns a reference to the backing array (creating an empty one if needed).</summary>
    /// <remarks>Callers must not mutate the returned array; it is shared with the struct.</remarks>
    public T[] AsArray() => _array ?? Array.Empty<T>();

    /// <summary>Gets the element at the given index.</summary>
    public T this[int index] => AsArray()[index];

    /// <inheritdoc />
    public bool Equals(EquatableArray<T> other)
    {
        var a = _array;
        var b = other._array;

        if (ReferenceEquals(a, b))
        {
            return true;
        }

        // Null and empty compare equal; a is-null with b non-empty (or vice versa) is not.
        if (a is null)
        {
            return b!.Length == 0;
        }

        if (b is null)
        {
            return a.Length == 0;
        }

        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var array = _array;
        if (array is null || array.Length == 0)
        {
            return 0;
        }

        // FNV-1a-ish. We just need something stable and well-distributed.
        unchecked
        {
            int hash = 17;
            foreach (var item in array)
            {
                hash = (hash * 31) + (item?.GetHashCode() ?? 0);
            }

            return hash;
        }
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)AsArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => AsArray().GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}
