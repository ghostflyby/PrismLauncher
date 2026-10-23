// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/auth/AccountList.{h,cpp} (persistence + list semantics).
//
// Deviations:
//  - The QAbstractListModel surface (data/headerData/flags/setData, column/role plumbing) is not
//    ported — FlintLauncher's account UI is not a Qt item model. The list semantics (add/replace
//    by profile id, remove, move, default-account activation) are kept.
//  - The background refresh schedule (m_refreshQueue/m_explicitRefreshes + the QTimer-driven
//    fillQueue/tryNext loop and the activity counter) is deferred; requestRefresh() /
//    queueRefresh() / isActive() therefore have no port here.
//  - QSaveFile + QFile::setPermissions(ReadOwner|WriteOwner|ReadUser|WriteUser) maps to an
//    atomic temp-file write with unix mode 0600 (where the OS supports it).
//  - loadV3() clears the list before loading (upstream only ever calls it on a fresh model).
//  - The file is the typed wire of AccountWire.cs (docs/JSON-MIGRATION.md): written as
//    standard indented JSON through AuthJsonContext, read leniently (a structurally broken
//    account document is skipped instead of half-loaded; the format version also accepts
//    the QVariant digit-string form).

using System.Text.Json;
using FlintLauncher.Core.IO;
using TruePath;
using TruePath.SystemIo;

namespace FlintLauncher.Core.Auth;

/// <summary>List of available Mojang accounts.</summary>
public sealed class AccountList
{
    private enum AccountListVersion
    {
        MojangMsa = 3,
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

    public void SetListFilePath(AbsolutePath path, bool enableAutosave = false)
    {
        _listFilePath = path;
        _autosave = enableAutosave;
    }

    public bool LoadList()
    {
        byte[] jsonData;
        try
        {
            jsonData = _listFilePath.ReadAllBytes();
        }
        catch (Exception)
        {
            // qCritical() << "Failed to read the account list file" ...
            return false;
        }

        AccountFileJson? root;
        try
        {
            root = JsonSerializer.Deserialize(jsonData, AuthJsonContext.Default.AccountFileJson);
        }
        catch (JsonException)
        {
            // qCritical() << "Failed to parse account list file ..."
            return false;
        }

        if (root is null)
        {
            return false;
        }

        // Make sure the format version matches.
        if (root.FormatVersion == (int)AccountListVersion.MojangMsa)
        {
            return LoadV3(root);
        }

        const string newName = "accounts-old.json";
        // Deliberate divergence from C++ (AccountList.cpp:478-481): the C++ passes a bare relative name to QFile::rename,
        // which resolves against the PROCESS CWD — the rename usually fails (or lands elsewhere) whenever the CWD is not
        // the data directory, despite the warning text promising a rename to accounts-old.json. Flint keeps the process
        // CWD non-load-bearing (see WebHostBuilder), so the backup is created next to the original file — the behavior
        // the warning actually describes. Both sides return false and never load old-format accounts; on any failure
        // the original file is left untouched.
        // Attempt to rename the old version.
        try
        {
            if (_listFilePath.Parent is { } parent)
            {
                _listFilePath.Move(parent / newName);
            }
        }
        catch (Exception)
        {
            // best-effort rename: a leftover file at the old path is harmless
        }

        return false;
    }

    public bool LoadV3(AccountFileJson root)
    {
        _accounts.Clear();
        _defaultAccount = null;
        if (root.Accounts is null)
        {
            return true;
        }

        foreach (var accountElement in root.Accounts)
        {
            if (accountElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            AccountJson? wire;
            try
            {
                wire = accountElement.Deserialize(AuthJsonContext.Default.AccountJson);
            }
            catch (JsonException)
            {
                // a structurally broken account is skipped, not half-loaded with silently
                // empty members (the deliberate divergence from the per-field C++ probing)
                continue;
            }

            if (wire is null)
            {
                continue;
            }

            var account = MinecraftAccount.LoadFromJsonV3(wire);
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
                if (wire.Active == true)
                {
                    _defaultAccount = account;
                }
            }
            // qWarning() << "Failed to load an account.";
        }

        return true;
    }

    public bool SaveList()
    {
        // no path is set yet (the C++ empty-string check); default(AbsolutePath) carries null
        if (_listFilePath.Value is not { Length: > 0 })
        {
            return false;
        }

        // make sure the parent folder exists
        if (!FileOps.EnsureFilePathExists(_listFilePath))
        {
            return false;
        }

        // make sure the file wasn't overwritten with a folder before (fixes a bug)
        if (_listFilePath.ExistsDirectory())
        {
            try
            {
                _listFilePath.DeleteDirectoryRecursively();
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Build the typed wire document and write it through the source-generated context
        // (the write shape is ours; the read side is the compatibility surface). The
        // per-account documents go through JsonElement so the read side keeps skipping a
        // structurally broken account instead of failing the whole file.
        AccountFileJson root = new()
        {
            FormatVersion = (int)AccountListVersion.MojangMsa,
            Accounts = _accounts.Select(account =>
            {
                var wire = account.SaveToJson();
                if (ReferenceEquals(_defaultAccount, account))
                {
                    wire = wire with { Active = true };
                }

                return JsonSerializer.SerializeToElement(wire, AuthJsonContext.Default.AccountJson);
            }).ToArray(),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, AuthJsonContext.Default.AccountFileJson);
        try
        {
            // QSaveFile: write to a temporary file and atomically rename over the target
            var tmpPath = _listFilePath.Parent is { } parent
                ? parent / (_listFilePath.FileName + ".tmp")
                : new AbsolutePath(_listFilePath.Value + ".tmp");
            using (var stream = tmpPath.Open(FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmpPath.Value, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            tmpPath.Move(_listFilePath, overwrite: true);
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
    private AbsolutePath _listFilePath;
    private bool _autosave;
}
