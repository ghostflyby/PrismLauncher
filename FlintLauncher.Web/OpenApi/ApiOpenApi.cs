// SPDX-License-Identifier: GPL-3.0-only
// OpenAPI document shaping for the /api/v1 surface: security schemes, the blanket error
// responses every endpoint can produce through ApiJson, and small endpoint markers for the
// handful of shapes Minimal API cannot infer (SSE streams, plain-text bodies, multipart
// form uploads).

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FlintLauncher.Web.OpenApi;

/// <summary>Marks the SSE endpoints (GET /tasks/{id}/stream): 200 is a text/event-stream
/// with no fixed schema.</summary>
internal sealed record SseMarker;

/// <summary>Marks the plain-text endpoints (GET /instances/{id}/logs/{dir}/{name}):
/// 200 is text/plain carrying a string.</summary>
internal sealed record PlainTextMarker;

/// <summary>Marks the multipart/form endpoints whose request bodies Minimal API cannot
/// infer (they read HttpRequest.Form directly). <paramref name="Description"/> names the
/// form fields.</summary>
internal sealed record FormUploadMarker(string Description);

internal static class ApiOpenApi
{
    /// <summary>The error shape every /api/v1 endpoint can return (ApiJson helpers + the
    /// auth filter), inlined by hand — the envelope never appears as endpoint response
    /// metadata, so the generator would not emit a schema for it.</summary>
    internal static OpenApiSchema ErrorEnvelopeSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["error"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                    ["message"] = new OpenApiSchema { Type = JsonSchemaType.String },
                },
            },
        },
    };

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Flint API",
                Version = "v1",
                Description = "PrismLauncher's headless launcher core (G3: API v1, in-place).",
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["ApiToken"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Api-Token",
                Description = "The token from the data-dir api-token handshake (--api-token / generated per process).",
            };
            document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                Description = "The same token as 'Authorization: Bearer <token>'.",
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var relative = context.Description.RelativePath ?? string.Empty;
            var authenticated = relative != "healthz" && relative != "api/v1/info";

            operation.Responses ??= new OpenApiResponses();
            if (authenticated)
            {
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("ApiToken")] = [],
                    },
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("Bearer")] = [],
                    },
                ];
                operation.Responses.TryAdd("401", ErrorResponse("unauthorized"));
            }

            operation.Responses.TryAdd("400", ErrorResponse("invalid_request"));
            operation.Responses.TryAdd("404", ErrorResponse("not_found"));
            operation.Responses.TryAdd("409", ErrorResponse("conflict"));

            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<SseMarker>().Any())
            {
                operation.Responses["200"] = new OpenApiResponse
                {
                    Description = "Server-sent events: 'data:' frames carry LogLineDto JSON, a final 'event: state' frame carries TaskView.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["text/event-stream"] = new(),
                    },
                };
            }

            if (metadata.OfType<PlainTextMarker>().Any())
            {
                operation.Responses["200"] = new OpenApiResponse
                {
                    Description = "The log file tail (last 512 KiB).",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["text/plain"] = new()
                        {
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                        },
                    },
                };
            }

            if (metadata.OfType<FormUploadMarker>().FirstOrDefault() is { } form)
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = form.Description,
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["multipart/form-data"] = new()
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Object,
                                AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.String },
                            },
                        },
                    },
                };
            }

            return Task.CompletedTask;
        });
    }

    private static OpenApiResponse ErrorResponse(string code) => new()
    {
        Description = code,
        Content = new Dictionary<string, OpenApiMediaType>
        {
            ["application/json"] = new() { Schema = ErrorEnvelopeSchema() },
        },
    };
}
