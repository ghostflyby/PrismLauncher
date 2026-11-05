// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Tests for the FlintLauncher.Nbt port of libnbt++: synthetic round-trips over all tag
// types, deep nesting, lists of compounds, gzip vs raw IO, Java modified UTF-8 vectors
// and truncation/corruption errors. No real user level.dat data is used.

using System.Buffers.Binary;
using System.Text;
using FlintLauncher.Nbt;
using Xunit;

namespace FlintLauncher.Tests;

public class NbtTests
{
    // ---------------------------------------------------------------- round trips

    [Fact]
    public void RoundTrip_AllTagTypes_Raw()
    {
        var root = MakeAllTypesCompound();

        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        var (name, read) = NbtIo.ReadCompound(new MemoryStream(bytes));

        Assert.Equal("", name);
        Assert.True(root.Equals(read), "compound should round-trip exactly");
    }

    [Fact]
    public void RoundTrip_AllTagTypes_Gzip()
    {
        var root = MakeAllTypesCompound();

        var bytes = NbtIo.WriteTagToBytes("", root, gzip: true);
        Assert.Equal(0x1F, bytes[0]); // gzip magic
        Assert.Equal(0x8B, bytes[1]);

        var (name, read) = NbtIo.ReadCompound(new MemoryStream(bytes));
        Assert.Equal("", name);
        Assert.True(root.Equals(read));
    }

    [Fact]
    public void Read_AutoDetects_RawAndGzip()
    {
        var root = new NbtCompound();
        root["value"] = new NbtInt(42);

        var raw = NbtIo.WriteTagToBytes("", root, gzip: false);
        var gzip = NbtIo.WriteTagToBytes("", root, gzip: true);
        Assert.NotEqual(raw, gzip);

        Assert.Equal(42, ((NbtInt)NbtIo.ReadCompound(new MemoryStream(raw)).Root["value"]).Value);
        Assert.Equal(42, ((NbtInt)NbtIo.ReadCompound(new MemoryStream(gzip)).Root["value"]).Value);
    }

    [Fact]
    public void RoundTrip_DeepNesting()
    {
        // 900 nested compounds round-trip (limit is 1024, matching libnbt++ MAX_DEPTH)
        var root = NestedCompound(900);
        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        var (_, read) = NbtIo.ReadCompound(new MemoryStream(bytes));
        Assert.Equal(900, CountDepth(read));
    }

    [Fact]
    public void RoundTrip_ListOfCompounds()
    {
        var list = new NbtList();
        foreach (var i in new[] { 0, 1, 2 })
        {
            var entry = new NbtCompound();
            entry["index"] = new NbtInt(i);
            entry["name"] = new NbtString($"item {i}");
            list.Add(entry);
        }

        var root = new NbtCompound();
        root["entries"] = list;

        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        var (_, read) = NbtIo.ReadCompound(new MemoryStream(bytes));

        var reread = (NbtList)read["entries"];
        Assert.Equal(NbtTagType.Compound, reread.ElementType);
        Assert.Equal(3, reread.Count);
        Assert.Equal(1, ((NbtInt)((NbtCompound)reread[1])["index"]).Value);
        Assert.Equal("item 2", ((NbtString)((NbtCompound)reread[2])["name"]).Value);
    }

    [Fact]
    public void RoundTrip_EmptyList_And_TypedLists()
    {
        var root = new NbtCompound();
        root["undetermined"] = new NbtList();
        var ints = new NbtList();
        ints.Add(new NbtInt(1));
        ints.Add(new NbtInt(-1));
        root["ints"] = ints;

        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        var (_, read) = NbtIo.ReadCompound(new MemoryStream(bytes));

        var undetermined = (NbtList)read["undetermined"];
        Assert.Empty(undetermined);
        Assert.Equal(NbtTagType.Null, undetermined.ElementType);
        Assert.Equal(NbtTagType.Int, ((NbtList)read["ints"]).ElementType);
        Assert.Equal(-1, ((NbtInt)((NbtList)read["ints"])[1]).Value);
    }

