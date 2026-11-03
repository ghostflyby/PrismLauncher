// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.Core.Launch;

/// <summary>The launch pipeline's host-level feature toggles, handed to the
/// <see cref="LaunchFactory"/> constructor. Unset members keep the bare-fixture default
/// (the gate off, the build-default resource base).</summary>
/// <param name="XmlLogParsing">Parse log4j XML events out of the game output
/// (LaunchTask::parseXmlLogs; the C++ had no switch — the host enables it).</param>
/// <param name="ResourceUrlOverride">C++ settings "ResourceURLOverride" (AssetUpdateTask
/// asset-object base URL). Null keeps the build default base.</param>
public sealed record LaunchOptions(bool XmlLogParsing = false, string? ResourceUrlOverride = null);
