// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

namespace FlintLauncher.ExternalApi.Imgur;

/// <summary>The per-API failure: wraps the inner exception (transport, gate or reply) with
/// the imgur failure message. Cancellation is never wrapped.</summary>
public sealed class ImgurApiException(string message, Exception innerException)
    : Exception(message, innerException);
