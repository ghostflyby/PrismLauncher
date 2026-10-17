// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/launch/MinecraftTarget.{h,cpp}: the "join server / world on
// launch" target representation.
//
// P5 reconciliation: the C++ type is a mutable struct shared through MinecraftTarget::Ptr —
// the LookupServerAddress step writes the resolved address/port back through the pointer and
// LauncherPartLaunch reads them afterwards. The draft `record` was immutable, so the port is
// now a plain mutable class matching the C++ shape (the parse logic is unchanged).

using System.Globalization;

namespace FlintLauncher.Core.Instances;

public sealed class MinecraftTarget
{
    public MinecraftTarget()
    {
    }

    public MinecraftTarget(string address, ushort port, string world)
    {
        Address = address;
        Port = port;
        World = world;
    }

    public string Address { get; set; } = "";

    public ushort Port { get; set; }

    public string World { get; set; } = "";

    public static MinecraftTarget Parse(string fullAddress, bool useWorld)
    {
        if (useWorld)
        {
            // C++ default-constructs the target (port 0) and only fills the world
            return new MinecraftTarget("", 0, fullAddress);
        }

        var split = fullAddress.Split(':').ToList();

        // The logic below replicates the exact logic minecraft uses for parsing server addresses.
        // While the conversion is not lossless and eats errors, it ensures the same behavior
        // within Minecraft and Prism Launcher when entering server addresses.
        if (fullAddress.StartsWith("[", StringComparison.Ordinal))
        {
            var bracket = fullAddress.IndexOf(']');
            if (bracket > 0)
            {
                var ipv6 = fullAddress[1..bracket];
                var port = fullAddress[(bracket + 1)..].Trim();

                if (port.StartsWith(":", StringComparison.Ordinal) && ipv6.Length != 0)
                {
                    split = [ipv6, port[1..]];
                }
                else
                {
                    split = [ipv6];
                }
            }
        }

        if (split.Count > 2)
        {
            split = [fullAddress];
        }

        var realAddress = split[0];

        ushort realPort = 25565;
        if (split.Count > 1)
        {
            // QString::toUInt: parsed into an uint first, then truncated to quint16 by the
            // assignment (e.g. "70000" wraps around instead of falling back to 25565)
            if (uint.TryParse(split[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                realPort = (ushort)parsed;
            }
        }

        return new MinecraftTarget(realAddress, realPort, "");
    }
}
