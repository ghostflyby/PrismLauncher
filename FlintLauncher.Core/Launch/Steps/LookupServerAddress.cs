// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/steps/LookupServerAddress.{h,cpp}: resolve the "join server on
// launch" target through a _minecraft._tcp SRV lookup before the address is handed to the
// game. The QDnsLookup usage is replaced by the minimal SrvResolver; failure semantics match
// the C++ exactly — a failed lookup is NOT fatal, the launch continues with the original
// address and the default port and Minecraft itself may still connect (or not).

using FlintLauncher.Core.Instances;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class LookupServerAddress(LaunchTask parent, MinecraftTarget output) : LaunchStep(parent)
{
    public string LookupAddress { get; init; } = "";

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var dnsName = $"_minecraft._tcp.{LookupAddress}";
        SrvRecord? record;
        try
        {
            record = await SrvResolver.QueryFirstAsync(dnsName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            EmitLogLine($"Failed to resolve server address (this is NOT an error!) {dnsName}: {e.Message}\n",
                MessageLevel.Launcher);
            Resolve(LookupAddress, 25565); // Technically the task failed, however, we don't abort the launch
            return;                        // and leave it up to minecraft to fail (or maybe not) when connecting
        }

        if (record is null)
        {
            EmitLogLine($"Failed to resolve server address {dnsName}: the DNS lookup succeeded, but no records were returned.\n",
                MessageLevel.Warning);
            Resolve(LookupAddress, 25565);
            return;
        }

        EmitLogLine($"Resolved server address {dnsName} to {record.Target} with port {record.Port}\n",
            MessageLevel.Launcher);
        Resolve(record.Target, record.Port);
    }

    private void Resolve(string address, ushort port)
    {
        output.Address = address;
        output.Port = port;
        EmitSucceeded();
    }

    public override bool CanAbort() => true;

    public override bool AbortStep()
    {
        EmitAborted();
        return true;
    }
}
