// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/ParseUtils.{h,cpp}.
//
// QDateTime is represented by DateTimeOffset? here: null mirrors the null/invalid QDateTime
// (QDateTime::isNull()), and the offset mirrors Qt::OffsetFromUTC. Callers guard with HasValue
// where the C++ code checks isNull().

using System.Globalization;

namespace FlintLauncher.Core.Minecraft;

public static class ParseUtils
{
    /// <summary>take the timestamp used by S3 and turn it into a QDateTime (null when unparseable)</summary>
    public static DateTimeOffset? TimeFromS3Time(string str)
    {
        // QDateTime::fromString(str, Qt::ISODate): offset form "+hh:mm"/"-hh:mm", a plain
        // local timestamp, or "Z" (accepted by Qt's ISO parser as +00:00). K accepts all
        // three in one pattern (the literal "Z" variant relied on .NET's quirk of marking
        // Z-terminated input as Utc — verified equivalent, K just says what it means).
        if (DateTimeOffset.TryParseExact(
                str,
                ["yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            return time;
        }
        return null;
    }

    /// <summary>take a timestamp and convert it into an S3 timestamp</summary>
    public static string TimeToS3Time(DateTimeOffset time)
    {
        // this all because Qt can't format timestamps right.
        var offsetRaw = (int)time.Offset.TotalSeconds;
        var negative = offsetRaw < 0;
        var offsetAbs = Math.Abs(offsetRaw);

        var offsetSeconds = offsetAbs % 60;
        offsetAbs -= offsetSeconds;

        var offsetMinutes = offsetAbs % 3600;
        offsetAbs -= offsetMinutes;
        offsetMinutes /= 60;

        var offsetHours = offsetAbs / 3600;

        var raw = time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        raw += negative ? '-' : '+';
        raw += offsetHours.ToString("00", CultureInfo.InvariantCulture);
        raw += ":";
        raw += offsetMinutes.ToString("00", CultureInfo.InvariantCulture);
        return raw;
    }
}
