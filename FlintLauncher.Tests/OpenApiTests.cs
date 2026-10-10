// SPDX-License-Identifier: GPL-3.0-only
// The OpenAPI document surface: unauthenticated availability (a discovery-plane artifact
// like /api/v1/info), coverage of the /api/v1 route table (every operation carries a 2xx
// response and, except the discovery endpoint, the 401 the auth filter produces), and the
// security scheme wiring.
using System.Text.Json;
using Xunit;

namespace FlintLauncher.Tests;

public class OpenApiTests
{
    [Fact]
    public async Task OpenApiDocument_IsServedWithoutCredentials_AndCoversTheApi()
    {
        await using var host = await FlintApiHost.StartAsync();
        var response = await host.CreateUnauthorizedClient().GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // the route table is covered (105 routes across ~90 distinct paths)
        Assert.True(root.GetProperty("paths").EnumerateObject().Count() >= 80);
        Assert.True(root.GetProperty("components").GetProperty("securitySchemes")
            .TryGetProperty("ApiToken", out _));
        Assert.True(root.GetProperty("components").GetProperty("securitySchemes")
            .TryGetProperty("Bearer", out _));

        var apiPaths = 0;
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            if (!path.Name.StartsWith("/api/v1/", StringComparison.Ordinal))
            {
                continue;
            }
            apiPaths++;
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var responses = operation.Value.GetProperty("responses");
                Assert.True(responses.EnumerateObject().Any(r => r.Name.StartsWith('2')),
                    $"{operation.Name.ToUpperInvariant()} {path.Name} lacks a 2xx response");
                if (path.Name != "/api/v1/info")
                {
                    Assert.True(responses.TryGetProperty("401", out _),
                        $"{operation.Name.ToUpperInvariant()} {path.Name} lacks the 401 the auth filter produces");
                }
            }
        }
        Assert.True(apiPaths >= 75, $"only {apiPaths} /api/v1 paths in the document");
    }

    [Fact]
    public async Task Healthz_And_Info_StayOpen()
    {
        await using var host = await FlintApiHost.StartAsync();
        var client = host.CreateUnauthorizedClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/v1/info")).StatusCode);
    }
}
