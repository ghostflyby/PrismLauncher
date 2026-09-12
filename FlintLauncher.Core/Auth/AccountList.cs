// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/AccountList.{h,cpp} (persistence + list semantics).
//
// Deviations:
//  - The QAbstractListModel surface (data/headerData/flags/setData, column/role plumbing) is not
//    ported — FlintLauncher's account UI is not a Qt item model. The list semantics (add/replace
//    by profile id, remove, move, default-account activation) are kept.
//  - The background refresh schedule (m_refreshQueue/m_explicitRefreshes + the QTimer-driven
//    fillQueue/tryNext loop and the activity counter) is deferred to P5; requestRefresh() /
//    queueRefresh() / isActive() therefore have no port here.
//  - QSaveFile + QFile::setPermissions(ReadOwner|WriteOwner|ReadUser|WriteUser) maps to an
//    atomic temp-file write with unix mode 0600 (where the OS supports it).
//  - loadV3() clears the list before loading (upstream only ever calls it on a fresh model).
//  - The JSON is written sorted-by-key with Qt's 4-space indentation (QJsonDocument::Indented),
//    matching the QtJson helper used by the other persisted formats.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlintLauncher.Core.IO;
using FlintLauncher.Core.Minecraft;

namespace FlintLauncher.Core.Auth;

/// <summary>List of available Mojang accounts.</summary>
public sealed class AccountList
{
    private enum AccountListVersion
    {
        MojangMSA = 3,
    }

    public AccountList()
    {
    }

    public int Count => _accounts.Count;

    public MinecraftAccount At(int i) => _accounts[i];

    public int FindAccountByProfileId(string profileId)
    {
        for (var i = 0; i < Count; i++)
        {
            if (At(i).ProfileId() == profileId)
            {
                return i;
            }
        }
        return -1;
    }

    public MinecraftAccount? GetAccountByProfileName(string profileName)
    {
        foreach (var account in _accounts)
        {
            if (account.ProfileName() == profileName)
            {
                return account;
            }
        }
        return null;
    }

    public List<string> ProfileNames()
    {
        var outList = new List<string>();
        foreach (var account in _accounts)
        {
            var profileName = account.ProfileName();
            if (profileName.Length == 0)
            {
                continue;
            }
            outList.Add(profileName);
        }
        return outList;
    }

    public void AddAccount(MinecraftAccount account)
    {
        // NOTE: Do not allow adding something that's already there. We shouldn't let it continue
        // because of the signal / slot connections after this.
        if (_accounts.Contains(account))
        {
            return;
        }

        // override/replace existing account with the same profileId
        var profileId = account.ProfileId();
        if (profileId.Length != 0)
        {
            var existingAccount = FindAccountByProfileId(profileId);
            if (existingAccount != -1)
            {
                var existingAccountPtr = _accounts[existingAccount];
                _accounts[existingAccount] = account;
                if (ReferenceEquals(_defaultAccount, existingAccountPtr))
                {
                    _defaultAccount = account;
                }
                OnListChanged();
                return;
            }
        }

        // if we don't have this profileId yet, add the account to the end
        _accounts.Add(account);
        OnListChanged();
    }

    public void RemoveAccount(int row)
    {
        if (row >= 0 && row < _accounts.Count)
        {
            var account = _accounts[row];
            if (ReferenceEquals(account, _defaultAccount))
            {
                _defaultAccount = null;
                OnDefaultAccountChanged();
            }
            _accounts.RemoveAt(row);
            OnListChanged();
        }
    }

    public void MoveAccount(int row, int delta)
    {
        var newRow = row + delta;
        if (row < _accounts.Count && newRow >= 0 && newRow < _accounts.Count)
        {
            var moved = _accounts[row];
            _accounts.RemoveAt(row);
            _accounts.Insert(newRow, moved);
            OnListChanged();
        }
    }

    public MinecraftAccount? DefaultAccount => _defaultAccount;

    public void SetDefaultAccount(MinecraftAccount? newAccount)
    {
        if (newAccount is null && _defaultAccount is not null)
        {
            _defaultAccount = null;
            OnDefaultAccountChanged();
        }
        else
        {
            // upstream only accepts accounts that are actually on the list
            var onList = _accounts.FirstOrDefault(a => ReferenceEquals(a, newAccount));
            if (onList is null || ReferenceEquals(_defaultAccount, onList))
            {
                return;
            }
            _defaultAccount = onList;
            OnDefaultAccountChanged();
        }
    }

