// SPDX-License-Identifier: GPL-3.0-only
// SPDX-FileCopyrightText: 2026 FlintLauncher Contributors
// SPDX-FileCopyrightText: 2022 Sefa Eyeoglu <contact@scrumplex.net>
// SPDX-FileCopyrightText: 2023 flowln <flowlnlnln@gmail.com>
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
// Port of the QVariant values stored by launcher/settings/INIFile (QSettings IniFormat subset).

namespace FlintLauncher.Core.Settings;

/// <summary>
/// A value in an INI file, mirroring the QVariant shapes QSettings' IniFormat can round-trip:
/// strings, string lists, byte arrays (@ByteArray), invalid (@Invalid) and opaque @-types
/// (@Variant/@Point/... which we keep verbatim so they round-trip byte-stably).
/// </summary>
public sealed class IniValue
{
    public enum Kind
    {
        Invalid,
        String,
        StringList,
        ByteArray,
        Raw,
    }

    private IniValue(Kind kind, string str, IReadOnlyList<string> list, byte[] bytes)
    {
        ValueKind = kind;
        String = str;
        List = list;
        Bytes = bytes;
    }

    public Kind ValueKind { get; }

    public string String { get; }

    public IReadOnlyList<string> List { get; }

    public byte[] Bytes { get; }

    public static readonly IniValue Invalid = new(Kind.Invalid, "", [], []);

    public static IniValue From(string? value) => new(Kind.String, value ?? "", [], []);

    public static IniValue From(IReadOnlyList<string> value) => new(Kind.StringList, "", value, []);

    public static IniValue From(byte[] value) => new(Kind.ByteArray, "", [], value);

    public static IniValue Raw(string? verbatim) => new(Kind.Raw, verbatim ?? "", [], []);

    public override string ToString() => ValueKind switch
    {
        Kind.String => String,
        Kind.StringList => string.Join(", ", List),
        Kind.ByteArray => Convert.ToBase64String(Bytes),
        Kind.Raw => String,
        _ => "",
    };
}
