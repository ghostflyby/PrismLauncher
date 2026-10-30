// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/AuthFlow.{h,cpp}.
//
// The Qt signal chain (AuthStep::finished -> AuthFlow::stepFinished -> nextStep) becomes an
// await loop: each step runs to its Finished report inside RunAsync; a Working report
// continues the chain, every other state is terminal through ChangeState(). Abort() cancels
// the in-flight step, the cancellation surfaces as OperationCanceledException, and RunAsync
// throws it to the caller.

using FlintLauncher.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FlintLauncher.Core.Auth;

public enum AuthFlowAction
{
    Refresh,
    Login,
    DeviceCode,
}

public sealed class AuthFlow
{
    private AccountTaskState _taskState = AccountTaskState.Created;
    private readonly List<AuthStep> _steps = [];

    // the terminal outcome ChangeState records while the step loop unwinds; RunAsync turns
    // it into the body's return or throw
    private string? _terminalFailure;
    private readonly ILoggerFactory? _loggerFactory;
    private AuthStep? _currentStep;
    private CancellationTokenSource? _abortSource;
    private bool _finished;

    public AuthFlow(AccountData data, AuthFlowAction action = AuthFlowAction.Refresh,
        AuthEndpointsOptions? auth = null, ILoggerFactory? loggerFactory = null)
    {
        Data = data;
        _loggerFactory = loggerFactory;
        var endpoints = auth ?? new AuthEndpointsOptions();
        if (data.Type == AccountType.Msa)
        {
            if (action == AuthFlowAction.DeviceCode)
            {
                var oauthStep = new MsaDeviceCodeStep(data, endpoints);
                oauthStep.AuthorizeWithBrowser += (url, code, expiresIn) =>
                    AuthorizeWithBrowserWithExtra?.Invoke(url, code, expiresIn);
                _steps.Add(oauthStep);
            }
            else
            {
                var oauthStep = new MsaInteractiveStep(data, silent: action == AuthFlowAction.Refresh,
                    auth: endpoints);
                oauthStep.AuthorizeWithBrowser += url => AuthorizeWithBrowser?.Invoke(url);
                _steps.Add(oauthStep);
            }

            _steps.Add(new XboxUserStep(data, endpoints));
            _steps.Add(new XboxAuthorizationStep(data, token => data.MojangServicesToken = token,
                "rp://api.minecraftservices.com/", "Mojang", endpoints));
            _steps.Add(new LauncherLoginStep(data, endpoints));
            _steps.Add(new EntitlementsStep(data, endpoints));
            _steps.Add(new MinecraftProfileStep(data, endpoints));
            _steps.Add(new GetSkinStep(data, endpoints));
        }

        ChangeState(AccountTaskState.Created);
    }

    public AccountData Data { get; }

    /// <summary>Port of AuthFlow::taskState().</summary>
    public AccountTaskState TaskState => _taskState;

    /// <summary>Whether the flow has settled: nothing will change the account again.</summary>
    public bool IsFinished => _finished;

    /// <summary>Port of the succeeded() signal: the account is authenticated and valid.</summary>
    public event Action? Succeeded;

    /// <summary>Port of the failed(QString) and aborted() signals, merged: the flow ended
    /// without authenticating the account, and <see cref="TaskState"/> says why.</summary>
    public event Action? Failed;

    /// <summary>Port of the authorizeWithBrowser(QUrl) signal (interactive login).</summary>
    public event Action<string>? AuthorizeWithBrowser;

    /// <summary>Port of the authorizeWithBrowserWithExtra(QString, QString, int) signal (device code).</summary>
    public event Action<string, string, int>? AuthorizeWithBrowserWithExtra;

    /// <summary>Runs the step chain. Returning is success; a failure throws with the reason
    /// the failing step reported, and an abort throws <see cref="OperationCanceledException"/>.
    /// The account's <see cref="Data"/> carries the outcome either way. A flow already run
    /// hands back its finished run instead of stepping twice, and a flow already running hands
    /// back the run in flight — the steps are not re-consumed, and the first caller's token
    /// owns the run.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? first;
        Task run;
        lock (_runLock)
        {
            // the entry is published before the body starts, so a second caller joins it
            // instead of racing it into the step list
            if (_run is { } existing)
            {
                return existing;
            }

            first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _run = run = first.Task;
        }

