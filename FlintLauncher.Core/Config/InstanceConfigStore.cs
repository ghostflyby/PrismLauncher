// SPDX-License-Identifier: GPL-3.0-only
// The instance.json store (DATA D4): one TypedJsonFile per instance directory holding the
// RAW instance layer — no fold at load time, because a member being null IS the layering
// signal (C6/C7); the user layer joins at read time through InstanceConfigKeys.
//
// The dual-track format determination (the CONFIG.md open point this batch closes, ask
// item 3): an instance directory that carries instance.json IS Flint format. Otherwise the
// instance.cfg is READ-ONLY compat — the importer produces the equivalent payload in memory
// and the FIRST WRITE materializes it as instance.json ("写时转换"). The cfg file itself is
// never deleted or rewritten; after the conversion instance.json exists and wins every
// subsequent read, so a stale cfg can never resurrect old values. The conversion carries the
// cfg's uuid over verbatim (Identity.Uuid), keeping the D2 addressing chain stable.
//
// Immediate atomic write on every mutation (the C++ INI settings object saved per Set; the
// instance settings surface relies on that).

using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.Config;

public sealed class InstanceConfigStore
{
    private readonly Lock _lock = new();
    private readonly TypedJsonFile<InstanceConfigFile> _file;

    public InstanceConfigStore(string instanceRoot, JsonTypeInfo<InstanceConfigFile> typeInfo)
    {
        InstanceRoot = instanceRoot;
        ConfigPath = Path.Combine(instanceRoot, "instance.json");
        _file = new TypedJsonFile<InstanceConfigFile>(ConfigPath, typeInfo);

        // the format marker is the FILE'S EXISTENCE (ask item 3): instance.json present ->
        // Flint format; else read-only instance.cfg (imported in memory, converted on write)
        IsFlintFormat = File.Exists(ConfigPath);
        Current = IsFlintFormat
            ? _file.LoadOrDefault(InstanceConfigFile.Default) // raw payload — nulls are the layer
            : InstanceIniImporter.Import(Path.Combine(instanceRoot, InstanceIniImporter.LegacyFileName));
    }

    /// <summary>The instance directory this store anchors.</summary>
    public string InstanceRoot { get; }

    /// <summary>The instance.json path (the format marker and the only write target).</summary>
    public string ConfigPath { get; }

    /// <summary>True once the instance layer is JSON-backed. False means the initial payload
    /// came from the read-only instance.cfg import and the next write converts.</summary>
    public bool IsFlintFormat { get; private set; }

    /// <summary>The raw instance-layer payload (absent members = inherit). Treat as
    /// immutable — mutate through <see cref="Update"/>.</summary>
    public InstanceConfigFile Current { get; private set; }

    /// <summary>Apply <paramref name="mutate"/> to the payload and persist immediately. The
    /// first call on a Prism-format instance performs the write-time conversion: the imported
    /// payload becomes instance.json on disk.</summary>
    public void Update(Func<InstanceConfigFile, InstanceConfigFile> mutate)
    {
        lock (_lock)
        {
            Current = mutate(Current);
            _file.Save(Current);
            IsFlintFormat = true;
        }
    }
}
