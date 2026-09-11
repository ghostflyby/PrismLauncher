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
| `Version.{h,cpp}` | `Flint.Core/Versioning/McVersion.cs` | ported | FlexVer + 空格特例；70 条官方向量全过 |
| `java/JavaVersion.{h,cpp}` | `Flint.Core/Java/JavaVersion.cs` | ported | |
| `StringUtils.{h,cpp}` | `Flint.Core/Text/StringUtils.cs` | partial | 仅 NaturalCompare（ordinal 代替 locale）；其余按需补 |
| `FileSystem.{h,cpp}` | `Flint.Core/IO/FileSystem.cs` | partial | 平台中立子集（copy/move/delete/combine/原子写等）；reflink/trash/desktop 等记录为 skip |
| `GZip.{h,cpp}` | `Flint.Core/IO/GZip.cs` | ported | System.IO.Compression |
| `Json.{h,cpp}` | `Flint.Core/Json/JsonUtils.cs` | partial | require/ensure 全家 + to/from list/map；QVariant 部分不适用 |
| `StringUtils`(其余) `Markdown.{h,cpp}` | `Flint.Core/Text/Markdown.cs` | ported | 手写 cmark 子集 |
| `Commandline.{h,cpp}` | `Flint.Core/Text/Commandline.cs` | ported | |
| `MMCZip.{h,cpp}` | `Flint.Core/IO/MMCZip.cs` | partial | extract/compress/merge；createModdedJar 待 Mod 模型；符号链接/权限位未实现 |
| `settings/INIFile.{h,cpp}` | `Flint.Core/Settings/IniFile.cs` + `QSettingsIniCodec.cs` | ported | 黄金对拍：真 QSettings 6.11 生成 golden 文件，读写字节级一致（tools/qt-ini-golden） |
| `settings/SettingsObject*` `Setting*` `INISettingsObject` `OverrideSetting` `PassthroughSetting` | `Flint.Core/Settings/*.cs` | ported | QVariant→object?+`Variant` 转换层 |
| `tasks/Task*` | `Flint.Core/Tasks/*`（Task→FlintTask 改名） | ported | ConcurrentTask 语义：子任务失败不中断兄弟、排空后聚合失败 |
| `net/Request*` | `Flint.Core/Net/NetRequest.cs` | partial | HttpClient 底座；429/503 自动重试；HeaderProxy 简化为 AddHeader |
| `net/Sink*` `FileSink` `ByteArraySink` `MetaCacheSink` | `Flint.Core/Net/Sink.cs` `FileSink.cs` | ported | PSaveFile→临时文件+原子替换 |
| `net/Validator*` `ChecksumValidator` | `Flint.Core/Net/Validator.cs` | ported | IncrementalHash |
| `net/HttpMetaCache*` | `Flint.Core/Net/HttpMetaCache.cs` | ported | metacache JSON 格式（version "1"）与原版互通 |
| `net/NetJob*` | `Flint.Core/Net/NetJob.cs` | ported | 共享静态 HttpClient |
| `net/Mode.h` `ApiRequest.h` `HeaderProxy*` | `Flint.Core/Net/Mode.cs`；头机制简化 | partial | 头注入按需在 P2/P6 恢复 |
| `net/PasteUpload*` | — | todo | P7 |
| `RWStorage.h` | — | n/a | 由 C# 并发设施取代 |
| `ExponentialSeries.h` `AssertHelpers.h` `Exception.h` `EnumWrapper.h` `QVariantUtils.h` `QObjectPtr.h` `Usable.h` `SeparatorPrefixTree.h` `Filter.h` `WatchLock.h` `KonamiCode` | `n/a`（Variant.cs 承接 QVariantUtils 子集） | n/a | Qt 设施，被 C# 语言设施取代 |
| `PSaveFile.h` | `Flint.Core/IO/FileSystem.cs`（原子写）+ `Net/FileSink.cs` | ported | |
| `MMCTime.{h,cpp}` | — | todo | 若仅 GUI 使用则 n/a |

## P2 元数据与版本模型（已完成 2026-09-11）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `meta/JsonFormat*` | `Flint.Core/Meta/MetaJsonFormat.cs` | ported | Require/RequireSet（uid 相等语义） |
| `meta/BaseEntity*` + BaseEntityLoadTask | `Flint.Core/Meta/MetaEntity.cs` | ported | sha256 校验、本地缓存优先、ParsingValidator 防坏提交；静态共享 HttpMetaCache(base "meta") |
| `meta/Index*` `VersionList*` `Version*` | `Flint.Core/Meta/Index.cs` `MetaVersionList.cs` `MetaVersion.cs` | ported | recommended/latest 逻辑 |
| `minecraft/GradleSpecifier.h` | `Flint.Core/Minecraft/GradleSpecifier.cs` | ported | readonly struct 保值拷贝语义 |
| `minecraft/Rule.*` `RuntimeContext.h` `SysInfo`(部分) | `Flint.Core/Minecraft/Rule.cs` `RuntimeContext.cs` | ported | |
| `minecraft/MojangDownloadInfo.h` | `Flint.Core/Minecraft/MojangDownloadInfo.cs` | ported | legacy 资产回退 URL 保留 |
| `minecraft/LaunchProfile*` | `Flint.Core/Minecraft/LaunchProfile.cs` | ported | 库冲突"高版本替换"语义 |
| `minecraft/VersionFilterData*` | `Flint.Core/Minecraft/VersionFilterData.cs` | ported | 表值逐一复制 |
| `minecraft/ParseUtils.h` | `Flint.Core/Minecraft/ParseUtils.cs` | ported | S3 时间戳 |
| `minecraft/Library*` | `Flint.Core/Minecraft/Library.cs` | ported | 构造期零网络（同上游）；此检出版本无 .pack.xz 逻辑（已核实） |
| `minecraft/VersionFile*` | `Flint.Core/Minecraft/VersionFile.cs` | ported | applyTo 合并含 net.minecraft 特例 |
| `minecraft/MojangVersionFormat*` `OneSixVersionFormat*` | 同名 .cs | ported | 上游已知 bug（缺空格错误串、jarMods→mods）保留并标注 |
| `minecraft/Component*` | `Flint.Core/Minecraft/Component.cs` | ported | UpdateAction variant → closed record set |
| `minecraft/PackProfile*` | `Flint.Core/Minecraft/PackProfile.cs` | ported | mmc-pack.json/patches 字节兼容（Qt 4 空格缩进+键排序）；GUI 模型部分不移植；IPackProfileEnv 解耦 |
| `minecraft/ComponentUpdateTask*` (+_p.h) | `Flint.Core/Minecraft/ComponentUpdateTask.cs` | ported | Launch/Resolution 模式、依赖注入、trivial-change 阶梯 |
| `minecraft/ProfileUtils*` | `Flint.Core/Minecraft/ProfileUtils.cs` | ported | readOverrideOrders/不抛异常 JSON 文件 IO |
| `minecraft/MinecraftLoadAndCheck*` `AssetsUtils` | — | todo | P3/P5（依赖 MinecraftInstance） |
| `minecraft/ProblemProvider.h` `Agent`(结构) `JavaMetadata`(解析半) | `Flint.Core/Minecraft/ProblemProvider.cs` `Agent.cs` `JavaMetadata.cs` | partial | JavaMetadata 下载半随 P5 java/ |

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
