# Third-party libraries

This folder has third-party or otherwise external libraries needed for other parts to work.

## javacheck

Simple Java tool that prints the JVM details - version and platform bitness.

Do what you want with it. It is so trivial that noone cares.

## launcher

Java launcher part for Minecraft.

It does the following:

- Waits for a launch script on stdin.
- Consumes the launch script you feed it.
- Proceeds with launch when it gets the `launcher` command.

If "abort" is sent, the process will exit.

This means the process is essentially idle until the final command is sent. You can, for example, attach a profiler before you send it.

The `standard` and `legacy` launchers are available.

- `standard` can handle launching any Minecraft version, at the cost of some extra features `legacy` enables (custom window icon and title).
- `legacy` is intended for use with Minecraft versions < 1.6 and is deprecated.

Example (some parts have been censored):

```text
mod legacyjavafixer-1.0
mainClass net.minecraft.launchwrapper.Launch
param --username
param CENSORED
param --version
param Prism Launcher
param --gameDir
param /home/peterix/minecraft/FTB/17ForgeTest/minecraft
param --assetsDir
param /home/peterix/minecraft/mmc5/assets
param --assetIndex
param 1.7.10
param --uuid
param CENSORED
param --accessToken
param CENSORED
param --userProperties
param {}
param --userType
param mojang
param --tweakClass
param cpw.mods.fml.common.launcher.FMLTweaker
windowTitle Prism Launcher: 172ForgeTest
windowParams 854x480
userName CENSORED
sessionId token:CENSORED:CENSORED
launcher standard
```

Available under `GPL-3.0-only` (with classpath exception), sublicensed from its original `Apache-2.0` codebase
