// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Lenny McLennington <lenny@sneed.church>
// SPDX-FileCopyrightText: 2022 Swirl <swurl@swurl.xyz>
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
// Port of launcher/net/PasteUpload.{h,cpp}: upload a launcher log to a paste site and get back
// a link. Supports the same four paste providers as upstream (0x0.st, hastebin, paste.gg,
// mclo.gs); each provider's request shape and reply parsing is the paste client's upload
// operation (FlintLauncher.ExternalApi.Paste), this unit owns the log side.
//
// Deviations:
//  - The C++ constructor anonymizes the log in place (logs/AnonymizeLog.cpp); the port applies
//    the same seven rules in AnonymizeLog() before the request is built.
//  - The base URL is a constructor parameter (like upstream's url argument); tests stub it,
//    there is no static endpoint override.

using System.Text.RegularExpressions;
using FlintLauncher.ExternalApi.Paste;

namespace FlintLauncher.Core.Net;

public sealed class PasteUpload
{
    private readonly string _log;
    private readonly PasteUploadQuery _query;
    private readonly HttpClient _http;

    /// <summary>Port of PasteUpload::PasteUpload(log, url, pasteType): anonymizes the log; an
    /// empty url selects the provider default base. The request rides the injected client
    /// (the host's; tests isolate their own).</summary>
    public PasteUpload(string log, string url, PasteType pasteType, HttpClient http)
    {
        _log = AnonymizeLog(log);
        _query = new PasteClient(new PasteClientOptions { Type = pasteType, BaseUrl = url }).Pastes.Upload(_log);
        _http = http;
    }

    /// <summary>Port of pasteLink().</summary>
    public string PasteLink { get; private set; } = "";

    /// <summary>The resolved request URL (m_url): base + provider endpoint path.</summary>
    public string EndpointUrl => _query.Url;

    /// <summary>The anonymized log body this upload sends (exposed for tests).</summary>
    public string Log => _log;

    /// <summary>
    /// Uploads the log and returns the link the provider handed back. A network failure, a
    /// provider error, a malformed reply or a reply without a link fails as
    /// <see cref="PasteApiException"/>.
    /// </summary>
    public async Task<string> UploadAsync(CancellationToken cancellationToken = default)
    {
        PasteLink = await _query.SendOnceAsync(_http, cancellationToken).ConfigureAwait(false);
        return PasteLink;
    }

    // ------------------------------------------------------------- AnonymizeLog.cpp

    private static (Regex Reg, string With)[]? _anonymizeRules;

    /// <summary>Port of anonymizeLog(QString&): applies the seven redaction rules (all
    /// case-insensitive, all occurrences) and returns the anonymized copy.</summary>
    public static string AnonymizeLog(string log)
    {
        _anonymizeRules ??=
        [
            (new Regex(@"C:\\Users\\([^\\]+)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "C:\\Users\\********\\"), // windows
            (new Regex(@"C:/Users/([^/]+)/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "C:/Users/********/"), // windows with forward slashes
            (new Regex(@"(?<!\w)/home/[^/]+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "/home/********/"), // linux
            (new Regex(@"(?<!\w)/Users/[^/]+/", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "/Users/********/"), // macos
            (new Regex(@"\(Session ID is [^)]+\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "(Session ID is <SESSION_TOKEN>)"), // SESSION_TOKEN
            (new Regex(@"new refresh token: ""[^""]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "new refresh token: \"<TOKEN>\""), // refresh token
            (new Regex(@"""device_code"" :  ""[^""]+""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                "\"device_code\" :  \"<DEVICE_CODE>\""), // device code
        ];
        foreach (var (reg, with) in _anonymizeRules)
        {
            log = reg.Replace(log, with);
        }

        return log;
    }
}
