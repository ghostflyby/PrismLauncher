// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of launcher/minecraft/Library.{h,cpp}.
//
// The C++ protected data members are exposed as get/set properties here because the C++ code
// grants friend access to MojangVersionFormat, OneSixVersionFormat and LibraryTest; C# has no
// friends, and hiding them behind mutable accessors would not change the coupling.
// Deviations:
//  - getDownloads() creates Net::Request objects exactly like upstream (no network activity
//    happens until the requests are actually started), but returns List<NetRequest>.
//  - Net::ApiRequest::makeCached (makeCached + AddAPIHeaders) maps to NetRequest.MakeCached
//    with the AddAPIHeaders option.
//  - The qDebug()/qWarning() log lines are dropped.
//  - This checkout has no ".pack.xz" handling in Library::getDownloads (older MultiMC branches
//    rewrote forge URLs to .pack.xz); nothing to port there.

using System.Security.Cryptography;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Net;
using TruePath;

namespace FlintLauncher.Core.Minecraft;

public class Library
{
    /// the basic gradle dependency specifier.
    public GradleSpecifier Name { get; set; } = new();

    /// DEPRECATED URL prefix of the maven repo where the file can be downloaded
    public string RepositoryUrl { get; set; } = "";

    /// DEPRECATED: Prism Launcher-specific absolute URL. takes precedence over the implicit maven repo URL, if defined
    public string AbsoluteUrl { get; set; } = "";

    /// Prism Launcher extension - filename override
    public string FilenameOverride { get; set; } = "";

    /// DEPRECATED Prism Launcher extension - display name
    public string DisplayNameValue { get; set; } = "";

    /// Prism Launcher-specific type hint - modifies how the library is treated
    public string Hint { get; set; } = "";

    /**
     * storage - by default the local libraries folder in Prism Launcher, but could be elsewhere
     * Prism Launcher specific, because of FTB.
     */
    public string StoragePrefixValue { get; init; } = "";

    /// true if the library had an extract/excludes section (even empty)
    public bool HasExcludes { get; set; }

    /// a list of files that shouldn't be extracted from the library
    public List<string> ExtractExcludes { get; } = [];

    /// native suffixes per OS
    public Dictionary<string, string> NativeClassifiers { get; } = [];

    /// true if the library had a rules section (even empty)
    public bool ApplyRules { get; set; }

    /// rules associated with the library
    public List<Rule> Rules { get; } = [];

    /// MOJANG: container with Mojang style download info
    public MojangLibraryDownloadInfo? MojangDownloads { get; set; }

    public Library() { }

    public Library(string name)
    {
        Name = new GradleSpecifier(name);
    }

    /// <summary>
    /// limited copy without some data. TODO: why? (upstream copies name, repo URL, hint,
    /// absolute URL, extract excludes, native classifiers, rules, storage prefix, Mojang
    /// download info and filename — NOT the display name, has-excludes flag or applyRules)
    /// </summary>
    public static Library LimitedCopy(Library baseLib)
    {
        var newlib = new Library
        {
            Name = baseLib.Name,
            RepositoryUrl = baseLib.RepositoryUrl,
            Hint = baseLib.Hint,
            AbsoluteUrl = baseLib.AbsoluteUrl,
            StoragePrefixValue = baseLib.StoragePrefixValue,
            MojangDownloads = baseLib.MojangDownloads,
            FilenameOverride = baseLib.FilenameOverride,
        };
        newlib.ExtractExcludes.AddRange(baseLib.ExtractExcludes);
        foreach (var (os, classifier) in baseLib.NativeClassifiers)
        {
            newlib.NativeClassifiers[os] = classifier;
        }

        newlib.Rules.AddRange(baseLib.Rules);
        return newlib;
    }

    /// <summary>Returns the raw name field</summary>
    public GradleSpecifier RawName() => Name;

    public void SetClassifier(string spec) => Name = Name.WithClassifier(spec);

    /// <summary>returns the full group and artifact prefix</summary>
    public string ArtifactPrefix() => Name.ArtifactPrefix();

    /// <summary>get the artifact ID</summary>
    public string ArtifactId() => Name.ArtifactId;

    /// <summary>get the artifact version</summary>
    public string Version() => Name.Version;

    /// <summary>Returns true if the library is native</summary>
    public bool IsNative() => NativeClassifiers.Count != 0;

    /// <summary>Set the load rules</summary>
    public void SetRules(IEnumerable<Rule> rules)
    {
        Rules.Clear();
        Rules.AddRange(rules);
    }

    /// <summary>
    /// Returns true if the library should be loaded (or extracted, in case of natives)
    /// </summary>
    public bool IsActive(RuntimeContext runtimeContext)
    {
        var result = true;
        if (Rules.Count == 0)
        {
            result = true;
        }
        else
        {
            var ruleResult = Rule.Action.Disallow;
            foreach (var rule in Rules)
            {
                var temp = rule.Apply(runtimeContext);
                if (temp != Rule.Action.Defer)
                {
                    ruleResult = temp;
                }
            }

            result = result && ruleResult == Rule.Action.Allow;
        }

        if (IsNative())
        {
            result = result && GetCompatibleNative(runtimeContext) is not null;
        }

        return result;
    }