    [Fact]
    public void Compound_PreservesInsertionOrder()
    {
        // Deviation from libnbt++ (std::map ordering): insertion order is kept.
        var root = new NbtCompound();
        root["zeta"] = new NbtInt(1);
        root["alpha"] = new NbtInt(2);
        root["middle"] = new NbtInt(3);

        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        var (_, read) = NbtIo.ReadCompound(new MemoryStream(bytes));

        Assert.Equal(new[] { "zeta", "alpha", "middle" }, read.Keys.ToArray());
    }

    [Fact]
    public void Compound_Indexer_KeepsPositionOnReplace()
    {
        var root = new NbtCompound();
        root["first"] = new NbtString("one");
        root["second"] = new NbtString("two");
        root["first"] = new NbtString("uno");

        Assert.Equal(new[] { "first", "second" }, root.Keys.ToArray());
        Assert.Equal("uno", ((NbtString)root["first"]).Value);
        Assert.False(root.TryAdd("second", new NbtString("nope"))); // insert never replaces
    }

    [Fact]
    public void Compound_DictionaryApi()
    {
        var root = new NbtCompound();
        Assert.False(root.IsReadOnly);

        root.Add("a", new NbtInt(1));
        root["b"] = new NbtInt(2);
        Assert.True(root.TryAdd("c", new NbtInt(3)));
        Assert.False(root.TryAdd("c", new NbtInt(30)));
        Assert.Throws<ArgumentException>(() => root.Add("a", new NbtInt(10)));

        Assert.Equal(3, root.Count);
        Assert.Equal(new[] { "a", "b", "c" }, root.Keys.ToArray());
        Assert.Equal(3, root.Values.Count);
        Assert.True(root.ContainsKey("a"));
        Assert.False(root.ContainsKey("missing"));
        Assert.True(root.TryGetValue("b", out var b) && b is NbtInt { Value: 2 });
        Assert.Equal(new[] { "a", "b", "c" }, root.Select(e => e.Key).ToArray());

        IReadOnlyDictionary<string, NbtTag> readOnly = root;
        Assert.Equal(3, readOnly.Count);
        Assert.Equal(new[] { "a", "b", "c" }, readOnly.Keys.ToArray());
        Assert.Equal(2, ((NbtInt)readOnly["b"]).Value);

        Assert.True(root.Remove("b"));
        Assert.False(root.Remove("missing"));
        Assert.Equal(new[] { "a", "c" }, root.Keys.ToArray());

        root.Clear();
        Assert.Empty(root);
    }

