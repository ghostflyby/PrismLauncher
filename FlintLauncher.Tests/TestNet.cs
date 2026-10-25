// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Net;

namespace FlintLauncher.Tests;

/// <summary>The one client pair the fixtures share where a test only needs an HttpClient to
/// reach its local stub (no proxy or user-agent assertions). A test that asserts on settings
/// or transport behavior constructs its own pair instead.</summary>
internal static class TestNet
{
    public static readonly NetSettings Settings = NetSettings.CreateDefault();

    public static readonly HttpClient Http = NetPrimaryHandler.CreateClient(Settings);
}
