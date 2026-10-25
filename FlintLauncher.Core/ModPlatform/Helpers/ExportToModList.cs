// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2023 Trial97 <alexandru.tripon97@gmail.com>
//
// Port of modplatform/helpers/ExportToModList.{h,cpp}: render a mod list as HTML,
// Markdown, plain text, JSON, CSV or through a custom line template. The C++ walks Mod*
// objects; here the per-mod data (name/version/authors/url/filename/mod id plus the
// optional .pw.toml metadata version) is carried by the ModListMod record so the export is
// pure functions over a mod list model.
//
// The exact templates of the C++ are kept, including Qt specifics: toHtmlEscaped (only
// & < > ") and the CSV quoting rule for multi-author entries. The JSON export is the typed
// ModListEntryJson wire (docs/JSON-MIGRATION.md, the Qt write-tail batch): the member set
// and values are the shared-file contract, the Qt byte shape (sorted keys, indentation) is
// not — the source-generated context writes standard indented JSON.

using System.Text.Json;
using System.Text.Json.Serialization;
using FlintLauncher.Core.Minecraft.Resources;

namespace FlintLauncher.Core.ModPlatform.Helpers;

/// <summary>The per-mod JSON export entry (ExportToModList::toJSON): members appear only
/// when their OptionalData flag is on AND the value is non-empty, like the C++ conditions.</summary>
internal sealed record ModListEntryJson
{
    public string Name { get; init; } = "";

    public string? Url { get; init; }

    public string? Version { get; init; }

    public string[]? Authors { get; init; }

    public string? Filename { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ModListEntryJson>))]
internal sealed partial class ModListJsonContext : JsonSerializerContext;

/// <summary>Port of the Formats enum (CUSTOM is served by the line-template overload).</summary>
public enum ModListFormat : byte
{
    Html,
    Markdown,
    Plaintxt,
    Json,
    Csv,
    Custom,
}

/// <summary>Port of the OptionalData flags.</summary>
[Flags]
public enum OptionalData : byte
{
    None = 0,
    Authors = 1 << 0,
    Url = 1 << 1,
    Version = 1 << 2,
    FileName = 1 << 3,
}

/// <summary>The per-mod data exportToModList consumes (Mod + its Packwiz metadata).</summary>
public sealed class ModListMod
{
    public string Name { get; init; } = "";
    public string ModId { get; init; } = "";

    /// <summary>Mod::homepage().</summary>
    public string Url { get; init; } = "";

    /// <summary>Mod::version() (the resolved details version).</summary>
    public string Version { get; init; } = "";

    /// <summary>metadata()-&gt;version().toString(): the .pw.toml version, used when the
    /// details version is empty.</summary>
    public string MetadataVersion { get; init; } = "";

    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>fileinfo().fileName().</summary>
    public string FileName { get; init; } = "";

    /// <summary>Builds the record from a Mod (without the metadata layer the C#
    /// Mod carries no .pw.toml data; pass it through <see cref="MetadataVersion"/>).</summary>
    public static ModListMod FromMod(Mod mod, string metadataVersion = "")
    {
        return new ModListMod
        {
            Name = mod.Name,
            ModId = mod.ModId(),
            Url = mod.Homepage(),
            Version = mod.Version,
            MetadataVersion = metadataVersion,
            Authors = mod.Authors(),
            FileName = mod.Fileinfo.Name,
        };
    }

    /// <summary>Port of the "ver" fallback of the C++: the details version, else the
    /// metadata version.</summary>
    public string EffectiveVersion => Version.Length != 0 ? Version : MetadataVersion;
}