    [Fact]
    public void Compound_Equality_IgnoresInsertionOrder_AndHashesConsistently()
    {
        var first = new NbtCompound();
        first["a"] = new NbtInt(1);
        first["b"] = new NbtString("two");

        var second = new NbtCompound();
        second["b"] = new NbtString("two");
        second["a"] = new NbtInt(1);

        Assert.True(first.Equals(second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Single(new HashSet<NbtCompound> { first, second });
    }

    [Fact]
    public void Compound_AndList_HashPrimitiveValuesNotInstances()
    {
        var firstCompound = new NbtCompound();
        firstCompound["byte"] = new NbtByte(1);
        firstCompound["short"] = new NbtShort(2);
        firstCompound["int"] = new NbtInt(3);
        firstCompound["long"] = new NbtLong(4);

        var secondCompound = new NbtCompound();
        secondCompound["byte"] = new NbtByte(1);
        secondCompound["short"] = new NbtShort(2);
        secondCompound["int"] = new NbtInt(3);
        secondCompound["long"] = new NbtLong(4);

        Assert.True(firstCompound.Equals(secondCompound));
        Assert.Equal(firstCompound.GetHashCode(), secondCompound.GetHashCode());
        Assert.Single(new HashSet<NbtCompound> { firstCompound, secondCompound });

        var firstList = new NbtList();
        firstList.Add(new NbtInt(1));
        firstList.Add(new NbtInt(2));

        var secondList = new NbtList();
        secondList.Add(new NbtInt(1));
        secondList.Add(new NbtInt(2));

        Assert.True(firstList.Equals(secondList));
        Assert.Equal(firstList.GetHashCode(), secondList.GetHashCode());
        Assert.Single(new HashSet<NbtList> { firstList, secondList });
    }

    [Fact]
    public void Compound_PairMembers_UseStructuralTagEquality()
    {
        var root = new NbtCompound();
        root["k"] = new NbtInt(1);
        var clone = (NbtCompound)root.Clone();
        var pair = new KeyValuePair<string, NbtTag>("k", clone["k"]);

        Assert.True(root.Equals(clone));
        Assert.True(((ICollection<KeyValuePair<string, NbtTag>>)root).Contains(pair));
        Assert.True(root.Values.Contains(clone["k"]));

        var copied = new KeyValuePair<string, NbtTag>[root.Count];
        ((ICollection<KeyValuePair<string, NbtTag>>)root).CopyTo(copied, 0);
        Assert.Equal("k", Assert.Single(copied).Key);

        Assert.True(((ICollection<KeyValuePair<string, NbtTag>>)root).Remove(pair));
        Assert.False(root.ContainsKey("k"));
    }

    [Fact]
    public void RoundTrip_PrimitiveExtremes()
    {
        var root = new NbtCompound();
        root["b"] = new NbtByte(-128);
        root["s"] = new NbtShort(short.MinValue);
        root["i"] = new NbtInt(int.MinValue);
        root["l"] = new NbtLong(long.MinValue);
        root["l2"] = new NbtLong(long.MaxValue);
        root["f"] = new NbtFloat(1.0f / 3.0f);
        root["d"] = new NbtDouble(1.0 / 3.0);

        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        var (_, read) = NbtIo.ReadCompound(new MemoryStream(bytes));

        Assert.Equal(-128, ((NbtByte)read["b"]).Value);
        Assert.Equal(short.MinValue, ((NbtShort)read["s"]).Value);
        Assert.Equal(int.MinValue, ((NbtInt)read["i"]).Value);
        Assert.Equal(long.MinValue, ((NbtLong)read["l"]).Value);
        Assert.Equal(long.MaxValue, ((NbtLong)read["l2"]).Value);
        // float/double must survive bit-exact
        var f = (NbtFloat)read["f"];
        var d = (NbtDouble)read["d"];
        Assert.Equal(BitConverter.SingleToInt32Bits(1.0f / 3.0f), BitConverter.SingleToInt32Bits(f.Value));
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0 / 3.0), BitConverter.DoubleToInt64Bits(d.Value));
    }

