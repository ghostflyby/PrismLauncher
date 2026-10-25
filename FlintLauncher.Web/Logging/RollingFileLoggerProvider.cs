// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2022 Lenny McLennington <lenny@sneed.church>
// SPDX-FileCopyrightText: 2022 Tayou <git@tayou.org>
// SPDX-FileCopyrightText: 2023 TheKodeToad <TheKodeToad@proton.me>
// SPDX-FileCopyrightText: 2023 Rachel Powers <508861+Ryex@users.noreply.github.com>
//
// This file incorporates work covered by the following copyright and
// permission notice:
//
//     Copyright 2013-2021 MultiMC Contributors
//
//     Licensed under the Apache License, Version 2.0 (the "License");
//     you may not use this file except in compliance with the License.
//     You may obtain a copy of the License at
//
//         http://www.apache.org/licenses/LICENSE-2.0
//
//     Unless required by applicable law or agreed to in writing, software
//     distributed under the License is distributed on an "AS IS" BASIS,
//     WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//     See the License for the specific language governing permissions and
//     limitations under the License.
//
// Port of the rolling log file sink of Application::init (Application.cpp:513-528): at
// every process start the previous generations shift down (Flint-3.log -> Flint-4.log ...
// Flint-0.log -> Flint-1.log) and a fresh Flint-0.log is opened — one generation per run,
// <Generations> kept. Every line is flushed (the C++ flushes per message too). An
// unwritable directory must not take the launcher down: the C++ shows a fatal dialog, the
// port degrades to console-only logging.

using System.Globalization;
using System.Security;
using FlintLauncher.Core.IO;
using TruePath.SystemIo;

namespace FlintLauncher.Web.Logging;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const int Generations = 5;

    private readonly StreamWriter? _writer;
    private readonly object _lock = new();

    public RollingFileLoggerProvider(MaterializedDirectory directory)
    {
        try
        {
            for (var generation = Generations - 2; generation >= 0; generation--)
            {
                var source = directory.Path / FileName(generation);
                if (!source.ExistsFile())
                {
                    continue;
                }

                source.Move(directory.Path / FileName(generation + 1), overwrite: true);
            }

            _writer = new StreamWriter(
                (directory.Path / FileName(0)).Open(FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or SecurityException or NotSupportedException)
        {
            _writer = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
        }
    }

    private static string FileName(int generation) => $"Flint-{generation}.log";

    private sealed class Writer(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var writer = provider._writer;
            if (writer is null || !IsEnabled(logLevel))
            {
                return;
            }

            var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} " +
                       $"[{LevelName(logLevel)}] [{category}] {formatter(state, exception)}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (provider._lock)
            {
                writer.WriteLine(line);
            }
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
