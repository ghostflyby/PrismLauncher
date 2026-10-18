// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/Agent.h.

namespace FlintLauncher.Core.Minecraft;

public struct Agent(Library library, string argument)
{
    /// <summary>The library pointing to the jar this Java agent is contained within</summary>
    public Library Library = library;

    /// <summary>The argument to the Java agent, passed after an = if present</summary>
    public string Argument = argument;
}
