# MAPPING — 原 C++ 文件 ↔ Flint C# 映射表

> 规则：每阶段推进时更新本表。状态：`ported` 完整移植（含测试）/ `partial` 部分移植 / `todo` 未开始 / `n/a` 不适用或被平台设施取代 / `GUI-bound` 不移植（RESEARCH.md §8）。
> C# 侧路径相对 `FlintLauncher/src/`。类型注释中必须标注移植来源。

## P0 已完成

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `buildconfig/BuildConfig.{h,cpp.in}` + CMake 默认值 | `Flint.Core/FlintConfig.cs` | ported | 常量按仓库 CMake 默认值固化；版本号改为 Flint 自己的 0.1.0；`LAUNCHER_CONFIGFILE`→`flint.cfg`（导入 prismlauncher.cfg 属 P7 DataMigrationTask） |

## P1 基础设施

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `Version.{h,cpp}` | `Flint.Core/Versioning/Version.cs` | todo | Minecraft 版本序比较；对拍 Version_test |
| `java/JavaVersion.{h,cpp}` | `Flint.Core/Java/JavaVersion.cs` | todo | |
| `FileSystem.{h,cpp}` | `Flint.Core/IO/FileSystem.cs` | todo | 原子写(PSaveFile)、路径规范化、copy/move 语义 |
| `GZip.{h,cpp}` | `Flint.Core/IO/GZip.cs` | todo | |
| `Json.{h,cpp}` | `Flint.Core/Json/JsonUtils.cs` | todo | Qt JSON 容错语义 → STJ + 容错读取助手 |
| `StringUtils.{h,cpp}` | `Flint.Core/Text/StringUtils.cs` | todo | |
| `Markdown.{h,cpp}` | `Flint.Core/Text/Markdown.cs` | todo | 仅 HTML 转换用途 |
| `MMCZip.{h,cpp}` | `Flint.Core/IO/ZipUtils.cs` | todo | ZipArchive；保留目录项/权限位 |
| `Commandline.{h,cpp}` | `Flint.Core/Text/Commandline.cs` | todo | |
| `settings/INIFile.{h,cpp}` | `Flint.Core/Settings/IniFile.cs` | todo | QSettings 兼容：读 legacy/1.1/1.2/1.3，写 1.3 |
| `settings/SettingsObject*` `Setting*` `INISettingsObject` `OverrideSetting` `PassthroughSetting` | `Flint.Core/Settings/*.cs` | todo | gate 覆盖语义 |
| `tasks/Task*` | `Flint.Core/Tasks/` | todo | async 化；进度/取消/失败聚合 |
| `net/*` | `Flint.Core/Net/` | todo | Request/Sink/Validator/HttpMetaCache/ApiHeader |
| `RWStorage.h` | `Flint.Core/RwStorage.cs` | todo | 或用 Channel/TPL 取代，记录决策 |
| `ExponentialSeries.h` `AssertHelpers.h` `Exception.h` `EnumWrapper.h` `QVariantUtils.h` `QObjectPtr.h` `Usable.h` `SeparatorPrefixTree.h` `Filter.h` `WatchLock.h` `KonamiCode` | 视需要内联或 `n/a` | todo | Qt 设施，多数被 C# 语言设施取代 |
| `MMCTime.{h,cpp}` | `Flint.Core/Time/MmcTime.cs` | todo | 若仅 GUI 使用则 n/a |

## P2 元数据与版本模型

| 原文件 | C# 目标 | 状态 |
|---|---|---|
| `meta/BaseEntity*` `Index` `VersionList` `Version` `JsonFormat*` | `Flint.Core/Meta/` | todo |
| `minecraft/MojangVersionFormat*` `OneSixVersionFormat*` | `Flint.Core/Minecraft/VersionFormats/` | todo |
| `minecraft/VersionFile*` `Library*` `GradleSpecifier.h` `MojangDownloadInfo.h` `Rule.*` `LaunchProfile*` | `Flint.Core/Minecraft/` | todo |
| `minecraft/Component*` `PackProfile*` `ComponentUpdateTask*` | `Flint.Core/Minecraft/Components/` | todo |
| `minecraft/VersionFilterData*` `ParseUtils.h` `AssetsUtils` | `Flint.Core/Minecraft/` | todo |
| `minecraft/MinecraftLoadAndCheck*` | `Flint.Core/Minecraft/` | todo |

## P3 实例域

| 原文件 | C# 目标 | 状态 |
|---|---|---|
| `libraries/libnbtplusplus` | `Flint.Nbt/`（新项目） | todo |
| `BaseInstance*` `NullInstance.h` `MinecraftInstance*`(minecraft/) | `Flint.Core/Instances/` | todo |
| `InstanceList*` `InstanceCopyPrefs*` `InstanceCopyTask*` `InstanceDirUpdate*` `InstanceTask*` `InstanceImportTask*` | `Flint.Core/Instances/` | todo |
| `VanillaInstanceCreationTask` `ProfileUtils*`(minecraft/) | `Flint.Core/Instances/Creation/` | todo |
| `minecraft/World*` `WorldList*` `WorldTasks*` | `Flint.Core/Minecraft/Worlds/` | todo |
| `minecraft/mod/*`（资源模型/解析：mod, resourcepack, texturepack, shaderpack, datapack, WorldSave） | `Flint.Core/Minecraft/Resources/` | todo |
| `minecraft/Op*`（gameoptions） | `Flint.Core/Minecraft/GameOptions/` | todo |
| `icons/IconList` `IconUtils`（文件管理部分） | `Flint.Core/Icons/` | todo |
| `RecursiveFileSystemWatcher*` `FileIgnoreProxy*` `FastFileIconProvider*` | `Flint.Core/IO/`（图标相关 GUI-bound） | todo |

