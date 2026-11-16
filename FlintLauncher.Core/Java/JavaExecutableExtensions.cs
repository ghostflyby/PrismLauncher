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
                : Is64Bit(executable.Architecture) ? "64" : "32";
    }

    private static bool Is64Bit(string osArch) =>
        osArch is "x86_64" or "amd64" or "aarch64" or "arm64" or "riscv64" or "ppc64le" or "ppc64";
}