        _ = ForwardAsync(RunCoreAsync(cancellationToken), first);
        return run;
    }

    private static async Task ForwardAsync(Task work, TaskCompletionSource target)
    {
        try
        {
            await work.ConfigureAwait(false);
            target.SetResult();
        }
        catch (Exception e)
        {
            // the exception is passed through verbatim rather than through SetCanceled, so an
            // abort keeps the reason the flow gave it instead of the BCL's generic text
            target.SetException(e);
        }
    }

    private readonly Lock _runLock = new();
    private Task? _run;

    private async Task RunCoreAsync(CancellationToken cancellationToken)
    {
        using var abortSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _abortSource = abortSource;
        var token = abortSource.Token;
        try
        {
            ChangeState(AccountTaskState.Working, "Initializing");
            while (!_finished && !token.IsCancellationRequested)
            {
                if (_steps.Count == 0)
                {
                    // we got to the end without an incident... assume this is all.
                    _currentStep = null;
                    Succeed();
                    break;
                }

                _currentStep = _steps[0];
                _steps.RemoveAt(0);
                Log.LogDebug("auth step: {Step}", _currentStep.Describe());
                var stepReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                void Handler(AccountTaskState resultingState, string message)
                {
                    // NOTE: true -> non-terminal state, false -> terminal state
                    stepReported.TrySetResult(ChangeState(resultingState, message));
                }

                _currentStep.Finished += Handler;
                try
                {
                    await _currentStep.PerformAsync(token).ConfigureAwait(false);
                    // WaitAsync: a step whose loop exits through its abort flag without
                    // reporting (upstream MSADeviceCodeStep expiry quirk) must not wedge the
                    // flow when the task is cancelled.
                    var nonTerminal = await stepReported.Task.WaitAsync(token).ConfigureAwait(false);
                    if (!nonTerminal)
                    {
                        break;
                    }
                }
                finally
                {
                    _currentStep.Finished -= Handler;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // an abort (or the caller's token) ended the step chain; the outcome below is the
            // flow's own, not the transport's TaskCanceledException
        }
        catch (OperationCanceledException e)
        {
            // a cancellation nobody asked for is a step failure: settle the account state the
            // way every other failure does, so the caller does not read it as still working
            ChangeState(AccountTaskState.FailedSoft, e.Message);
        }
        finally
        {
            _abortSource = null;
            _finished = true;
            _currentStep = null;
        }

        if (_terminalFailure is { } failure)
        {
            Failed?.Invoke();
            throw new InvalidOperationException(failure);
        }

        if (!_succeeded)
        {
            // the loop left without succeeding: an abort, or the caller's token
            Failed?.Invoke();
            throw new OperationCanceledException("Aborted", cancellationToken);
        }

        Succeeded?.Invoke();
    }

    /// <summary>Port of AuthFlow::abort(): stops the step in flight and the chain behind it.</summary>
    public void Abort()
    {
        _currentStep?.Abort();
        _abortSource?.Cancel();
    }

    private bool _succeeded;

    private void Succeed()
    {
        Data.Validity = Validity.Certain;
        _succeeded = true;
        ChangeState(AccountTaskState.Succeeded, "Finished all authentication steps");
    }

    private ILogger Log => Diagnostics.Log.For(_loggerFactory, LogCategories.Auth);

    private bool ChangeState(AccountTaskState newState, string reason = "")
    {
        _taskState = newState;
        switch (newState)
        {
            case AccountTaskState.Created:
                Log.LogDebug("auth flow created, waiting");
                Data.ErrorString = "";
                return true;
            case AccountTaskState.Working:
                if (_currentStep is null)
                {
                    Log.LogDebug("preparing to log in");
                }

                Data.AccountState = AccountState.Working;
                return true;
            case AccountTaskState.Succeeded:
                Log.LogInformation("authentication succeeded");
                Data.AccountState = AccountState.Online;
                return false;
            case AccountTaskState.Offline:
                Log.LogWarning("could not reach the authentication server: {Reason}", reason);
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Offline;
                _terminalFailure = reason;
                return false;
            case AccountTaskState.Disabled:
                Log.LogWarning("client id changed, a new session is required: {Reason}", reason);
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Disabled;
                _terminalFailure = reason;
                return false;
            case AccountTaskState.FailedSoft:
                Log.LogWarning("authentication failed: {Reason}", reason);
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Errored;
                _terminalFailure = reason;
                return false;
            case AccountTaskState.FailedHard:
                Log.LogWarning("authentication failed, the session has expired: {Reason}", reason);
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Expired;
                _terminalFailure = reason;
                return false;
            case AccountTaskState.FailedGone:
                Log.LogWarning("authentication failed, the account no longer exists: {Reason}", reason);
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Gone;
                _terminalFailure = reason;
                return false;
            default:
                var error = $"Unknown account task state: {(int)newState}";
                Log.LogError("{Error}: {Reason}", error, reason);
                Data.AccountState = AccountState.Errored;
                _terminalFailure = error;
                return false;
        }
    }
}