    [Fact]
    public void ReadCompound_RejectsNonCompoundRoot()
    {
        var bytes = NbtIo.WriteTagToBytes("", new NbtInt(5), gzip: false);
        Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(new MemoryStream(bytes)));
    }

    [Fact]
    public void ReadTag_ReturnsRootNameAndTag()
    {
        var bytes = NbtIo.WriteTagToBytes("rootname", new NbtLong(77), gzip: false);
        var (name, tag) = NbtIo.ReadTag(new MemoryStream(bytes));
        Assert.Equal("rootname", name);
        Assert.Equal(77L, ((NbtLong)tag).Value);
    }

    // ------------------------------------------------------------------ nesting cap

    [Fact]
    public void Read_TooDeepNesting_Throws()
    {
        // 1500 nested compounds exceed the 1024 depth cap (write side is uncapped,
        // like libnbt++'s writer)
        var root = NestedCompound(1500);
        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        var ex = Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(new MemoryStream(bytes)));
        Assert.Contains("deeply nested", ex.Message);
    }

    // ------------------------------------------------------------- modified UTF-8

    [Fact]
    public void ModifiedUtf8_Nul_IsTwoBytes()
    {
        // Java modified UTF-8: U+0000 -> C0 80 (plain UTF-8: a single 00 byte)
        Assert.Equal(new byte[] { 0xC0, 0x80 }, ModifiedUtf8.Encode("\0"));
        Assert.Equal("\0", ModifiedUtf8.Decode([0xC0, 0x80]));
    }

    [Fact]
    public void ModifiedUtf8_AstralChar_IsTwoSurrogateTriplets()
    {
        // U+10000: .NET UTF-8 emits F0 90 80 80 (4 bytes);
        // modified UTF-8 emits ED A0 80 ED B0 80 (surrogate pair D800 DC00, 3 bytes each)
        var netUtf8 = Encoding.UTF8.GetBytes("\U00010000");
        Assert.Equal(new byte[] { 0xF0, 0x90, 0x80, 0x80 }, netUtf8);

        var modified = ModifiedUtf8.Encode("\U00010000");
        Assert.Equal(new byte[] { 0xED, 0xA0, 0x80, 0xED, 0xB0, 0x80 }, modified);

        Assert.Equal("\U00010000", ModifiedUtf8.Decode(modified));
    }

    [Fact]
    public void ModifiedUtf8_MixedString_RoundTrip()
    {
        var value = "a\0\u00e9\u4f60\u597d\U0001F600"; // 1-byte, NUL, 2-byte, 3-byte, astral
        var bytes = ModifiedUtf8.Encode(value);
        Assert.Equal(value, ModifiedUtf8.Decode(bytes));
    }

    [Fact]
    public void ModifiedUtf8_LoneSurrogate_RoundTrips()
    {
        // readUTF-compatible leniency: a lone surrogate survives the round trip
        var bytes = ModifiedUtf8.Encode("\ud83d");
        Assert.Equal(new byte[] { 0xED, 0xA0, 0xBD }, bytes);
        Assert.Equal("\ud83d", ModifiedUtf8.Decode(bytes));
    }

    [Fact]
    public void ModifiedUtf8_Decodes_PlainUtf8AstralSequence_Leniently()
    {
        // Some tools write standard UTF-8; Java's readUTF rejects it but we accept it.
        Assert.Equal("\U00010000", ModifiedUtf8.Decode([0xF0, 0x90, 0x80, 0x80]));
    }

    [Fact]
    public void ModifiedUtf8_MalformedInput_Throws()
    {
        Assert.Throws<NbtParseException>(() => ModifiedUtf8.Decode([0x80]));             // lone continuation byte
        Assert.Throws<NbtParseException>(() => ModifiedUtf8.Decode([0xC3]));             // truncated 2-byte
        Assert.Throws<NbtParseException>(() => ModifiedUtf8.Decode([0xE4, 0xBD]));       // truncated 3-byte
        Assert.Throws<NbtParseException>(() => ModifiedUtf8.Decode([0xE4, 0xBD, 0xC3])); // bad continuation
    }

    [Fact]
    public void NbtString_UsesModifiedUtf8_OnTheWire()
    {
        var root = new NbtCompound();
        root["nul"] = new NbtString("a\0b");

        var bytes = NbtIo.WriteTagToBytes("", root, gzip: false);
        // the wire bytes must contain C0 80 for the embedded NUL
        Assert.True(Contains(bytes, [0xC0, 0x80]), "embedded NUL must be encoded as C0 80");

        var (_, read) = NbtIo.ReadCompound(new MemoryStream(bytes));
        Assert.Equal("a\0b", ((NbtString)read["nul"]).Value);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    // ----------------------------------------------------------------- corruption

    [Fact]
    public void Read_TruncatedInput_ThrowsInsteadOfHanging()
    {
        var full = NbtIo.WriteTagToBytes("", MakeAllTypesCompound(), gzip: false);
        for (var cut = 0; cut < full.Length; cut++)
        {
            var truncated = full[..cut];
            Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(new MemoryStream(truncated)));
        }
    }

    [Fact]
    public void Read_TruncatedGzip_Throws()
    {
        var full = NbtIo.WriteTagToBytes("", MakeAllTypesCompound(), gzip: true);
        Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(new MemoryStream(full[..(full.Length / 2)])));
    }

    [Fact]
    public void Read_InvalidTagType_Throws()
    {
        // root type byte 0x7F is not a valid tag type
        var bytes = new byte[] { 0x7F, 0x00, 0x00 };
        var ex = Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(new MemoryStream(bytes)));
        Assert.Contains("Invalid tag type", ex.Message);
    }

    [Fact]
    public void Read_NegativeListLength_Throws()
    {
        var buffer = new MemoryStream();
        // root compound containing a list whose length is negative (type, name, payload)
        buffer.WriteByte((byte)NbtTagType.Compound);
        WriteStringBytes(buffer, "");
        buffer.WriteByte((byte)NbtTagType.List); // child type
        WriteStringBytes(buffer, "bad");         // child name
        WriteI32(buffer, -5);                    // negative length
        buffer.WriteByte(0);                     // compound end
        buffer.Position = 0;

        Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(buffer));
    }

    [Fact]
    public void Read_NegativeArrayLength_Throws()
    {
        var buffer = new MemoryStream();
        buffer.WriteByte((byte)NbtTagType.Compound);
        WriteStringBytes(buffer, "");
        buffer.WriteByte((byte)NbtTagType.ByteArray);
        WriteStringBytes(buffer, "bad");
        WriteI32(buffer, -1);
        buffer.WriteByte(0);
        buffer.Position = 0;

        Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(buffer));
    }

    [Fact]
    public void Read_CorruptHugeArrayLength_FailsAtEof()
    {
        // array length claims ~2G elements but the stream ends immediately;
        // the reader must fail fast instead of trying to allocate
        var buffer = new MemoryStream();
        buffer.WriteByte((byte)NbtTagType.Compound);
        WriteStringBytes(buffer, "");
        buffer.WriteByte((byte)NbtTagType.LongArray);
        WriteStringBytes(buffer, "huge");
        WriteI32(buffer, int.MaxValue);
        buffer.Position = 0;

        Assert.Throws<NbtParseException>(() => NbtIo.ReadCompound(buffer));
    }

    private static void WriteI32(MemoryStream stream, int value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(tmp, value);
        stream.Write(tmp);
    }

    private static void WriteStringBytes(MemoryStream stream, string value)
    {
        var encoded = ModifiedUtf8.Encode(value);
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(tmp, (ushort)encoded.Length);
        stream.Write(tmp);
        stream.Write(encoded, 0, encoded.Length);
    }

    // ------------------------------------------------------------- write guards

    [Fact]
    public void Write_StringTooLong_Throws()
    {
        var tooLong = new string('a', 65536);
        var root = new NbtCompound();
        root["big"] = new NbtString(tooLong);
        Assert.Throws<ArgumentOutOfRangeException>(() => NbtIo.WriteTagToBytes("", root, gzip: false));

        // exactly at the limit is fine (65535 ASCII bytes == 65535 encoded bytes)
        var atLimit = new string('a', 65535);
        var ok = new NbtCompound();
        ok["big"] = new NbtString(atLimit);
        var bytes = NbtIo.WriteTagToBytes("", ok, gzip: false);
        var (_, read) = NbtIo.ReadCompound(new MemoryStream(bytes));
        Assert.Equal(65535, ((NbtString)read["big"]).Value.Length);
    }

    [Fact]
    public void List_Add_MixedTypes_Throws()
    {
        var list = new NbtList();
        list.Add(new NbtInt(1));
        Assert.Throws<ArgumentException>(() => list.Add(new NbtString("nope")));
    }

    // ----------------------------------------------------------------- helpers

    private static NbtCompound MakeAllTypesCompound()
    {
        var root = new NbtCompound();
        root["byte"] = new NbtByte(42);
        root["short"] = new NbtShort(1337);
        root["int"] = new NbtInt(123456);
        root["long"] = new NbtLong(1234567890123L);
        root["float"] = new NbtFloat(1.5f);
        root["double"] = new NbtDouble(2.5);
        root["string"] = new NbtString("héllo wörld");
        root["bytearray"] = new NbtByteArray([1, 2, 3, 4, 255]);
        root["intarray"] = new NbtIntArray([int.MinValue, 0, int.MaxValue]);
        root["longarray"] = new NbtLongArray([long.MinValue, 0, long.MaxValue]);

        var inner = new NbtCompound();
        inner["flag"] = new NbtByte(1);
        inner["text"] = new NbtString("nested");
        root["compound"] = inner;

        var list = new NbtList();
        list.Add(new NbtShort(7));
        list.Add(new NbtShort(8));
        root["shorts"] = list;

        var emptyList = new NbtList();
        root["empty"] = emptyList;
        return root;
    }

    private static NbtCompound NestedCompound(int depth)
    {
        var root = new NbtCompound();
        var current = root;
        for (var i = 0; i < depth; i++)
        {
            var next = new NbtCompound();
            current["child"] = next;
            current = next;
        }

        current["leaf"] = new NbtInt(1);
        return root;
    }

    private static int CountDepth(NbtCompound root)
    {
        var depth = 0;
        var current = root;
        while (current.TryGetValue("child", out var tag) && tag is NbtCompound child)
        {
            depth++;
            current = child;
        }

        return depth;
    }
}
