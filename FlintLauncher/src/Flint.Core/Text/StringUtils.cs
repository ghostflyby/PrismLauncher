// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/StringUtils.{h,cpp} (partial: only what has call sites and tests so far).
// Per-char comparisons use ordinal ordering instead of Qt's locale-aware compare; relevant
// call sites (version prerelease ordering) are ASCII, where both agree.
namespace Flint.Core.Text;

public static class StringUtils
{
    /// <summary>
    /// Port of StringUtils::naturalCompare (itself taken from Qt): compares strings containing
    /// numbers by their natural order ("a9" &lt; "a10"). Returns &lt;0, 0 or &gt;0.
    /// </summary>
    public static int NaturalCompare(string s1, string s2, bool ignoreCase = false)
    {
        var l1 = 0;
        var l2 = 0;
        while (l1 <= s1.Length && l2 <= s2.Length)
        {
            // skip spaces, tabs and 0's
            var c1 = GetNextChar(s1, l1);
            while (char.IsWhiteSpace(c1))
            {
                c1 = GetNextChar(s1, ++l1);
            }

            var c2 = GetNextChar(s2, l2);
            while (char.IsWhiteSpace(c2))
            {
                c2 = GetNextChar(s2, ++l2);
            }

            if (char.IsDigit(c1) && char.IsDigit(c2))
            {
                while (DigitValue(c1) == 0)
                {
                    c1 = GetNextChar(s1, ++l1);
                }
                while (DigitValue(c2) == 0)
                {
                    c2 = GetNextChar(s2, ++l2);
                }

                var lookAheadLocation1 = l1;
                var lookAheadLocation2 = l2;
                var currentReturnValue = 0;
                // find the last digit, setting currentReturnValue as we go if it isn't equal
                var lookAhead1 = c1;
                var lookAhead2 = c2;
                while (lookAheadLocation1 <= s1.Length && lookAheadLocation2 <= s2.Length)
                {
                    var is1ADigit = lookAhead1 != '\0' && char.IsDigit(lookAhead1);
                    var is2ADigit = lookAhead2 != '\0' && char.IsDigit(lookAhead2);
                    if (!is1ADigit && !is2ADigit)
                    {
                        break;
                    }
                    if (!is1ADigit)
                    {
                        return -1;
                    }
                    if (!is2ADigit)
                    {
                        return 1;
                    }
                    if (currentReturnValue == 0)
                    {
                        if (lookAhead1 < lookAhead2)
                        {
                            currentReturnValue = -1;
                        }
                        else if (lookAhead1 > lookAhead2)
                        {
                            currentReturnValue = 1;
                        }
                    }
                    lookAhead1 = GetNextChar(s1, ++lookAheadLocation1);
                    lookAhead2 = GetNextChar(s2, ++lookAheadLocation2);
                }
                if (currentReturnValue != 0)
                {
                    return currentReturnValue;
                }
            }

            if (ignoreCase)
            {
                c1 = char.ToLowerInvariant(c1);
                c2 = char.ToLowerInvariant(c2);
            }

            if (c1 != c2)
            {
                return c1 < c2 ? -1 : 1;
            }

            l1 += 1;
            l2 += 1;
        }

        // The two strings are the same (02 == 2) so fall back to the normal sort
        return ignoreCase ? string.Compare(s1, s2, StringComparison.OrdinalIgnoreCase) : string.CompareOrdinal(s1, s2);
    }

    private static char GetNextChar(string s, int location) => location < s.Length ? s[location] : '\0';

    private static int DigitValue(char c) => char.IsDigit(c) ? c - '0' : -1;
}
