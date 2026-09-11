// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Serialization;
using Flint.Core;
using Flint.Web;
using Flint.Web.Models;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, FlintJsonContext.Default);
});

builder.Services.AddSingleton(FlintConfig.Default);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok")));

var api = app.MapGroup("/api/v1");
api.MapGet("/info", (FlintConfig config) => new InfoResponse(
    config.LauncherDisplayName,
    config.PrintableVersionString(),
    config.UserAgent,
    config.MetaUrl,
    config.JavaDownloaderEnabled));

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
