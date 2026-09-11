// SPDX-License-Identifier: GPL-3.0-only
// Port of the ModPlatform::ModLoaderType subset of launcher/modplatform/ModIndex.h that the
// instance component stack (Component/PackProfile) needs. The rest of ModIndex (mod metadata
// scraping types) belongs to the modplatform layer and is not ported here.
namespace Flint.Core.Minecraft;

/// <summary>Port of ModPlatform::ModLoaderType; combinations mirror the QFlags ModLoaderTypes.</summary>
[Flags]
public enum ModLoaderType : ushort
{
    None = 0,
    NeoForge = 1 << 0,
    Forge = 1 << 1,
    Cauldron = 1 << 2,
    LiteLoader = 1 << 3,
    Fabric = 1 << 4,
    Quilt = 1 << 5,
    DataPack = 1 << 6,
    Babric = 1 << 7,
    BTA = 1 << 8,
    LegacyFabric = 1 << 9,
    Ornithe = 1 << 10,
    Rift = 1 << 11,
}

/// <summary>Port of the ModloaderMapEntry struct from launcher/minecraft/Component.h.</summary>
public readonly record struct ModloaderMapEntry(ModLoaderType Type, IReadOnlyList<string> KnownConflictingComponents);
