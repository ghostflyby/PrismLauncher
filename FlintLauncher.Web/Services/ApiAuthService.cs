// SPDX-License-Identifier: GPL-3.0-only
// Local-token authentication for the /api/v1 surface: the host generates a random token on
// first run and stores it next to the data dir (0600 on unix); clients send it as
// `Authorization: Bearer <token>` or `X-Api-Token: <token>`. Override with --api-token <t> /
// FLINT_API_TOKEN; disable entirely with --no-auth / FLINT_NO_AUTH=1 (e.g. for a loopback-only
// deployment where another layer owns auth).
using System.Security.Cryptography;

namespace FlintLauncher.Web.Services;

public sealed class ApiAuthService
{
    public const string TokenFileName = "api-token";

    public bool Enabled { get; }
    public string Token { get; }

    private ApiAuthService(bool enabled, string token)
    {
        Enabled = enabled;
        Token = token;
    }

    public static ApiAuthService Resolve(string dataDir, string[] args)
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
            return new ApiAuthService(false, "");
        }

        var tokenFile = Path.Combine(dataDir, TokenFileName);
        if (explicitToken is not null)
        {
            // keep the file in sync so clients can always read the effective token
            WriteTokenFile(tokenFile, explicitToken);
            return new ApiAuthService(true, explicitToken);
        }

        if (File.Exists(tokenFile))
        {
            var stored = File.ReadAllText(tokenFile).Trim();
            if (stored.Length > 0)
            {
                return new ApiAuthService(true, stored);
            }
        }

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        WriteTokenFile(tokenFile, generated);
        return new ApiAuthService(true, generated);
    }

    public bool Validate(string? candidate) =>
        !Enabled ||
        (candidate is not null &&
         string.Equals(candidate, Token, StringComparison.Ordinal));

    private static void WriteTokenFile(string path, string token)
    {
        File.WriteAllText(path, token + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
                // best effort: the file lives inside the (0700-recommended) data dir anyway
            }
        }
    }
}

/// <summary>The /api/v1 gate: Bearer/X-Api-Token when the service is enabled. /healthz and
/// /api/v1/info stay open so clients can discover the host without credentials.</summary>
public static class ApiAuthMiddleware
{
    public static RouteGroupBuilder AddApiAuth(this RouteGroupBuilder api, ApiAuthService auth)
    {
        api.AddEndpointFilter(async (EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        {
            var request = context.HttpContext.Request;
            // /api/v1/info stays open for client discovery (like /healthz outside the group)
            var isDiscovery = request.Path == "/api/v1/info";
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
                        System.Text.Json.JsonSerializer.Serialize(
                            new Models.ApiErrorEnvelope(
                                new Models.ErrorResponse("unauthorized",
                                    "provide the data-dir token as 'Authorization: Bearer <token>' or 'X-Api-Token'")),
                            FlintJsonContext.Default.ApiErrorEnvelope));
                    return Results.Empty;
                }
            }
            return await next(context);
        });
        return api;
    }
}
