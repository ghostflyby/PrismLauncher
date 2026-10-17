// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/InstanceCopyTask.{h,cpp}: copies an instance folder into the staging
// area honoring the InstanceCopyPrefs exclusion filters, then fixes up the copy's metadata
// (new name/icon/uuid, optional playtime reset) through a generic NullInstance shell —
// exactly like the C++, which only needs a BaseInstance surface for the settings writes.
//
// Deviations:
//  - the copy runs synchronously inside StartAsync (no QtConcurrent::run / QFutureWatcher;
//    the C++ progress bookkeeping of the fileCopied() signal is dropped with it).
//  - the symlink / hard-link / reflink transport modes are not portable here
//    (FS::create_link and FS::clone are declared not ported in FileSystem.cs): the task
//    fails with a clear message when they are requested. The link-only extras of
//    copyFinished() (addLinkedInstanceId + allowed_symlinks.txt bookkeeping) are part of
//    that path and are therefore not ported either. When it lands, it must pass
//    m_origInstance.Uuid to AddLinkedInstanceId — DATA D3 makes the uuid the persisted
//    reference form (the C++ passed id(), the dirname; that form is not carried over).
using System.Text.RegularExpressions;
using FlintLauncher.Core.IO;

namespace FlintLauncher.Core.Instances;

public class InstanceCopyTask : InstanceTask
{
    private readonly BaseInstance _origInstance;
    private readonly bool _keepPlaytime;
    private readonly bool _useLinks;
    private readonly bool _useHardLinks;
    private readonly bool _linkRecursively;
    private readonly bool _useClone;
    private readonly Regex? _matcher;

    public InstanceCopyTask(BaseInstance origInstance, InstanceCopyPrefs prefs)
    {
        _origInstance = origInstance;
        _keepPlaytime = prefs.KeepPlaytime;
        _useLinks = prefs.UseSymLinks;
        _linkRecursively = prefs.LinkRecursively;
        _useHardLinks = prefs.LinkRecursively && prefs.UseHardLinks;
        // NOTE: the C++ also computes m_copySaves = linkRecursively && dontLinkSaves &&
        // copySaves for the separate saves copy of the link path; that path is not ported.
        _useClone = prefs.UseClone;

        var filters = prefs.GetSelectedFiltersAsRegex();
        if (_useLinks || _useHardLinks)
        {
            if (filters.Length != 0)
            {
                filters += "|";
            }
            filters += "instance.cfg";
        }

        if (filters.Length != 0)
        {
            // QRegularExpression(filters, QRegularExpression::CaseInsensitiveOption)
            _matcher = new Regex(filters, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        EmitStatus("Copying instance " + _origInstance.Name());

        if (_useClone)
        {
            // FS::clone (reflink syscalls) is not ported — see FileSystem.cs.
            EmitFailed("Reflink-based instance copies are not supported in this port.");
            return Task.CompletedTask;
        }
        if (_useLinks || _useHardLinks || _linkRecursively)
        {
            // FS::create_link (symlinks/hard links, optional privilege elevation on Windows)
            // is not ported — see FileSystem.cs.
            EmitFailed("Link-based instance copies are not supported in this port.");
            return Task.CompletedTask;
        }

        Func<string, bool>? matcher = _matcher is null ? null : path => _matcher.IsMatch(path);
        if (!FileSystem.Copy(_origInstance.InstanceRoot(), StagingPath, matcher))
        {
            EmitFailed("Instance folder copy failed.");
            return Task.CompletedTask;
        }

        // copyFinished(): the copy's instance layer is rewritten through a generic (null
        // type) instance shell over the typed adapter — the staged folder commits as Flint
        // format (instance.json), whatever format the source was
        var instanceSettings = new Config.InstanceJsonSettingsObject(
            StagingPath, Environment ?? throw new InvalidOperationException(
                "InstanceTask requires SetParentEnvironment before execution."));
        BaseInstance inst = new NullInstance(instanceSettings, StagingPath);
        inst.SetName(Name());
        inst.SetIconKey(Icon);
        inst.RegenerateUuid();
        if (!_keepPlaytime)
        {
            inst.ResetTimePlayed();
        }

        EmitSucceeded();
        return Task.CompletedTask;
    }
}
