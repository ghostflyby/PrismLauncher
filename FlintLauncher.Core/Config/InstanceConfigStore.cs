// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Text.Json.Serialization.Metadata;
using TruePath;

namespace FlintLauncher.Core.Config;

public sealed class InstanceConfigStore
{
    private readonly Lock _lock = new();
    private readonly TypedJsonFile<InstanceConfigFile> _file;

    public InstanceConfigStore(AbsolutePath instanceRoot, JsonTypeInfo<InstanceConfigFile> typeInfo)
    {
        ConfigPath = instanceRoot / "instance.json";
        _file = new TypedJsonFile<InstanceConfigFile>(ConfigPath, typeInfo);

        // raw payload — nulls are the layer
        Current = _file.LoadOrDefault(InstanceConfigFile.Default);
    }

    /// <summary>The instance.json path (the instance layer's only home and write target).</summary>
    public AbsolutePath ConfigPath { get; }

    /// <summary>The raw instance-layer payload (absent members = inherit). Treat as
    /// immutable — mutate through <see cref="Update"/>.</summary>
    public InstanceConfigFile Current { get; private set; }

    /// <summary>Apply <paramref name="mutate"/> to the payload and persist immediately.</summary>
    public void Update(Func<InstanceConfigFile, InstanceConfigFile> mutate)
    {
        lock (_lock)
        {
            Current = mutate(Current);
            _file.Save(Current);
        }
    }
}
