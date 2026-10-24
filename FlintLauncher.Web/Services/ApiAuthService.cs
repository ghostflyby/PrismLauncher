// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Local-token authentication for the /api/v1 surface: the token lives for the PROCESS
// lifetime and never touches the disk (the pre-reform <dataDir>/api-token file is
// dead; the host owns credential provisioning). Resolution order: an explicit token from
// --api-token <t> / FLINT_API_TOKEN (host injection — the host is responsible for getting
// the token to its clients) → a freshly generated temporary token held in-process (the
// startup logs it once at Information). Disable entirely with --no-auth / FLINT_NO_AUTH=1
// (e.g. for a loopback-only deployment where another layer owns auth).

using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using FlintLauncher.Web.Models;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Web.Services;

public sealed class ApiAuthService
{
    public bool Enabled { get; }
    public string Token { get; }

    /// <summary>True when the effective token was generated for this process (the default):
    /// the host must surface it from the startup log line — it exists nowhere else.</summary>
    public bool TokenGenerated { get; }

    private ApiAuthService(bool enabled, string token, bool tokenGenerated)
    {
        Enabled = enabled;
        Token = token;
        TokenGenerated = tokenGenerated;
    }

    public static ApiAuthService Resolve(string[] args)
    {
        string? explicitToken = null;
        var disabled = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--api-token" && i + 1 < args.Length)
            {
                explicitToken = args[i + 1];
            }
            else if (args[i].StartsWith("--api-token=", StringComparison.Ordinal))
            {
                explicitToken = args[i]["--api-token=".Length..];
            }
            else if (args[i] == "--no-auth")
            {
                disabled = true;
            }
        }

        if (Environment.GetEnvironmentVariable("FLINT_API_TOKEN") is { Length: > 0 } envToken)
        {
            explicitToken = envToken;
        }

        if (Environment.GetEnvironmentVariable("FLINT_NO_AUTH") == "1")
        {
            disabled = true;
        }

        if (disabled)
        {
            return new ApiAuthService(false, "", tokenGenerated: false);
        }

        if (explicitToken is not null)
        {
            return new ApiAuthService(true, explicitToken, tokenGenerated: false);
        }

        // no file fallback, no write-back — a process-lifetime token, logged once at
        // startup (the host prints it; see WebHostBuilder), never persisted anywhere
        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new ApiAuthService(true, generated, tokenGenerated: true);
    }

    public bool Validate(string? candidate) =>
        !Enabled ||
        (candidate is not null &&
         string.Equals(candidate, Token, StringComparison.Ordinal));

    /// <summary>Cleanup: the pre-reform token file (&lt;dataDir&gt;/api-token) is dead —
    /// no compatibility read, and a leftover is deleted on startup (best effort; an unreadable
    /// leftover is inert since nothing reads it anymore).</summary>
    public const string LegacyTokenFileName = "api-token";

    public static void RemoveLegacyTokenFile(AbsolutePath dataDir)
    {
        try
        {
            (dataDir / LegacyTokenFileName).Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or SecurityException)
        {
            // the leftover stays; it is inert (nothing reads it anymore)
        }
    }
}

/// <summary>The /api/v1 gate: Bearer/X-Api-Token when the service is enabled. /healthz and
/// /api/v1/info stay open so clients can discover the host without credentials.</summary>
public static class ApiAuthMiddleware
{
    public static RouteGroupBuilder AddApiAuth(this RouteGroupBuilder api, ApiAuthService auth)
    {
        api.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            // /api/v1/info and the OpenAPI document stay open for client discovery (like
            // /healthz outside the group)
            var isDiscovery = request.Path == "/api/v1/info" || request.Path.StartsWithSegments("/openapi");
            if (auth.Enabled && !isDiscovery)
            {
                var candidate =
                    request.Headers["Authorization"].ToString() is { Length: > 0 } header
                    && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? header["Bearer ".Length..].Trim()
                        : request.Headers["X-Api-Token"].ToString() is { Length: > 0 } apiToken
                            ? apiToken
                            : null;
                if (!auth.Validate(candidate))
                {
                    var response = context.HttpContext.Response;
                    response.StatusCode = 401;
                    response.ContentType = "application/json";
                    await response.WriteAsync(
                        JsonSerializer.Serialize(
                            new ApiErrorEnvelope(
                                new ErrorResponse("unauthorized",
                                    "provide the API token as 'Authorization: Bearer <token>' or 'X-Api-Token'")),
                            FlintJsonContext.Default.ApiErrorEnvelope));
                    return Results.Empty;
                }
            }

            return await next(context);
        });
        return api;
    }
}
