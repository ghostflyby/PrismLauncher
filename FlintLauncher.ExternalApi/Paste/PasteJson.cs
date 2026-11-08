// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using System.Text.Json.Serialization;

namespace FlintLauncher.ExternalApi.Paste;

/// <summary>The paste.gg create-paste request body (a third-party protocol shape).</summary>
internal sealed record PasteGgBody(string Expires, PasteGgFile[] Files);

internal sealed record PasteGgFile(string Name, PasteGgContent Content);

internal sealed record PasteGgContent(string Format, string Value);

/// <summary>The hastebin reply: the created document's key.</summary>
public sealed record HastebinReply(
    string? Key = null);

/// <summary>The mclo.gs reply: the outcome flag, the created link and the refusal text; a
/// missing outcome member makes the reply malformed.</summary>
public sealed record MclogsReply(
    bool? Success = null,
    string? Url = null,
    string? Error = null);

/// <summary>The paste.gg reply: the status word and, per outcome, the created document or
/// the refusal.</summary>
public sealed record PasteGgReply(
    string? Status = null,
    PasteGgResultReply? Result = null,
    string? Error = null,
    string? Message = null);

public sealed record PasteGgResultReply(
    string? Id = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PasteGgBody))]
[JsonSerializable(typeof(HastebinReply))]
[JsonSerializable(typeof(MclogsReply))]
[JsonSerializable(typeof(PasteGgReply))]
internal sealed partial class PasteGgJsonContext : JsonSerializerContext;
