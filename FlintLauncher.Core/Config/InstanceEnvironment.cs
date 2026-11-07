// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The instance-layer environment: everything an instance's typed settings stack needs —
// the user layer's live read face (the flint.json store's Options monitor) plus the
// source-generated type infos for instance.json / instance-state.json (Core only sees the
// opaque JsonTypeInfo handles; the registration list stays in the host's FlintJsonContext —
// AOT red line).
//
// Threaded from the composition root through InstanceList to every discovered instance, and
// through the InstanceStaging wrapper to the creation/copy/import tasks that build instances
// inside a staging folder.

using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;

namespace FlintLauncher.Core.Config;

public sealed record InstanceEnvironment(
    IOptionsMonitor<FlintConfigFile>? UserConfig,
    JsonTypeInfo<InstanceConfigFile> ConfigInfo,
    JsonTypeInfo<InstanceStateFile> StateInfo);
