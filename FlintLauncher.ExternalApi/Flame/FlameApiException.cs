// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Flame;

/// <summary>The per-API failure: wraps the inner exception (transport, gate or decode) with
/// the CurseForge failure message. Cancellation is never wrapped.</summary>
public sealed class FlameApiException(string message, Exception innerException)
    : Exception(message, innerException);