    /// <summary>Port of AccountList::anyAccountIsValid.</summary>
    public bool AnyAccountIsValid()
    {
        foreach (var account in _accounts)
        {
            if (account.OwnsMinecraft())
            {
                return true;
            }
        }
        return false;
    }

    public void SetListFilePath(string path, bool autosave = false)
    {
        _listFilePath = path;
        _autosave = autosave;
    }

    public bool LoadList()
    {
        if (string.IsNullOrEmpty(_listFilePath))
        {
            return false;
        }

        byte[] jsonData;
        try
        {
            jsonData = FileSystem.Read(_listFilePath);
        }
        catch (Exception)
        {
            // qCritical() << "Failed to read the account list file" ...
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(jsonData);
        }
        catch (JsonException)
        {
            // qCritical() << "Failed to parse account list file ..."
            return false;
        }
        if (node is not JsonObject root)
        {
            return false;
        }

        // Make sure the format version matches.
        var listVersion = EnsureIntegerLike(root["formatVersion"]);
        if (listVersion == (int)AccountListVersion.MojangMSA)
        {
            return LoadV3(root);
        }

        const string newName = "accounts-old.json";
        // Attempt to rename the old version.
        try
        {
            File.Move(_listFilePath, Path.Combine(Path.GetDirectoryName(_listFilePath) ?? "", newName));
        }
        catch (Exception)
        {
        }
        return false;
    }

    public bool LoadV3(JsonObject root)
    {
        _accounts.Clear();
        _defaultAccount = null;
        if (root["accounts"] is not JsonArray accounts)
        {
            return true;
        }
        foreach (var accountVal in accounts)
        {
            if (accountVal is not JsonObject accountObj)
            {
                continue;
            }
            var account = MinecraftAccount.LoadFromJsonV3(accountObj);
            if (account is not null)
            {
                var profileId = account.ProfileId();
                if (profileId.Length != 0)
                {
                    if (FindAccountByProfileId(profileId) != -1)
                    {
                        continue;
                    }
                }
                _accounts.Add(account);
                if (AccountData.EnsureBool(accountObj["active"]))
                {
                    _defaultAccount = account;
                }
            }
            else
            {
                // qWarning() << "Failed to load an account.";
            }
        }
        return true;
    }

    public bool SaveList()
    {
        if (string.IsNullOrEmpty(_listFilePath))
        {
            return false;
        }

        // make sure the parent folder exists
        if (!FileSystem.EnsureFilePathExists(_listFilePath))
        {
            return false;
        }

        // make sure the file wasn't overwritten with a folder before (fixes a bug)
        if (Directory.Exists(_listFilePath))
        {
            try
            {
                Directory.Delete(_listFilePath, recursive: true);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Build the JSON document to write to the list file.
        JsonObject root = new()
        {
            ["formatVersion"] = (int)AccountListVersion.MojangMSA,
        };

        // Build a list of accounts.
        var accounts = new JsonArray();
        foreach (var account in _accounts)
        {
            var accountObj = account.SaveToJson();
            if (ReferenceEquals(_defaultAccount, account))
            {
                accountObj["active"] = true;
            }
            accounts.Add((JsonNode?)accountObj);
        }
        root["accounts"] = accounts;

        // Now that we're done building the JSON object, we can write it to the file.
        var bytes = Encoding.UTF8.GetBytes(QtJson.ToIndentedString(root));
        try
        {
            // QSaveFile: write to a temporary file and atomically rename over the target
            var tmpPath = _listFilePath + ".tmp";
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmpPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tmpPath, _listFilePath, overwrite: true);
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- events / autosave

    /// <summary>Port of the listChanged() signal.</summary>
    public event Action? ListChanged;

    /// <summary>Port of the defaultAccountChanged() signal.</summary>
    public event Action? DefaultAccountChanged;

    private void OnListChanged()
    {
        if (_autosave)
        {
            // TODO: Alert the user if this fails.
            SaveList();
        }
        ListChanged?.Invoke();
    }

    private void OnDefaultAccountChanged()
    {
        if (_autosave)
        {
            SaveList();
        }
        DefaultAccountChanged?.Invoke();
    }

    private readonly List<MinecraftAccount> _accounts = [];
    private MinecraftAccount? _defaultAccount;
    private string _listFilePath = "";
    private bool _autosave;

    /// <summary>QVariant::toInt over the formatVersion value (numbers and digit strings).</summary>
    private static int EnsureIntegerLike(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var i))
            {
                return i;
            }
            if (value.TryGetValue<double>(out var d) && (int)d == d)
            {
                return (int)d;
            }
            if (value.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed))
            {
                return parsed;
            }
        }
        return 0;
    }
}
