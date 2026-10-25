// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
//
// Opt-in background account refresh: on startup (after a small delay) and on a fixed interval,
// re-run the auth flow for every account whose token is stale (MinecraftAccount.ShouldRefresh).
// Disabled by default for C++ parity — enable with FLINT_ACCOUNT_REFRESH_MINUTES=<n>.
//
// Decision: this service is the accepted replacement for the
// C++ AccountList fillQueue/tryNext refresh queue — the full queue (activity counting, the
// per-account scheduled slots) is GUI-era machinery and is NOT ported. The per-launch refresh
// path (LaunchController.DecideLaunchMode -> MinecraftAccount.Refresh) stays the primary
// correctness net; this service only pre-warms tokens for long-running hosts.

using FlintLauncher.Core.Auth;

namespace FlintLauncher.Web.Services;

public sealed class AccountRefreshService(
    AccountList accounts,
    AuthEndpointsOptions auth,
    ILogger<AccountRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    public static int? ResolveIntervalMinutes() =>
        int.TryParse(Environment.GetEnvironmentVariable("FLINT_ACCOUNT_REFRESH_MINUTES"),
            out var minutes) && minutes > 0
            ? minutes
            : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = ResolveIntervalMinutes();
        if (minutes is null)
        {
            logger.LogInformation("Background account refresh disabled (set FLINT_ACCOUNT_REFRESH_MINUTES to enable)");
            return;
        }

        var interval = TimeSpan.FromMinutes(minutes.Value);
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshStaleAccountsAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    internal async Task RefreshStaleAccountsAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < accounts.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var account = accounts.At(i);
            if (account.AccountType == AccountType.Offline || !account.ShouldRefresh())
            {
                continue;
            }

            logger.LogInformation("Refreshing account '{Profile}'", account.ProfileName());
            try
            {
                var refresh = account.Refresh(auth);
                if (refresh is not null && !refresh.IsFinished)
                {
                    await refresh.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // a failing account must never kill the loop; the next launch surfaces the error
                logger.LogWarning(e, "Background refresh for '{Profile}' failed", account.ProfileName());
            }
        }
    }
}
