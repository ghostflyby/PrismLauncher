// SPDX-License-Identifier: GPL-3.0-only
// Port of libraries/libnbtplusplus/include/tag_list.h and src/tag_list.cpp.
// An ordered list of tags that must all share one element type. An empty list has the
// undetermined element type NbtTagType.Null; it is serialized with element type End
// (and its length, which libnbt++ also ignores when reading End, is written as 0).

namespace FlintLauncher.Nbt;

public sealed class NbtList : NbtTag
{
    private readonly List<NbtTag> _tags = [];

    public NbtList() { }

    /// <summary>Port of tag_list(tag_type type): an empty list with a preset element type.</summary>
    public NbtList(NbtTagType elementType)
    {
        if (elementType == NbtTagType.End)
        {
            throw new ArgumentException("tag_type::End cannot be used as a list element type", nameof(elementType));
        }
        ElementType = elementType;
    }

    /// <summary>The element type; Null while the list is empty and undetermined (nbt::tag_list::el_type).</summary>
    public NbtTagType ElementType { get; private set; } = NbtTagType.Null;

    public int Count => _tags.Count;

    /// <summary>Port of tag_list::at: bounds-checked access (throws for invalid indices).</summary>
    public NbtTag this[int index] => _tags[index];

    /// <summary>The elements in order.</summary>
    public IReadOnlyList<NbtTag> Tags => _tags;

    /// <summary>Port of tag_list::push_back: appends, setting the element type if undetermined.</summary>
    /// <exception cref="ArgumentException">If the tag type does not match the element type.</exception>
    public void Add(NbtTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag); // push_back rejects null values
        if (ElementType == NbtTagType.Null)
        {
            ElementType = tag.Type;
        }
        else if (ElementType != tag.Type)
        {
            throw new ArgumentException(
                $"The tag type ({tag.Type}) does not match the list's content type ({ElementType})", nameof(tag));
        }
        _tags.Add(tag);
    }

    /// <summary>Port of tag_list::reset: clears the list and sets a new element type.</summary>
    public void Reset(NbtTagType elementType)
    {
        if (elementType == NbtTagType.End)
        {
            throw new ArgumentException("tag_type::End cannot be used as a list element type", nameof(elementType));
        }
        _tags.Clear();
        ElementType = elementType;
    }

    public override NbtTagType Type => NbtTagType.List;

    public override NbtTag Clone()
    {
        var copy = new NbtList(ElementType);
        foreach (var tag in _tags)
        {
            copy._tags.Add(tag.Clone());
        }
        return copy;
    }

    public override string ToString() => $"tag_list(el={ElementType}, size={_tags.Count})";

    public override bool Equals(object? obj)
    {
        if (obj is not NbtList other || ElementType != other.ElementType || _tags.Count != other._tags.Count)
        {
            return false;
        }
        for (var i = 0; i < _tags.Count; i++)
        {
            if (!NbtTagEquals(_tags[i], other._tags[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ElementType);
        foreach (var tag in _tags)
        {
            hash.Add(tag);
        }
        return hash.ToHashCode();
    }

    internal static bool NbtTagEquals(NbtTag? a, NbtTag? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null || a.Type != b.Type)
        {
            return false;
        }
        return a switch
        {
            NbtByte t => t.Value == ((NbtByte)b).Value,
            NbtShort t => t.Value == ((NbtShort)b).Value,
            NbtInt t => t.Value == ((NbtInt)b).Value,
            NbtLong t => t.Value == ((NbtLong)b).Value,
            NbtFloat t => t.Equals(b),
            NbtDouble t => t.Equals(b),
            NbtString t => t.Equals(b),
            NbtByteArray t => t.Equals(b),
            NbtIntArray t => t.Equals(b),
            NbtLongArray t => t.Equals(b),
            NbtList t => t.Equals(b),
            NbtCompound t => t.Equals(b),
            _ => false,
        };
    }
}