    /// <summary>Returns true if the library is contained in an instance and false if it is shared</summary>
    public bool IsLocal() => Hint == "local";

    /// <summary>Returns true if the library is to always be checked for updates</summary>
    public bool IsAlwaysStale() => Hint == "always-stale";

    /// <summary>
    /// Return true if the library requires forge XZ hacks. NOTE: upstream declares isForge()
    /// in Library.h but never defines or calls it in this checkout, so it is intentionally
    /// not implemented here.
    /// </summary>
    /// <summary>Get the compatible native classifier for the current runtime context, or null.</summary>
    public string? GetCompatibleNative(RuntimeContext runtimeContext)
    {
        // try to match precise classifier "[os]-[arch]"
        if (!NativeClassifiers.TryGetValue(runtimeContext.GetClassifier(), out var entry))
        {
            // try to match imprecise classifier on legacy architectures "[os]"
            if (runtimeContext.IsLegacyArch())
            {
                NativeClassifiers.TryGetValue(runtimeContext.System, out entry);
            }
        }

        return entry;
    }

    /// <summary>
    /// Collect applicable files for the library. Depending on whether the library is native or
    /// not, it adds paths to the appropriate lists for jar files, native libraries, and the
    /// 32-bit/64-bit native library split.
    /// </summary>
    public void GetApplicableFiles(
        RuntimeContext runtimeContext,
        List<AbsolutePath> jar,
        List<AbsolutePath> native,
        List<AbsolutePath> native32,
        List<AbsolutePath> native64,
        AbsolutePath overridePath,
        AbsolutePath storageBase = default)
    {
        var local = IsLocal();

        AbsolutePath ActualPath(string relPath)
        {
            relPath = FileNaming.RemoveInvalidPathChars(relPath);
            var outPath = new LocalPath(StoragePrefix()) / relPath;
            if (local && overridePath.Value is { Length: > 0 })
            {
                var fileName = outPath.FileName;
                return new AbsolutePath(Path.GetFullPath((overridePath / fileName).Value));
            }

            // the default prefix is the C++-era relative "libraries/", which anchored on
            // the C++ process CWD (the application chdir'd into its data dir at startup);
            // the port has no chdir (DI-B), so an unstamped prefix must anchor on the
            // storage base (the store root) — never on the process CWD
            if (!Path.IsPathRooted(outPath.Value) && storageBase.Value is { Length: > 0 })
            {
                outPath = storageBase / outPath;
            }

            return new AbsolutePath(Path.GetFullPath(outPath.Value));
        }

        var rawStorage = StorageSuffix(runtimeContext);
        if (IsNative())
        {
            if (rawStorage.Contains("${arch}", StringComparison.Ordinal))
            {
                var nat32Storage = rawStorage.Replace("${arch}", "32");
                var nat64Storage = rawStorage.Replace("${arch}", "64");
                native32.Add(ActualPath(nat32Storage));
                native64.Add(ActualPath(nat64Storage));
            }
            else
            {
                native.Add(ActualPath(rawStorage));
            }
        }
        else
        {
            jar.Add(ActualPath(rawStorage));
        }
    }

