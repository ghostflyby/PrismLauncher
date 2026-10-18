// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/launch/steps/PrintServers.{h,cpp}: resolve the auth/session server host
// names and print the addresses once every lookup finished. QHostInfo::lookupHost maps to
// Dns.GetHostAddressesAsync; the in-order output and the N/A fallback are ported.

using System.Net;

namespace FlintLauncher.Core.Launch.Steps;

public sealed class PrintServers(LaunchTask parent, IReadOnlyList<string> servers) : LaunchStep(parent)
{
    private readonly List<string> _servers = [.. servers];
    private readonly Dictionary<string, string> _serverToAddress = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (var server in _servers)
        {
            string addresses;
            try
            {
                var hostAddresses = await Dns.GetHostAddressesAsync(server, cancellationToken).ConfigureAwait(false);
                addresses = server + " resolves to:\n  ";
                if (hostAddresses.Length != 0)
                {
                    for (var i = 0; i < hostAddresses.Length; i++)
                    {
                        addresses += hostAddresses[i].ToString();
                        if (i != hostAddresses.Length - 1)
                        {
                            addresses += ", ";
                        }
                    }
                }
                else
                {
                    addresses += "N/A";
                }

                addresses += "\n";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                addresses = server + " resolves to:\n  N/A\n";
            }

            _serverToAddress[server] = addresses;
        }

        // print server info in order once all servers are resolved
        foreach (var server in _servers)
        {
            EmitLogLine(_serverToAddress.GetValueOrDefault(server, ""), MessageLevel.Launcher);
        }

        EmitSucceeded();
    }

    public override bool CanAbort() => true;
}
