// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Technic;

/// <summary>The per-API failure: wraps the inner exception (transport, gate or decode) with
/// the Technic or Solder failure message. Cancellation is never wrapped.</summary>
public sealed class TechnicApiException(string message, Exception innerException)
    : Exception(message, innerException);
