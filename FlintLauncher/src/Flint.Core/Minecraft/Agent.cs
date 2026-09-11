// SPDX-License-Identifier: GPL-3.0-only
// Port of launcher/minecraft/Agent.h.
namespace Flint.Core.Minecraft;

public struct Agent
{
    public Agent(Library library, string argument)
    {
        Library = library;
        Argument = argument;
    }

    /// <summary>The library pointing to the jar this Java agent is contained within</summary>
    public Library Library;

    /// <summary>The argument to the Java agent, passed after an = if present</summary>
    public string Argument;
}