## P4 账户

| 原文件 | C# 目标 | 状态 |
|---|---|---|
| `minecraft/auth/*`（AccountList/AccountData/MinecraftAccount/AuthFlow/steps/Parsers） | `Flint.Core/Auth/` | todo |
| `minecraft/skins/*`（SkinUpload/SkinDelete/CapeChange/SkinList） | `Flint.Core/Skins/` | todo |
| Katabasis(libraries) | `Flint.Core/Auth/OAuth/`（device-code + 授权码） | todo |

## P5 启动

| 原文件 | C# 目标 | 状态 |
|---|---|---|
| `launch/*`（LaunchTask/LaunchStep/TaskStepWrapper/LogModel/steps） | `Flint.Core/Launch/` | todo |
| `minecraft/launch/*`（各 MC 步骤 + MinecraftTarget） | `Flint.Core/Launch/Steps/` | todo |
| `LaunchController*` `LaunchMode.h` `LoggedProcess*` `JavaCommon*` | `Flint.Core/Launch/` | todo |
| `java/JavaUtils*` `JavaChecker*` `JavaInstallList*` `JavaInstall.h` `JavaMetadata*` `java/download/*` | `Flint.Core/Java/` | todo |
| `libraries/launcher`（NewLaunch java 源码） | 原样复用，编译 jar 内嵌资源 | todo |
| `MinecraftInstance::createLaunchTask/createLaunchScript/createEnvironment` | `Flint.Core/Instances/MinecraftInstance.Launch.cs` | todo |
| `logs/*` | `Flint.Core/Logs/` | todo |

## P6 modplatform

| 原文件 | C# 目标 | 状态 |
|---|---|---|
| `modplatform/ModIndex.h` `ResourceAPI.h` | `Flint.Core/ModPlatform/` | todo |
| `modplatform/modrinth/*` | `Flint.Core/ModPlatform/Modrinth/` | todo |
| `modplatform/flame/*` | `Flint.Core/ModPlatform/Flame/` | todo |
| `modplatform/packwiz/*` | `Flint.Core/ModPlatform/Packwiz/` | todo |
| `modplatform/atlauncher/*` | `Flint.Core/ModPlatform/AtLauncher/` | todo |
| `modplatform/ftb/*` `legacy_ftb/*` `import_ftb/*` | `Flint.Core/ModPlatform/Ftb*/` | todo |
| `modplatform/technic/*` | `Flint.Core/ModPlatform/Technic/` | todo |
| `modplatform/helpers/*` | `Flint.Core/ModPlatform/Helpers/` | todo |
| `libraries/murmur2` | `Flint.Core/Hashing/Murmur2.cs` | todo |
| `libraries/qdcss` | 待查用途后登记 | todo |
| `InstanceImportTask*`（modpack 分发） | P3 已列，此处补 provider 路由 | — |
| `ResourceDownloadTask*` | `Flint.Core/ModPlatform/` | todo |

## P7 周边

| 原文件 | C# 目标 | 状态 |
|---|---|---|
| `screenshots/*` | `Flint.Core/Screenshots/` | todo |
| `news/*` | `Flint.Core/News/` | todo |
| `net/PasteUpload*` | `Flint.Core/Net/PasteUpload.cs` | todo |
| `DataMigrationTask*` | `Flint.Core/Migration/DataMigration.cs` | todo |
| `tools/*` | `Flint.Core/Tools/` | todo |
| `translations/*` 下载逻辑 | 视需求 | todo |
| `updater/*` | GUI-bound / 进程外 | n/a |
| `Application*` `main.cpp` `ApplicationMessage*` `LocalPeer`(libraries) | `Flint.Web/Program.cs` + 宿主服务 | partial（P0 起） |
| `HardwareInfo*` `SysInfo*` | `Flint.Core/System/` | todo |
| `DesktopServices*` | n/a（打开文件/URL 语义按需暴露） | n/a |
| `ui/**` `icons`(渲染) `ui/themes` `translations`(UI) | — | GUI-bound |
| `filelink/*` | Windows 反射链接助手 | todo（P5+，可延后） |
| `macsandbox/*` | GUI-bound(macOS 沙箱) | n/a |

## 其他 libraries/

| 原 | 处置 | 状态 |
|---|---|---|
| `javacheck`(JavaCheck.jar 源码) | 原样复用，内嵌资源 | todo |
| `quazip` | System.IO.Compression 替代 | n/a |
| `tomlplusplus` | Tomlyn 或自写子集 | todo |
| `katabasis` | 自写 OAuth（P4） | n/a |
| `rainbow`（颜色工具） | GUI-bound | n/a |
| `QJsonModel` | GUI-bound | n/a |
| `qrcodegenerator` | device-code 登录二维码（API 场景可输出 URL，二维码前端事） | n/a（如需可在 Web 层用 SkiaSharp 另议） |
