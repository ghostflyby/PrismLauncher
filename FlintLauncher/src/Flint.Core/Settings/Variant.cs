// SPDX-License-Identifier: GPL-3.0-only
// Port of the QVariant-ish conversions Prism performs on values coming out of INI files
// (the Qt6 INI reader returns strings for scalars, so typed accessors parse them the way
// QVariant::toBool/toInt would).
namespace Flint.Core.Settings;

public static class Variant
{
    public static string AsString(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        IReadOnlyList<string> list => string.Join(", ", list),
        _ => value.ToString() ?? "",
    };

    public static bool AsBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s == "true" || s == "1",
        int i => i != 0,
        long l => l != 0,
        _ => false,
    };

    public static int AsInt(object? value) => value switch
    {
        null => 0,
        int i => i,
        long l => checked((int)l),
        bool b => b ? 1 : 0,
        string s => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : 0,
        double d => (int)d,
        _ => 0,
    };

    public static long AsLong(object? value) => value switch
    {
        null => 0,
        long l => l,
        int i => i,
        bool b => b ? 1 : 0,
        string s => long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : 0,
        double d => (long)d,
        _ => 0,
    };

    public static double AsDouble(object? value) => value switch
    {
        null => 0,
        double d => d,
        int i => i,
        long l => l,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
        _ => 0,
    };

    public static IReadOnlyList<string> AsStringList(object? value) => value switch
    {
        null => [],
        IReadOnlyList<string> list => list,
        IEnumerable<string> en => en.ToList(),
        string s => [s],
        _ => [],
    };

    // ---- IniValue conversion ---------------------------------------------------

    public static object? FromIniValue(IniValue value) => value.ValueKind switch
    {
        IniValue.Kind.String => value.String,
        IniValue.Kind.StringList => value.List,
        IniValue.Kind.ByteArray => value.Bytes,
        IniValue.Kind.Raw => value.String,
        _ => null,
    };

    public static IniValue ToIniValue(object? value) => value switch
    {
        null => IniValue.Invalid,
        IniValue iv => iv,
        string s => IniValue.From(s),
        bool b => IniValue.From(b ? "true" : "false"),
        int i => IniValue.From(i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        long l => IniValue.From(l.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        double d => IniValue.From(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        IReadOnlyList<string> list => list.Count == 0 ? IniValue.Invalid : IniValue.From(list.ToList()),
        IEnumerable<string> en => ToIniValue(en.ToList()),
        byte[] bytes => IniValue.From(bytes),
        _ => IniValue.From(value.ToString() ?? ""),
    };
}
