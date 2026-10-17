// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/libnbtplusplus/include/tag_compound.h and src/tag_compound.cpp.
//
// Deviation from libnbt++: tag_compound stores its tags in a std::map<std::string, value>
// keyed map, so payload iteration is sorted lexicographically by key. This port keeps
// INSERTION ORDER instead (a list-of-pairs plus a lookup dictionary): tags are written
// back out in the order they were read or inserted. Minecraft clients accept any order
// and preserving it keeps rewritten files (e.g. World::rename's level.dat) byte-diffable
// apart from the edited value.

namespace FlintLauncher.Nbt;

public sealed class NbtCompound : NbtTag
{
    private readonly List<KeyValuePair<string, NbtTag>> _entries = [];
    private readonly Dictionary<string, NbtTag> _map = new(StringComparer.Ordinal);

    public override NbtTagType Type => NbtTagType.Compound;

    public int Count => _entries.Count;

    /// <summary>
    /// Port of tag_compound::at: bounds-checked access. Throws KeyNotFoundException
    /// when the key does not exist (the C++ throws std::out_of_range).
    /// </summary>
    public NbtTag this[string key]
    {
        get
        {
            if (_map.TryGetValue(key, out var value))
            {
                return value;
            }

            throw new KeyNotFoundException($"No NBT tag with key \"{key}\"");
        }
    }

    /// <summary>Lookup that reports presence instead of throwing.</summary>
    public bool TryGet(string key, out NbtTag? value)
    {
        return _map.TryGetValue(key, out value);
    }

    /// <summary>
    /// Typed lookup: returns the tag under <paramref name="key"/> cast to
    /// <typeparamref name="T"/>, or null when the key is missing or of another type.
    /// Replaces the C++ try { at(name).as&lt;T&gt;() } catch(...) idiom.
    /// </summary>
    public T? TryGet<T>(string key) where T : NbtTag
    {
        return _map.TryGetValue(key, out var value) && value is T typed ? typed : null;
    }

    /// <summary>Port of tag_compound::has_key(key).</summary>
    public bool HasKey(string key) => _map.ContainsKey(key);

    /// <summary>Port of tag_compound::has_key(key, type).</summary>
    public bool HasKey(string key, NbtTagType type)
    {
        return _map.TryGetValue(key, out var value) && value.Type == type;
    }

    /// <summary>
    /// Port of tag_compound::put: inserts or assigns. Assigning an existing key keeps
    /// its original insertion position (like assigning to a map slot).
    /// </summary>
    public void Put(string key, NbtTag value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_map.TryGetValue(key, out var existing))
        {
            if (ReferenceEquals(existing, value))
            {
                return;
            }

            _map[key] = value;
            var index = _entries.FindIndex(kv => kv.Key == key);
            _entries[index] = new KeyValuePair<string, NbtTag>(key, value);
        }
        else
        {
            _map.Add(key, value);
            _entries.Add(new KeyValuePair<string, NbtTag>(key, value));
        }
    }

    /// <summary>
    /// Port of tag_compound::insert: inserts only when the key is absent.
    /// Returns true when the value was inserted.
    /// </summary>
    public bool Insert(string key, NbtTag value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_map.ContainsKey(key))
        {
            return false;
        }

        _map.Add(key, value);
        _entries.Add(new KeyValuePair<string, NbtTag>(key, value));
        return true;
    }

    /// <summary>Port of tag_compound::erase. Returns true when a tag was removed.</summary>
    public bool Remove(string key)
    {
        if (!_map.Remove(key))
        {
            return false;
        }

        _entries.RemoveAll(kv => kv.Key == key);
        return true;
    }

    /// <summary>Port of tag_compound::clear.</summary>
    public void Clear()
    {
        _map.Clear();
        _entries.Clear();
    }

    /// <summary>The tags in insertion order (only iteration that guarantees order).</summary>
    public IEnumerable<KeyValuePair<string, NbtTag>> Entries => _entries;

    public override NbtTag Clone()
    {
        var copy = new NbtCompound();
        foreach (var (key, value) in _entries)
        {
            copy.Put(key, value.Clone());
        }

        return copy;
    }

    public override string ToString() => $"tag_compound(size={_entries.Count})";

    public override bool Equals(object? obj)
    {
        if (obj is not NbtCompound other || _entries.Count != other._entries.Count)
        {
            return false;
        }

        foreach (var (key, value) in _entries)
        {
            if (!other._map.TryGetValue(key, out var otherValue) || !NbtList.NbtTagEquals(value, otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _entries)
        {
            hash.Add(key);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}
