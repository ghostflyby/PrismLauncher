// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors

using JdkFind;

namespace FlintLauncher.Core.Java;

internal static class JavaExecutableExtensions
{
    extension(JavaExecutable executable)
    {
        /// <summary>The Mojang legacy platform string ("32"/"64") derived from the
        /// reported os.arch; empty for an unprobed binary — never guessed as 32-bit.</summary>
        public string MojangArchitecture =>
            executable.Architecture.Length == 0
                ? ""
                : JavaUtils.Is64BitArchitecture(executable.Architecture) ? "64" : "32";
    }
}
