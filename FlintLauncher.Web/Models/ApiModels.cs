// SPDX-License-Identifier: GPL-3.0-only
namespace FlintLauncher.Web.Models;

public sealed record HealthResponse(string Status);

public sealed record InfoResponse(
    string Name,
    string Version,
    string UserAgent,
    string MetaUrl,
    bool JavaDownloaderEnabled);

public sealed record ErrorResponse(string Code, string Message, string? Detail = null);
