// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Modrinth;

/// <summary>The per-API failure: wraps the inner exception (transport, gate or decode) with
/// the Modrinth failure message. Cancellation is never wrapped.</summary>
public sealed class ModrinthApiException(string message, Exception innerException)
    : Exception(message, innerException);
