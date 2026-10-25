// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022-2026 Prism Launcher Contributors
//
// Port of launcher/minecraft/auth/AuthFlow.{h,cpp} on top of FlintTask.
//
// The Qt signal chain (AuthStep::finished -> AuthFlow::stepFinished -> nextStep) becomes an
// await loop: each step runs to its Finished report inside ExecuteAsync; a Working report
// continues the chain, every other state is terminal through changeState(). abort() cancels the
// in-flight step (the cancellation surfaces as OperationCanceledException and maps to the
// FlintTask aborted state, matching the C++ emitAborted() + tr("Aborted") path).

using FlintLauncher.Core.Tasks;

namespace FlintLauncher.Core.Auth;

public enum AuthFlowAction
{
    Refresh,
    Login,
    DeviceCode,
}

public sealed class AuthFlow : FlintTask
{
    private AccountTaskState _taskState = AccountTaskState.Created;
    private readonly List<AuthStep> _steps = [];
    private AuthStep? _currentStep;

    public AuthFlow(AccountData data, AuthFlowAction action = AuthFlowAction.Refresh,
        AuthEndpointsOptions? auth = null)
    {
        Data = data;
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

    /// <summary>Port of the authorizeWithBrowser(QUrl) signal (interactive login).</summary>
    public event Action<string>? AuthorizeWithBrowser;

    /// <summary>Port of the authorizeWithBrowserWithExtra(QString, QString, int) signal (device code).</summary>
    public event Action<string, string, int>? AuthorizeWithBrowserWithExtra;

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        ChangeState(AccountTaskState.Working, "Initializing");
        while (IsRunning)
        {
            if (_steps.Count == 0)
            {
                // we got to the end without an incident... assume this is all.
                _currentStep = null;
                Succeed();
                return;
            }

            _currentStep = _steps[0];
            _steps.RemoveAt(0);
            EmitStatus(_currentStep.Describe());
            var stepReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(AccountTaskState resultingState, string message)
            {
                // NOTE: true -> non-terminal state, false -> terminal state
                stepReported.TrySetResult(ChangeState(resultingState, message));
            }

            _currentStep.Finished += Handler;
            try
            {
                await _currentStep.PerformAsync(cancellationToken).ConfigureAwait(false);
                // WaitAsync: a step whose loop exits through its abort flag without reporting
                // (upstream MSADeviceCodeStep expiry quirk) must not wedge the flow when the
                // task is cancelled — the cancellation surfaces as the aborted state.
                var nonTerminal = await stepReported.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!nonTerminal)
                {
                    return;
                }
            }
            finally
            {
                _currentStep.Finished -= Handler;
            }
        }
    }

    public override void Abort()
    {
        _currentStep?.Abort();
        base.Abort();
    }

    private void Succeed()
    {
        Data.Validity = Validity.Certain;
        ChangeState(AccountTaskState.Succeeded, "Finished all authentication steps");
    }

    private bool ChangeState(AccountTaskState newState, string reason = "")
    {
        _taskState = newState;
        EmitDetails(reason);
        switch (newState)
        {
            case AccountTaskState.Created:
                EmitStatus("Waiting...");
                Data.ErrorString = "";
                return true;
            case AccountTaskState.Working:
                if (_currentStep is null)
                {
                    EmitStatus("Preparing to log in...");
                }

                Data.AccountState = AccountState.Working;
                return true;
            case AccountTaskState.Succeeded:
                EmitStatus("Authentication task succeeded.");
                Data.AccountState = AccountState.Online;
                EmitSucceeded();
                return false;
            case AccountTaskState.Offline:
                EmitStatus("Failed to contact the authentication server.");
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Offline;
                EmitFailed(reason);
                return false;
            case AccountTaskState.Disabled:
                EmitStatus("Client ID has changed. New session needs to be created.");
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Disabled;
                EmitFailed(reason);
                return false;
            case AccountTaskState.FailedSoft:
                EmitStatus("Encountered an error during authentication.");
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Errored;
                EmitFailed(reason);
                return false;
            case AccountTaskState.FailedHard:
                EmitStatus("Failed to authenticate. The session has expired.");
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Expired;
                EmitFailed(reason);
                return false;
            case AccountTaskState.FailedGone:
                EmitStatus("Failed to authenticate. The account no longer exists.");
                Data.ErrorString = reason;
                Data.AccountState = AccountState.Gone;
                EmitFailed(reason);
                return false;
            default:
                EmitStatus("...");
                var error = $"Unknown account task state: {(int)newState}";
                Data.AccountState = AccountState.Errored;
                EmitFailed(error);
                return false;
        }
    }
}
