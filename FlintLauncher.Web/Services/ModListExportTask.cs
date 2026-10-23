// SPDX-License-Identifier: GPL-3.0-only
// The modlist leg of POST /instances/{id}/export: render the
// instance's mods through the ported ExportToModList helpers and write the result file into
// the instance directory. Runs as a registered task (202 + taskId) like the mrpack/Flame
// export legs so the endpoint shape stays uniform; the work is local and quick.
//
// C++ analogue: ExportToModListDialog (exportToModList over the ModFolderModel contents,
// user picks the format + target file — here the format comes from the request body and the
// target is always "<name>-mods.<ext>" inside the instance directory, exposed through the
// task view's OutputPath result).

using System.Security;
using FlintLauncher.Core.Instances;
using FlintLauncher.Core.ModPlatform.Helpers;
using FlintLauncher.Core.Tasks;
using TruePath;

namespace FlintLauncher.Web.Services;

public sealed class ModListExportTask(
    MinecraftInstance instance,
    AbsolutePath? outputPath,
    ModListFormat format,
    OptionalData extraData)
    : FlintTask
{
    private static readonly Dictionary<string, (ModListFormat Format, string Extension)> Formats =
        new(StringComparer.Ordinal)
        {
            ["html"] = (ModListFormat.Html, "html"),
            ["markdown"] = (ModListFormat.Markdown, "md"),
            ["plain"] = (ModListFormat.Plaintxt, "txt"),
            ["json"] = (ModListFormat.Json, "json"),
            ["csv"] = (ModListFormat.Csv, "csv"),
        };

    /// <summary>The written file (exposed through the task view's result payload).</summary>
    public AbsolutePath? OutputPath => outputPath;

    /// <summary>Maps the API format2 key ("html"|"markdown"|"plain"|"json"|"csv") to the
    /// core format + file extension; null when the key is unknown.</summary>
    public static bool TryParseFormat(string? format2, out ModListFormat format, out string extension)
    {
        if (format2 is not null && Formats.TryGetValue(format2.Trim().ToLowerInvariant(), out var entry))
        {
            format = entry.Format;
            extension = entry.Extension;
            return true;
        }

        // the C++ dialog default is HTML
        format = ModListFormat.Html;
        extension = "html";
        return false;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Collecting mods...");

        // the same mod list the /instances/{id}/mods endpoint serves (metadata-aware)
        var folder = instance.LoaderModList();
        folder.Update();
        var mods = new List<ModListMod>();
        foreach (var mod in folder.AllMods())
        {
            if (mod.Fileinfo.Name.StartsWith('.'))
            {
                continue; // hidden/scratch files, like the mods listing
            }

            var metadata = folder.GetMetadata(mod);
            mods.Add(new ModListMod
            {
                Name = metadata is { Name.Length: > 0 } ? metadata.Name : mod.Name,
                ModId = mod.ModId(),
                Url = mod.Homepage(),
                Version = mod.Version,
                MetadataVersion = metadata?.VersionNumber ?? "",
                Authors = mod.Authors(),
                FileName = mod.Fileinfo.Name,
            });
        }

        EmitStatus("Writing the mod list...");
        if (outputPath is not { } target)
        {
            EmitFailed("No output path was provided for the mod list export");
            return Task.CompletedTask;
        }

        try
        {
            File.WriteAllText(target.Value, ExportToModList.ExportModsToString(mods, format, extraData));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                      or SecurityException)
        {
            EmitFailed("Could not write the mod list: " + e.Message);
            return Task.CompletedTask;
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }
}