    /// <summary>
    /// Get download requests for the library files. Like upstream, this only prepares the
    /// request objects (resolving the cache entry); starting the requests performs the actual
    /// transfers.
    /// </summary>
    public List<NetRequest> GetDownloads(
        RuntimeContext runtimeContext,
        HttpMetaCache cache,
        List<string> failedLocalFiles,
        string overridePath)
    {
        var result = new List<NetRequest>();
        var stale = IsAlwaysStale();
        var local = IsLocal();

        void CheckLocalFile(string storage)
        {
            var fileName = Path.GetFileName(storage);
            // an empty override keeps the bare relative name (the string-era
            // Path.Combine("", name) behavior); callers pass "" for "no override"
            var fullPath = overridePath.Length > 0
                ? (new AbsolutePath(overridePath) / fileName).Value
                : fileName;
            if (!File.Exists(fullPath))
            {
                failedLocalFiles.Add(fullPath);
            }
        }

        void AddDownload(string storage, string url, string sha1)
        {
            if (local)
            {
                CheckLocalFile(storage);
                return;
            }

            var entry = cache.ResolveEntry("libraries", storage);
            if (stale)
            {
                entry.IsStale = true;
            }

            if (!entry.IsStale)
            {
                return;
            }

            var options = RequestOptions.NoOptions;
            if (stale)
            {
                options |= RequestOptions.AcceptLocalFiles;
            }

            // Don't add a time limit for the libraries cache entry validity
            options |= RequestOptions.MakeEternal;

            // Net::ApiRequest::makeCached == makeCached + AddAPIHeaders
            options |= RequestOptions.AddApiHeaders;

            var dl = NetRequest.MakeCached(cache, new Uri(url), entry, options);
            if (sha1.Length != 0)
            {
                dl.AddValidator(ChecksumValidator.FromHex(HashAlgorithmName.SHA1, sha1));
            }

            result.Add(dl);
        }

        var rawStorage = StorageSuffix(runtimeContext);
        if (MojangDownloads is not null)
        {
            if (IsNative())
            {
                var nativeClassifier = GetCompatibleNative(runtimeContext);
                if (nativeClassifier is not null)
                {
                    if (nativeClassifier.Contains("${arch}", StringComparison.Ordinal))
                    {
                        var nat32Classifier = nativeClassifier.Replace("${arch}", "32");
                        var nat64Classifier = nativeClassifier.Replace("${arch}", "64");
                        var nat32Info = MojangDownloads.GetDownloadInfo(nat32Classifier);
                        if (nat32Info is not null)
                        {
                            var cookedStorage = rawStorage.Replace("${arch}", "32");
                            AddDownload(cookedStorage, nat32Info.Url, nat32Info.Sha1);
                        }

                        var nat64Info = MojangDownloads.GetDownloadInfo(nat64Classifier);
                        if (nat64Info is not null)
                        {
                            var cookedStorage = rawStorage.Replace("${arch}", "64");
                            AddDownload(cookedStorage, nat64Info.Url, nat64Info.Sha1);
                        }
                    }
                    else
                    {
                        var info = MojangDownloads.GetDownloadInfo(nativeClassifier);
                        if (info is not null)
                        {
                            AddDownload(rawStorage, info.Url, info.Sha1);
                        }
                    }
                }
            }
            else
            {
                if (MojangDownloads.Artifact is { } artifact)
                {
                    AddDownload(rawStorage, artifact.Url, artifact.Sha1);
                }
            }
        }
        else
        {
            string RawDl()
            {
                if (AbsoluteUrl.Length != 0)
                {
                    return AbsoluteUrl;
                }

                if (RepositoryUrl.Length == 0)
                {
                    return FlintConfig.Default.LibraryBase + rawStorage;
                }

                return RepositoryUrl.EndsWith('/')
                    ? RepositoryUrl + rawStorage
                    : RepositoryUrl + '/' + rawStorage;
            }

            var rawDl = RawDl();
            if (rawStorage.Contains("${arch}", StringComparison.Ordinal))
            {
                AddDownload(rawStorage.Replace("${arch}", "32"), rawDl.Replace("${arch}", "32"), "");
                AddDownload(rawStorage.Replace("${arch}", "64"), rawDl.Replace("${arch}", "64"), "");
            }
            else
            {
                AddDownload(rawStorage, rawDl, "");
            }
        }

        return result;
    }

    /// <summary>Get the file name of the library</summary>
    public string Filename(RuntimeContext runtimeContext)
    {
        if (FilenameOverride.Length != 0)
        {
            return FilenameOverride;
        }

        // non-native? use only the gradle specifier
        if (!IsNative())
        {
            return Name.GetFileName() ?? "";
        }

        // otherwise native, override classifiers. Mojang HACK!
        var nativeSpec = Name.WithClassifier(GetCompatibleNative(runtimeContext) ?? "INVALID");
        return nativeSpec.GetFileName() ?? "";
    }

    /// <summary>Get the display name of the library (defaults to the filename)</summary>
    public string DisplayName(RuntimeContext runtimeContext)
    {
        if (DisplayNameValue.Length != 0)
        {
            return DisplayNameValue;
        }

        return Filename(runtimeContext);
    }

    /// <summary>The Maven-relative storage path under the libraries root — the same string
    /// the download path keys the ledger entry with (base "libraries"; see GetDownloads) and
    /// the store-completeness derivation checks. Native ${arch} placeholders are
    /// NOT expanded here (GetApplicableFiles expands them into the 32/64 pair).</summary>
    public string StorageRelativePath(RuntimeContext runtimeContext) => StorageSuffix(runtimeContext);

    /// <summary>the default storage prefix used by Prism Launcher</summary>
    private static string DefaultStoragePrefix() => "libraries/";

    /// <summary>Get the prefix - root of the storage to be used</summary>
    private string StoragePrefix() => StoragePrefixValue.Length == 0 ? DefaultStoragePrefix() : StoragePrefixValue;

    /// <summary>Get the relative file path where the library should be saved</summary>
    private string StorageSuffix(RuntimeContext runtimeContext)
    {
        // non-native? use only the gradle specifier
        if (!IsNative())
        {
            return Name.ToPath(FilenameOverride) ?? "";
        }

        // otherwise native, override classifiers. Mojang HACK!
        var nativeSpec = Name.WithClassifier(GetCompatibleNative(runtimeContext) ?? "INVALID");
        return nativeSpec.ToPath(FilenameOverride) ?? "";
    }
}
