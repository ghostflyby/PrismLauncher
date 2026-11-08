// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Ftb;

/// <summary>The per-API failure: wraps the inner exception (transport, gate or decode) with
/// the FTB failure message. Cancellation is never wrapped.</summary>
public sealed class FtbApiException(string message, Exception innerException)
    : Exception(message, innerException);
