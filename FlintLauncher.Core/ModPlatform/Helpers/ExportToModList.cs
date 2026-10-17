// SPDX-License-Identifier: GPL-3.0-only
// Port of modplatform/helpers/ExportToModList.{h,cpp}: render a mod list as HTML,
// Markdown, plain text, JSON, CSV or through a custom line template. The C++ walks Mod*
// objects; here the per-mod data (name/version/authors/url/filename/mod id plus the
// optional .pw.toml metadata version) is carried by the ModListMod record so the export is
// pure functions over a mod list model.
//
// The exact templates of the C++ are kept, including Qt specifics: toHtmlEscaped (only
// & < > "), the QJsonDocument::toJson() indented shape (sorted keys, 4-space indent,
// trailing newline) and the CSV quoting rule for multi-author entries.

using System.Text.Json.Nodes;
using FlintLauncher.Core.Minecraft.Resources;

namespace FlintLauncher.Core.ModPlatform.Helpers;

/// <summary>Port of the Formats enum (CUSTOM is served by the line-template overload).</summary>
public enum ModListFormat : byte
{
    HTML,
    MARKDOWN,
    PLAINTXT,
    JSON,
    CSV,
    CUSTOM,
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

    /// <summary>Port of ExportToModList::toJSON: a QJsonDocument over the array of per-mod
    /// objects written with toJson() (sorted keys, 4-space indent, trailing newline).</summary>
    public static string ToJson(IReadOnlyList<ModListMod> mods, OptionalData extraData)
    {
        var lines = new JsonArray();
        foreach (var mod in mods)
        {
            var line = new JsonObject
            {
                ["name"] = mod.Name,
            };
            if ((extraData & OptionalData.Url) != 0)
            {
                var url = mod.Url;
                if (url.Length != 0)
                {
                    line["url"] = url;
                }
            }
            if ((extraData & OptionalData.Version) != 0)
            {
                var ver = mod.EffectiveVersion;
                if (ver.Length != 0)
                {
                    line["version"] = ver;
                }
            }
            if ((extraData & OptionalData.Authors) != 0 && mod.Authors.Count != 0)
            {
                var authors = new JsonArray();
                foreach (var author in mod.Authors)
                {
                    authors.Add((JsonNode?)JsonValue.Create(author)); // non-generic Add: AOT/trimming clean
                }
                line["authors"] = authors;
            }
            if ((extraData & OptionalData.FileName) != 0)
            {
                line["filename"] = mod.FileName;
            }
            lines.Add((JsonNode?)line); // non-generic Add: AOT/trimming clean
        }
        return ToIndentedStringWithTrailingNewline(lines);
    }

    /// <summary>QJsonDocument::toJson(): QtJson's indented writer plus the trailing "\n".</summary>
    private static string ToIndentedStringWithTrailingNewline(JsonNode node)
    {
        return Minecraft.QtJson.ToIndentedString(node) + "\n";
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
            ModListFormat.HTML => ToHtml(mods, extraData),
            ModListFormat.MARKDOWN => ToMarkdown(mods, extraData),
            ModListFormat.PLAINTXT => ToPlainTxt(mods, extraData),
            ModListFormat.JSON => ToJson(mods, extraData),
            ModListFormat.CSV => ToCsv(mods, extraData),
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
