// SPDX-License-Identifier: GPL-3.0-only
// Port of the QProcessEnvironment subset the instance layer uses (insert/value/contains/
// isEmpty): an ordered string-to-string variable map handed to the launch pipeline. The
// actual process spawning that consumes it is phase P5.

namespace FlintLauncher.Core.Instances;

/// <summary>Minimal port of QProcessEnvironment for the instance environment builders.</summary>
public sealed class ProcessEnvironment
{
    private readonly SortedDictionary<string, string> _vars = new(StringComparer.Ordinal);

    /// <summary>QProcessEnvironment::insert (an empty value keeps the variable defined-empty, like Qt).</summary>
    public void Insert(string key, string value) => _vars[key] = value;

    /// <summary>QProcessEnvironment::value (missing keys yield an empty string).</summary>
    public string Value(string key) => _vars.GetValueOrDefault(key, "");

    public bool Contains(string key) => _vars.ContainsKey(key);

    public bool IsEmpty => _vars.Count == 0;

    public int Count => _vars.Count;

    /// <summary>The variables in key order (QMap-like iteration).</summary>
    public IReadOnlyDictionary<string, string> Vars => _vars;
}
