// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json.Serialization;
using Flint.Web.Models;

namespace Flint.Web;

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class FlintJsonContext : JsonSerializerContext;
