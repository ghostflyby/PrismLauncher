// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using FlintLauncher.Core.Net;

namespace FlintLauncher.Tests;

/// <summary>The one network stack the fixtures share where a test only needs an HttpClient
/// to reach its local stub (no proxy or user-agent assertions). A test that asserts on a
/// stack's configuration constructs its own stack instead.</summary>
internal static class TestNet
{
    public static NetStack Stack { get; } = new();
}
