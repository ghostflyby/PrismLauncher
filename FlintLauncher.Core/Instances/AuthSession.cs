// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
// SPDX-FileCopyrightText: 2026 Octol1ttle <l1ttleofficial@outlook.com>
//
// Port of launcher/minecraft/auth/AuthSession.{h,cpp} (data subset) and launcher/LaunchMode.h.
// Only the data members and helpers the instance/launch layer consumes are ported; the
// MinecraftAccount machinery that fills the session in is the auth layer's business.
//
// launch reconciliation: upstream has no status enum on AuthSession (the launch pipeline tracks
// state itself), so the draft's data surface was already complete; MinecraftAccount::fillSession
// lives on FlintLauncher.Core.Auth.MinecraftAccount.FillSession.

using System.Text.Json.Nodes;

namespace FlintLauncher.Core.Instances;

/// <summary>Port of LaunchMode.</summary>
public enum LaunchMode
{
    Normal,
    Offline,
    Demo,
}

/// <summary>Port of AuthSession: the launch-time view of an account.</summary>
public sealed class AuthSession
{
    /// <summary>combined session ID</summary>
    public string Session = "";

    /// <summary>volatile auth token</summary>
    public string AccessToken = "";

    /// <summary>profile name</summary>
    public string PlayerName = "";

    /// <summary>profile ID</summary>
    public string Uuid = "";

    /// <summary>'msa' or 'offline', depending on account type</summary>
    public string UserType = "";

    /// <summary>the actual launch mode for this session</summary>
    public LaunchMode LaunchMode = LaunchMode.Normal;

    /// <summary>Port of AuthSession::MakeOffline.</summary>
    public bool MakeOffline(string offlinePlayername)
    {
        Session = "-";
        AccessToken = "0";
        PlayerName = offlinePlayername;
        return true;
    }

    /// <summary>Port of AuthSession::MakeDemo.</summary>
    public void MakeDemo(string name, string uuid)
    {
        Uuid = uuid;
        Session = "-";
        AccessToken = "0";
        PlayerName = name;
        LaunchMode = LaunchMode.Demo;
    }

    /// <summary>Port of AuthSession::serializeUserProperties (an empty user attribute object).</summary>
    public string SerializeUserProperties()
    {
        var userAttrs = new JsonObject();
        return userAttrs.ToJsonString(); // QJsonDocument::Compact
    }
}
