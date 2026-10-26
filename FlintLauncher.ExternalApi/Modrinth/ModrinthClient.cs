// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// The Modrinth v2 client: the navigation root of the typed endpoint operations — the groups
// (Versions / Projects / Tags / Search) return operation values bound to this instance, and
// as the executor it sends them, reading the live hosts and PAT from Options per send and
// wrapping transport, gate and decode failures into the Modrinth failure type. The wire
// models and the strict decoding live in ModrinthJson; file downloads land on disk through
// a checksum-verified temp-file dance. Everything transport-shaped — proxy, user agent,
// timeouts, retries, resilience — belongs to the injected HttpClient and its handler
// pipeline, not here.

using System.Security.Cryptography;
using System.Text.Json;

namespace FlintLauncher.ExternalApi;

public sealed class ModrinthClient : IApiExecutor
{
    private readonly HttpClient _http;

    public ModrinthClient(HttpClient http, ModrinthClientOptions? options = null)
    {
        _http = http;
        Options = options ?? new();
    }

    /// <summary>The live configuration (hosts, PAT) this client reads per request; shared
    /// with whoever constructed it (the Core adapter forwards its properties here).</summary>
    public ModrinthClientOptions Options { get; }

    // ------------------------------------------------------------------ endpoint groups

    /// <summary>The version endpoints: file-hash lookups, the batch update check and the
    /// version-list fetch.</summary>
    public ModrinthVersionQueries Versions => new(this);

    /// <summary>The project endpoints: single and batch fetches plus the description.</summary>
    public ModrinthProjectQueries Projects => new(this);

    /// <summary>The tag endpoints.</summary>
    public ModrinthTagQueries Tags => new(this);

    /// <summary>The search endpoint.</summary>
    public ModrinthSearchQueries Search => new(this);

    // ------------------------------------------------------------------ downloads

    /// <summary>Stream a download URL to disk: the body flows into a sibling temp file and
    /// the file is moved into place only after the checksum matched ("" or null disables the
    /// check), so a failed attempt never leaves a half-written artifact. Transport failures,
    /// status failures and checksum mismatches all throw; a caller walking a fallback URL
    /// list just catches and moves on. The download-meta header rides on the request when
    /// the meta carries content.</summary>
    public async Task DownloadFileAsync(string url, string destinationPath,
        ModrinthDownloadMeta? meta = null, (string Algorithm, string Hash)? checksum = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (meta is { } downloadMeta && !downloadMeta.IsEmpty)
        {
            request.Headers.TryAddWithoutValidation("modrinth-download-meta",
                JsonSerializer.Serialize(downloadMeta, ModrinthJsonContext.Default.ModrinthDownloadMeta));
        }

        using var response = await (this as IApiExecutor).SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var reader = new StreamReader(stream);
            var buffer = new char[ApiOperation.MaxErrorBodyLength + 1];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            var excerpt = new string(buffer, 0, Math.Min(read, ApiOperation.MaxErrorBodyLength));
            throw new HttpRequestException(
                $"HTTP status {(int)response.StatusCode} for {request.RequestUri}\n{excerpt}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var tempPath = destinationPath + ".download";
        try
        {
            await using (var output = File.Create(tempPath))
            {
                if (checksum is { } expected && AlgorithmName(expected.Algorithm) is { } algorithmName)
                {
                    using var hasher = IncrementalHash.CreateHash(algorithmName);
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    if (!string.Equals(actual, expected.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"checksum mismatch for {request.RequestUri}: expected {expected.Algorithm} {expected.Hash}, got {actual}");
                    }
                }
                else
                {
                    await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // the temp file best-effort cleanup must not mask the real failure
            }

            throw;
        }
    }

    // ------------------------------------------------------------------ executor face

    async Task<HttpResponseMessage> IApiExecutor.SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Options.ApiToken.Length != 0)
        {
            request.Headers.TryAddWithoutValidation("Authorization", Options.ApiToken);
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    Exception IApiExecutor.WrapFailure(Exception failure) => new ModrinthApiException(failure.Message, failure);

    private static HashAlgorithmName? AlgorithmName(string modrinthAlgorithm) => modrinthAlgorithm switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        "sha512" => HashAlgorithmName.SHA512,
        "md5" => HashAlgorithmName.MD5,
        _ => null,
    };
}
