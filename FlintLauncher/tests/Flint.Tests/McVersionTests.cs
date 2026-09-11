// SPDX-License-Identifier: GPL-3.0-only
// Port of tests/Version_test.cpp.
using Flint.Core.Versioning;
using Xunit;

namespace Flint.Tests;

public class McVersionTests
{
    public static TheoryData<string, string, bool, bool> VersionCompareCases() => new()
    {
        // first, second, lessThan, equal
        { "1.2.0", "1.2.0", false, true },
        { "1.42", "1.42", false, true },
        { "1.2.0", "1.2.1", true, false },
        { "1.2.0", "1.3.0", true, false },
        { "1.2.0", "2.2.0", true, false },
        { "1.2", "1.2.0", true, false },
        { "1.2", "1.2.1", true, false },
        { "1.2", "1.3.0", true, false },
        { "1.2", "2.2.0", true, false },
        { "1.41", "1.42", true, false },
        { "1.20.0-rc2", "1.20.1", true, false },
        { "1.2.1", "1.2.0", false, false },
        { "1.3.0", "1.2.0", false, false },
        { "2.2.0", "1.2.0", false, false },
        { "1.2.0", "1.2", false, false },
        { "1.2.1", "1.2", false, false },
        { "1.3.0", "1.2", false, false },
        { "2.2.0", "1.2", false, false },
        { "1.42", "1.41", false, false },
        { "1.20.2-rc2", "1.20.1", false, false },
    };

    [Theory]
    [MemberData(nameof(VersionCompareCases))]
    public void VersionCompare(string first, string second, bool lessThan, bool equal)
    {
        var v1 = new McVersion(first);
        var v2 = new McVersion(second);
        Assert.Equal(lessThan, v1 < v2);
        Assert.Equal(!lessThan && !equal, v1 > v2);
        Assert.Equal(equal, v1 == v2);
    }

    public static IEnumerable<object[]> FlexVerTestVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Version", "test_vectors.txt");
        var testNumber = 0;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = string.Join(' ', rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }
            testNumber += 1;

            var splitLine = line.Split('<');
            if (splitLine.Length == 2)
            {
                yield return new object[] { splitLine[0].Trim(), splitLine[1].Trim(), true, false };
                continue;
            }
            splitLine = line.Split('=');
            if (splitLine.Length == 2)
            {
                yield return new object[] { splitLine[0].Trim(), splitLine[1].Trim(), false, true };
                continue;
            }
            splitLine = line.Split('>');
            if (splitLine.Length == 2)
            {
                yield return new object[] { splitLine[0].Trim(), splitLine[1].Trim(), false, false };
                continue;
            }
            throw new InvalidOperationException($"Unexpected separator in the test vector: {line}");
        }
    }

    [Theory]
    [MemberData(nameof(FlexVerTestVectors))]
    public void FlexVerTestVector(string first, string second, bool lessThan, bool equal)
    {
        var v1 = new McVersion(first);
        var v2 = new McVersion(second);
        Assert.Equal(lessThan, v1 < v2);
        Assert.Equal(!lessThan && !equal, v1 > v2);
        Assert.Equal(equal, v1 == v2);
    }

    [Fact]
    public void StrictWeakOrder()
    {
        // "1.10 Pre-Release 1" is a pre-release ordered before b because ' ' is lower than '-';
        // "1.10-pre1" is a pre-release ordered before c which is an actual release.
        var a = new McVersion("1.10 Pre-Release 1");
        var b = new McVersion("1.10-pre1");
        var c = new McVersion("1.10");

        static bool R(McVersion x, McVersion y) => x < y;
        static bool E(McVersion x, McVersion y) => !R(x, y) && !R(y, x);

        // irreflexive
        Assert.False(R(a, a));
        Assert.False(R(b, b));
        Assert.False(R(c, c));
        // transitive
        Assert.True(R(a, b));
        Assert.True(R(b, c));
        Assert.True(R(a, c));
        // transitive equivalence
        Assert.Equal(E(a, b) && E(b, c), E(a, c));
    }

    [Fact]
    public void EqualVersionsHaveEqualHashes()
    {
        var a = new McVersion("1.5");
        var b = new McVersion("1.5+foobar");
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