public static class ExportToModList
{
    /// <summary>Port of QString::toHtmlEscaped (escapes only &amp; &lt; &gt; and ").</summary>
    public static string ToHtmlEscaped(string src)
    {
        return src.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    /// <summary>Port of toMarkdownEscaped: backslash-escape the specials (the C++ escapes
    /// the backslash first; the escapes introduced afterwards are not re-escaped).</summary>
    public static string ToMarkdownEscaped(string src)
    {
        foreach (var ch in "\\`*_{}[]<>()#+-.!|")
        {
            src = src.Replace(ch.ToString(), "\\" + ch);
        }

        return src;
    }

    /// <summary>Port of ExportToModList::toHTML.</summary>
    public static string ToHtml(IReadOnlyList<ModListMod> mods, OptionalData extraData)
    {
        var lines = new List<string>();
        foreach (var mod in mods)
        {
            var modName = ToHtmlEscaped(mod.Name);
            if ((extraData & OptionalData.Url) != 0)
            {
                var url = ToHtmlEscaped(mod.Url);
                if (url.Length != 0)
                {
                    modName = $"<a href=\"{url}\">{modName}</a>";
                }
            }

            var line = modName;
            if ((extraData & OptionalData.Version) != 0)
            {
                var ver = mod.EffectiveVersion;
                if (ver.Length != 0)
                {
                    line += $" [{ToHtmlEscaped(ver)}]";
                }
            }

            if ((extraData & OptionalData.Authors) != 0 && mod.Authors.Count != 0)
            {
                line += " by " + ToHtmlEscaped(string.Join(", ", mod.Authors));
            }

            if ((extraData & OptionalData.FileName) != 0)
            {
                line += $" ({ToHtmlEscaped(mod.FileName)})";
            }

            lines.Add($"<li>{line}</li>");
        }

        return $"<html><body><ul>\n\t{string.Join("\n\t", lines)}\n</ul></body></html>";
    }

    /// <summary>Port of ExportToModList::toMarkdown.</summary>
    public static string ToMarkdown(IReadOnlyList<ModListMod> mods, OptionalData extraData)
    {
        var lines = new List<string>();

        foreach (var mod in mods)
        {
            var modName = ToMarkdownEscaped(mod.Name);
            if ((extraData & OptionalData.Url) != 0)
            {
                var url = mod.Url;
                if (url.Length != 0)
                {
                    modName = $"[{modName}]({url})";
                }
            }

            var line = modName;
            if ((extraData & OptionalData.Version) != 0)
            {
                var ver = ToMarkdownEscaped(mod.EffectiveVersion);
                if (ver.Length != 0)
                {
                    line += $" [{ver}]";
                }
            }

            if ((extraData & OptionalData.Authors) != 0 && mod.Authors.Count != 0)
            {
                line += " by " + ToMarkdownEscaped(string.Join(", ", mod.Authors));
            }

            if ((extraData & OptionalData.FileName) != 0)
            {
                line += $" ({ToMarkdownEscaped(mod.FileName)})";
            }

            lines.Add("- " + line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>Port of ExportToModList::toPlainTXT.</summary>
    public static string ToPlainTxt(IReadOnlyList<ModListMod> mods, OptionalData extraData)
    {
        var lines = new List<string>();
        foreach (var mod in mods)
        {
            var line = mod.Name;
            if ((extraData & OptionalData.Url) != 0)
            {
                var url = mod.Url;
                if (url.Length != 0)
                {
                    line += $" ({url})";
                }
            }

            if ((extraData & OptionalData.Version) != 0)
            {
                var ver = mod.EffectiveVersion;
                if (ver.Length != 0)
                {
                    line += $" [{ver}]";
                }
            }

            if ((extraData & OptionalData.Authors) != 0 && mod.Authors.Count != 0)
            {
                line += " by " + string.Join(", ", mod.Authors);
            }

            if ((extraData & OptionalData.FileName) != 0)
            {
                line += $" ({mod.FileName})";
            }

            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>Port of ExportToModList::toJSON: the per-mod typed entries serialized as an
    /// indented array with the trailing newline.</summary>
    public static string ToJson(IReadOnlyList<ModListMod> mods, OptionalData extraData)
    {
        var entries = new List<ModListEntryJson>();
        foreach (var mod in mods)
        {
            var entry = new ModListEntryJson { Name = mod.Name };
            if ((extraData & OptionalData.Url) != 0 && mod.Url.Length != 0)
            {
                entry = entry with { Url = mod.Url };
            }

            if ((extraData & OptionalData.Version) != 0 && mod.EffectiveVersion.Length != 0)
            {
                entry = entry with { Version = mod.EffectiveVersion };
            }

            if ((extraData & OptionalData.Authors) != 0 && mod.Authors.Count != 0)
            {
                entry = entry with { Authors = [.. mod.Authors] };
            }

            if ((extraData & OptionalData.FileName) != 0)
            {
                entry = entry with { Filename = mod.FileName };
            }

            entries.Add(entry);
        }

        return JsonSerializer.Serialize(entries, ModListJsonContext.Default.ListModListEntryJson) + "\n";
    }

    /// <summary>Port of ExportToModList::toCSV.</summary>
    public static string ToCsv(IReadOnlyList<ModListMod> mods, OptionalData extraData)
    {
        var lines = new List<string>();
        foreach (var mod in mods)
        {
            var data = new List<string> { mod.Name };
            if ((extraData & OptionalData.Url) != 0)
            {
                data.Add(mod.Url);
            }

            if ((extraData & OptionalData.Version) != 0)
            {
                data.Add(mod.EffectiveVersion);
            }

            if ((extraData & OptionalData.Authors) != 0)
            {
                string authors;
                if (mod.Authors.Count == 1)
                {
                    authors = mod.Authors[^1];
                }
                else if (mod.Authors.Count > 1)
                {
                    authors = $"\"{string.Join(",", mod.Authors)}\"";
                }
                else
                {
                    authors = "";
                }

                data.Add(authors);
            }

            if ((extraData & OptionalData.FileName) != 0)
            {
                data.Add(mod.FileName);
            }

            lines.Add(string.Join(",", data));
        }

        return string.Join("\n", lines);
    }

    /// <summary>Port of exportToModList(mods, format, extraData).</summary>
    public static string ExportModsToString(IReadOnlyList<ModListMod> mods, ModListFormat format,
        OptionalData extraData)
    {
        return format switch
        {
            ModListFormat.Html => ToHtml(mods, extraData),
            ModListFormat.Markdown => ToMarkdown(mods, extraData),
            ModListFormat.Plaintxt => ToPlainTxt(mods, extraData),
            ModListFormat.Json => ToJson(mods, extraData),
            ModListFormat.Csv => ToCsv(mods, extraData),
            _ => $"unknown format:{(byte)format}",
        };
    }

    /// <summary>Port of exportToModList(mods, lineTemplate): the {name}/{mod_id}/{url}/
    /// {version}/{authors}/{filename} placeholders of the custom template.</summary>
    public static string ExportModsToString(IReadOnlyList<ModListMod> mods, string lineTemplate)
    {
        var lines = new List<string>();
        foreach (var mod in mods)
        {
            lines.Add(lineTemplate
                .Replace("{name}", mod.Name)
                .Replace("{mod_id}", mod.ModId)
                .Replace("{url}", mod.Url)
                .Replace("{version}", mod.EffectiveVersion)
                .Replace("{authors}", string.Join(", ", mod.Authors))
                .Replace("{filename}", mod.FileName));
        }

        return string.Join("\n", lines);
    }
}
