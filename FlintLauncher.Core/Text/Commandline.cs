// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
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
// Port of launcher/Commandline.{h,cpp}: shell-like argv splitting and $VAR/${VAR}
// expansion (originally by Orochimarufan, MultiMC contributors).

using System.Text;

namespace FlintLauncher.Core.Text;

public static class Commandline
{
    /// <summary>
    /// Port of Commandline::splitArgs: split a string into argv items like a shell would.
    /// Quotes (single and double) group words; backslash escapes the next character inside
    /// quotes; unquoted text is split on whitespace.
    /// </summary>
    public static List<string> SplitArgs(string args)
    {
        var argv = new List<string>();
        var current = new StringBuilder();
        var escape = false;
        char inquotes = '\0';
        foreach (var cchar in args)
        {
            if (escape)
            {
                // \ escaped
                current.Append(cchar);
                escape = false;
            }
            else if (inquotes != '\0')
            {
                // in "quotes"
                if (cchar == '\\')
                {
                    escape = true;
                }
                else if (cchar == inquotes)
                {
                    inquotes = '\0';
                }
                else
                {
                    current.Append(cchar);
                }
            }
            else
            {
                // otherwise
                if (char.IsWhiteSpace(cchar))
                {
                    if (current.Length != 0)
                    {
                        argv.Add(current.ToString());
                        current.Clear();
                    }
                }
                else if (cchar == '"' || cchar == '\'')
                {
                    inquotes = cchar;
                }
                else
                {
                    current.Append(cchar);
                }
            }
        }

        if (current.Length != 0)
        {
            argv.Add(current.ToString());
        }

        return argv;
    }

    /// <summary>
    /// Port of Commandline::expandVariables: expand $VAR and ${VAR} like a shell would,
    /// using <paramref name="dict"/> (the QProcessEnvironment equivalent). Variables that
    /// are missing or empty stay untouched.
    /// </summary>
    public static string ExpandVariables(string input, IReadOnlyDictionary<string, string> dict)
    {
        var result = new StringBuilder(input);

        const int @base = 0;
        const int maybeBrace = 1;
        const int variable = 2;
        const int brace = 3;
        var state = @base;
        var startIdx = -1;
        for (var i = 0; i < result.Length;)
        {
            var c = result[i++];
            switch (state)
            {
                case @base:
                    if (c == '$')
                    {
                        state = maybeBrace;
                    }

                    break;
                case maybeBrace:
                    if (c == '{')
                    {
                        state = brace;
                        startIdx = i;
                    }
                    else if (char.IsLetterOrDigit(c) || c == '_')
                    {
                        state = variable;
                        startIdx = i - 1;
                    }
                    else
                    {
                        state = @base;
                    }

                    break;
                case brace:
                    if (c == '}')
                    {
                        var res = Lookup(dict, result.ToString(startIdx, i - 1 - startIdx));
                        if (res.Length != 0)
                        {
                            Replace(result, startIdx - 2, i - startIdx + 2, res);
                            i = startIdx - 2 + res.Length;
                        }

                        state = @base;
                    }

                    break;
                case variable:
                    if (!char.IsLetterOrDigit(c) && c != '_')
                    {
                        var res = Lookup(dict, result.ToString(startIdx, i - startIdx - 1));
                        if (res.Length != 0)
                        {
                            Replace(result, startIdx - 1, i - startIdx, res);
                            i = startIdx - 1 + res.Length;
                        }

                        state = @base;
                    }

                    break;
            }
        }

        if (state == variable)
        {
            var res = Lookup(dict, result.ToString(startIdx, result.Length - startIdx));
            if (res.Length != 0)
            {
                Replace(result, startIdx - 1, result.Length - startIdx + 1, res);
            }
        }

        return result.ToString();
    }

    /// <summary>QProcessEnvironment::value(key, "") equivalent.</summary>
    private static string Lookup(IReadOnlyDictionary<string, string> dict, string key)
    {
        return dict.GetValueOrDefault(key, "");
    }

    /// <summary>QString::replace(pos, n, after) equivalent.</summary>
    private static void Replace(StringBuilder sb, int start, int count, string replacement)
    {
        sb.Remove(start, count);
        sb.Insert(start, replacement);
    }

    /// <summary>
    /// Port of Commandline::process: split a commandline string into arguments, expanding
    /// variables in each argument.
    /// </summary>
    public static List<string> Process(string cmd, IReadOnlyDictionary<string, string>? dict = null)
    {
        dict ??= new Dictionary<string, string>();
        var splited = SplitArgs(cmd);
        for (var i = 0; i < splited.Count; i++)
        {
            splited[i] = ExpandVariables(splited[i], dict);
        }

        return splited;
    }
}
