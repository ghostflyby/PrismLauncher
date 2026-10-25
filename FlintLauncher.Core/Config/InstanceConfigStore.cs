// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The instance.json store: one TypedJsonFile per instance directory holding the
// RAW instance layer — no fold at load time, because a member being null IS the layering
// signal; the user layer joins at read time through InstanceConfigKeys.
//
// The dual-track format determination: an instance directory that carries instance.json
// IS Flint format. Otherwise the
// instance.cfg is READ-ONLY compat — the importer produces the equivalent payload in memory
// and the FIRST WRITE materializes it as instance.json. The cfg file itself is
// never deleted or rewritten; after the conversion instance.json exists and wins every
// subsequent read, so a stale cfg can never resurrect old values. The conversion carries the
// cfg's uuid over verbatim (Identity.Uuid), keeping the addressing chain stable.
//
// Immediate atomic write on every mutation (the C++ INI settings object saved per Set; the
// instance settings surface relies on that).

using System.Text.Json.Serialization.Metadata;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Config;

public sealed class InstanceConfigStore
{
    private readonly Lock _lock = new();
    private readonly TypedJsonFile<InstanceConfigFile> _file;

    public InstanceConfigStore(AbsolutePath instanceRoot, JsonTypeInfo<InstanceConfigFile> typeInfo)
    {
        ConfigPath = instanceRoot / "instance.json";
        _file = new TypedJsonFile<InstanceConfigFile>(ConfigPath, typeInfo);

        // the format marker is the FILE'S EXISTENCE: instance.json present ->
        // Flint format; else read-only instance.cfg (imported in memory, converted on write)
        IsFlintFormat = ConfigPath.ExistsFile();
        Current = IsFlintFormat
            ? _file.LoadOrDefault(InstanceConfigFile.Default) // raw payload — nulls are the layer
            : InstanceIniImporter.Import(instanceRoot / InstanceIniImporter.LegacyFileName);
    }

    /// <summary>The instance.json path (the format marker and the only write target).</summary>
    public AbsolutePath ConfigPath { get; }

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
