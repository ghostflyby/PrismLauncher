// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2013, 2015 ljfa-ag
//
// Port of libraries/libnbtplusplus/include/tag_compound.h and src/tag_compound.cpp.
// libnbt++ is LGPL-3.0-or-later (see COPYING.md); this port is conveyed under GPL-3.0-only per LGPL-3.0 section 2.
//
// Deviation from libnbt++: tag_compound stores its tags in a std::map<std::string, value>
// keyed map, so payload iteration is sorted lexicographically by key. This port keeps
// INSERTION ORDER instead (an OrderedDictionary keyed by tag name): tags are written
// back out in the order they were read or inserted. Minecraft clients accept any order
// and preserving it keeps rewritten files (e.g. World::rename's level.dat) byte-diffable
// apart from the edited value.

using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace FlintLauncher.Nbt;

public sealed class NbtCompound : NbtTag, IDictionary<string, NbtTag>, IReadOnlyDictionary<string, NbtTag>
{
    private readonly OrderedDictionary<string, NbtTag> _tags = new(StringComparer.Ordinal);

    public override NbtTagType Type => NbtTagType.Compound;

    public int Count => _tags.Count;

    public bool IsReadOnly => false;

    /// <summary>
    /// Gets or sets the tag under <paramref name="key"/>. Setting an existing key keeps
    /// its position; getting a missing key throws <see cref="KeyNotFoundException"/>.
    /// </summary>
    public NbtTag this[string key]
    {
        get
        {
            if (_tags.TryGetValue(key, out var value))
            {
                return value;
            }

            throw new KeyNotFoundException($"No NBT tag with key \"{key}\"");
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _tags[key] = value;
        }
    }

    public ICollection<string> Keys => _tags.Keys;

    public ICollection<NbtTag> Values => _tags.Values;

    /// <summary>Adds a tag under <paramref name="key"/>; throws when the key already exists.</summary>
    public void Add(string key, NbtTag value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _tags.Add(key, value);
    }

    /// <summary>Adds the tag only when the key is absent. Returns true when it was added.</summary>
    public bool TryAdd(string key, NbtTag value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _tags.TryAdd(key, value);
    }

    public bool ContainsKey(string key) => _tags.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out NbtTag value)
    {
        return _tags.TryGetValue(key, out value);
    }

    public bool Remove(string key) => _tags.Remove(key);

    public void Clear() => _tags.Clear();

    public IEnumerator<KeyValuePair<string, NbtTag>> GetEnumerator() => _tags.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    IEnumerable<string> IReadOnlyDictionary<string, NbtTag>.Keys => Keys;

    IEnumerable<NbtTag> IReadOnlyDictionary<string, NbtTag>.Values => Values;

    void ICollection<KeyValuePair<string, NbtTag>>.Add(KeyValuePair<string, NbtTag> item)
    {
        Add(item.Key, item.Value);
    }

    bool ICollection<KeyValuePair<string, NbtTag>>.Contains(KeyValuePair<string, NbtTag> item)
    {
        return ((ICollection<KeyValuePair<string, NbtTag>>)_tags).Contains(item);
    }

    void ICollection<KeyValuePair<string, NbtTag>>.CopyTo(KeyValuePair<string, NbtTag>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<string, NbtTag>>)_tags).CopyTo(array, arrayIndex);
    }

    bool ICollection<KeyValuePair<string, NbtTag>>.Remove(KeyValuePair<string, NbtTag> item)
    {
        return ((ICollection<KeyValuePair<string, NbtTag>>)_tags).Remove(item);
    }

    public override NbtTag Clone()
    {
        var copy = new NbtCompound();
        foreach (var (key, value) in _tags)
        {
            copy[key] = value.Clone();
        }

        return copy;
    }

    public override string ToString() => $"tag_compound(size={_tags.Count})";

    public override bool Equals(object? obj)
    {
        if (obj is not NbtCompound other || _tags.Count != other._tags.Count)
        {
            return false;
        }

        foreach (var (key, value) in _tags)
        {
            if (!other._tags.TryGetValue(key, out var otherValue) || !NbtList.NbtTagEquals(value, otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var entryHash = 0;
        foreach (var (key, value) in _tags)
        {
            entryHash ^= HashCode.Combine(key, NbtList.NbtTagHash(value));
        }

        return HashCode.Combine(_tags.Count, entryHash);
    }
}
