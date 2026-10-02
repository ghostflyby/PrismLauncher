// SPDX-License-Identifier: GPL-3.0-only
// The instance-layer environment (D4): everything an instance's typed settings stack needs —
// the user layer's live store (flint.json) plus the source-generated type infos for
// instance.json / instance-state.json (GLOBAL §1: Core only sees the opaque JsonTypeInfo
// handles; the registration list stays in the host's FlintJsonContext — AOT red line).
//
// Threaded from the composition root through InstanceList to every discovered instance, and
// through the InstanceStaging wrapper to the creation/copy/import tasks that build instances
// inside a staging folder.
using System.Text.Json.Serialization.Metadata;

namespace FlintLauncher.Core.Config;

public sealed record InstanceEnvironment(
    FlintConfigStore? UserConfig,
    JsonTypeInfo<InstanceConfigFile> ConfigInfo,
    JsonTypeInfo<InstanceStateFile> StateInfo);
