# MAPPING — 原 C++ 文件 ↔ Flint C# 映射表

> 规则：每阶段推进时更新本表。状态：`ported` 完整移植（含测试）/ `partial` 部分移植 / `todo` 未开始 / `n/a` 不适用或被平台设施取代 / `GUI-bound` 不移植（RESEARCH.md §8）。
> C# 侧路径相对仓库顶级项目目录（如 `FlintLauncher.Core/`）。类型注释中必须标注移植来源。

## P0 已完成

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `buildconfig/BuildConfig.{h,cpp.in}` + CMake 默认值 | `FlintLauncher.Core/FlintConfig.cs` | ported | 常量按仓库 CMake 默认值固化；版本号改为 Flint 自己的 0.1.0；`LAUNCHER_CONFIGFILE`→`flint.cfg`（导入 prismlauncher.cfg 属 P7 DataMigrationTask） |

## P1 基础设施

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `Version.{h,cpp}` | `FlintLauncher.Core/Versioning/McVersion.cs` | ported | FlexVer + 空格特例；70 条官方向量全过 |
| `java/JavaVersion.{h,cpp}` | `FlintLauncher.Core/Java/JavaVersion.cs` | ported | |
| `StringUtils.{h,cpp}` | `FlintLauncher.Core/Text/StringUtils.cs` | partial | 仅 NaturalCompare（ordinal 代替 locale）；其余按需补 |
| `FileSystem.{h,cpp}` | `FlintLauncher.Core/IO/FileSystem.cs` | partial | 平台中立子集（copy/move/delete/combine/原子写等）；reflink/trash/desktop 等记录为 skip |
| `GZip.{h,cpp}` | `FlintLauncher.Core/IO/GZip.cs` | ported | System.IO.Compression |
| `Json.{h,cpp}` | `FlintLauncher.Core/Json/JsonUtils.cs` | partial | require/ensure 全家 + to/from list/map；QVariant 部分不适用 |
| `StringUtils`(其余) `Markdown.{h,cpp}` | `FlintLauncher.Core/Text/Markdown.cs` | ported | 手写 cmark 子集 |
| `Commandline.{h,cpp}` | `FlintLauncher.Core/Text/Commandline.cs` | ported | |
| `MMCZip.{h,cpp}` | `FlintLauncher.Core/IO/MMCZip.cs` | partial | extract/compress/merge；createModdedJar 待 Mod 模型；符号链接/权限位未实现 |
| `settings/INIFile.{h,cpp}` | `FlintLauncher.Core/Settings/IniFile.cs` + `QSettingsIniCodec.cs` | ported | 黄金对拍：真 QSettings 6.11 生成 golden 文件，读写字节级一致（docs/tools/qt-ini-golden） |
| `settings/SettingsObject*` `Setting*` `INISettingsObject` `OverrideSetting` `PassthroughSetting` | `FlintLauncher.Core/Settings/*.cs` | ported | QVariant→object?+`Variant` 转换层 |
| `tasks/Task*` | `FlintLauncher.Core/Tasks/*`（Task→FlintTask 改名） | ported | ConcurrentTask 语义：子任务失败不中断兄弟、排空后聚合失败 |
| `net/Request*` | `FlintLauncher.Core/Net/NetRequest.cs` | partial | HttpClient 底座；429/503 自动重试；HeaderProxy 简化为 AddHeader |
| `net/Sink*` `FileSink` `ByteArraySink` `MetaCacheSink` | `FlintLauncher.Core/Net/Sink.cs` `FileSink.cs` | ported | PSaveFile→临时文件+原子替换 |
| `net/Validator*` `ChecksumValidator` | `FlintLauncher.Core/Net/Validator.cs` | ported | IncrementalHash |
| `net/HttpMetaCache*` | `FlintLauncher.Core/Net/HttpMetaCache.cs` | ported | metacache JSON 格式（version "1"）与原版互通 |
| `net/NetJob*` | `FlintLauncher.Core/Net/NetJob.cs` | ported | 共享静态 HttpClient |
| `net/Mode.h` `ApiRequest.h` `HeaderProxy*` | `FlintLauncher.Core/Net/Mode.cs`；头机制简化 | partial | 头注入按需在 P2/P6 恢复 |
| `net/PasteUpload*` | — | todo | P7 |
| `RWStorage.h` | — | n/a | 由 C# 并发设施取代 |
| `ExponentialSeries.h` `AssertHelpers.h` `Exception.h` `EnumWrapper.h` `QVariantUtils.h` `QObjectPtr.h` `Usable.h` `SeparatorPrefixTree.h` `Filter.h` `WatchLock.h` `KonamiCode` | `n/a`（Variant.cs 承接 QVariantUtils 子集） | n/a | Qt 设施，被 C# 语言设施取代 |
| `PSaveFile.h` | `FlintLauncher.Core/IO/FileSystem.cs`（原子写）+ `Net/FileSink.cs` | ported | |
| `MMCTime.{h,cpp}` | — | todo | 若仅 GUI 使用则 n/a |

## P2 元数据与版本模型（已完成 2026-09-11）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `meta/JsonFormat*` | `FlintLauncher.Core/Meta/MetaJsonFormat.cs` | ported | Require/RequireSet（uid 相等语义） |
| `meta/BaseEntity*` + BaseEntityLoadTask | `FlintLauncher.Core/Meta/MetaEntity.cs` | ported | sha256 校验、本地缓存优先、ParsingValidator 防坏提交；静态共享 HttpMetaCache(base "meta") |
| `meta/Index*` `VersionList*` `Version*` | `FlintLauncher.Core/Meta/Index.cs` `MetaVersionList.cs` `MetaVersion.cs` | ported | recommended/latest 逻辑 |
| `minecraft/GradleSpecifier.h` | `FlintLauncher.Core/Minecraft/GradleSpecifier.cs` | ported | readonly struct 保值拷贝语义 |
| `minecraft/Rule.*` `RuntimeContext.h` `SysInfo`(部分) | `FlintLauncher.Core/Minecraft/Rule.cs` `RuntimeContext.cs` | ported | |
| `minecraft/MojangDownloadInfo.h` | `FlintLauncher.Core/Minecraft/MojangDownloadInfo.cs` | ported | legacy 资产回退 URL 保留 |
| `minecraft/LaunchProfile*` | `FlintLauncher.Core/Minecraft/LaunchProfile.cs` | ported | 库冲突"高版本替换"语义 |
| `minecraft/VersionFilterData*` | `FlintLauncher.Core/Minecraft/VersionFilterData.cs` | ported | 表值逐一复制 |
| `minecraft/ParseUtils.h` | `FlintLauncher.Core/Minecraft/ParseUtils.cs` | ported | S3 时间戳 |
| `minecraft/Library*` | `FlintLauncher.Core/Minecraft/Library.cs` | ported | 构造期零网络（同上游）；此检出版本无 .pack.xz 逻辑（已核实） |
| `minecraft/VersionFile*` | `FlintLauncher.Core/Minecraft/VersionFile.cs` | ported | applyTo 合并含 net.minecraft 特例 |
| `minecraft/MojangVersionFormat*` `OneSixVersionFormat*` | 同名 .cs | ported | 上游已知 bug（缺空格错误串、jarMods→mods）保留并标注 |
| `minecraft/Component*` | `FlintLauncher.Core/Minecraft/Component.cs` | ported | UpdateAction variant → closed record set |
| `minecraft/PackProfile*` | `FlintLauncher.Core/Minecraft/PackProfile.cs` | ported | mmc-pack.json/patches 字节兼容（Qt 4 空格缩进+键排序）；GUI 模型部分不移植；IPackProfileEnv 解耦 |
| `minecraft/ComponentUpdateTask*` (+_p.h) | `FlintLauncher.Core/Minecraft/ComponentUpdateTask.cs` | ported | Launch/Resolution 模式、依赖注入、trivial-change 阶梯 |
| `minecraft/ProfileUtils*` | `FlintLauncher.Core/Minecraft/ProfileUtils.cs` | ported | readOverrideOrders/不抛异常 JSON 文件 IO |
| `minecraft/MinecraftLoadAndCheck*` `AssetsUtils` | — | todo | P3/P5（依赖 MinecraftInstance） |
| `minecraft/ProblemProvider.h` `Agent`(结构) `JavaMetadata`(解析半) | `FlintLauncher.Core/Minecraft/ProblemProvider.cs` `Agent.cs` `JavaMetadata.cs` | partial | JavaMetadata 下载半随 P5 java/ |

## P3 实例域（已完成 2026-09-12）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `libraries/libnbtplusplus`（launcher 用子集） | `FlintLauncher.Nbt/` | ported | 全 Tag 集、Java modified-UTF-8、gzip/raw 自检、插入序 Compound |
| `BaseInstance*` `NullInstance.h` | `FlintLauncher.Core/Instances/BaseInstance.cs` `NullInstance.cs` | ported | 设置注册/游玩时长/快捷方式/链接实例；Qt 信号→事件 |
| `minecraft/MinecraftInstance*` | `FlintLauncher.Core/Instances/MinecraftInstance.cs` | ported | 全部设置组与路径访问器；launch 管线 TODO(P5) |
| `InstanceList*` | `FlintLauncher.Core/Instances/InstanceList.cs` | ported | instgroups.json 往返、staged commit；QAbstractListModel/Watcher 不移植 |
| `InstanceCopyPrefs*` `InstanceCopyTask*` `InstanceTask*` | `FlintLauncher.Core/Instances/InstanceCopy*.cs` `InstanceTask.cs` | ported | link/clone 传输清晰报错（上游 create_link/clone 未移植） |
| `InstanceImportTask*` | — | todo | P6（依赖 modplatform 分发） |
| `InstanceDirUpdate*` | — | n/a | GUI 对话框耦合；纯文件半已在 BaseInstance/InstanceList |
| `VanillaInstanceCreationTask`(minecraft/) | `FlintLauncher.Core/Instances/Creation/VanillaInstanceCreationTask.cs` | ported | mmc-pack/instance.cfg 字节兼容 |
| `ProfileUtils*`(minecraft/) | `FlintLauncher.Core/Minecraft/ProfileUtils.cs` | ported | P2b 已完成；此检出版本无 legacy 导入半 |
| `minecraft/World*` `WorldList*` `WorldTasks*` | `FlintLauncher.Core/Minecraft/Worlds/` | ported | Qt model 不移植；World::install 上游反向检查 bug 已修 |
| `LocalWorldSaveParseTask` | `Worlds/WorldSaveIdentification.cs` | ported | |
| `minecraft/mod/*`（资源模型与解析） | `FlintLauncher.Core/Minecraft/Resources/` | ported | pack_format 表全量；QImage→PNG IHDR 读取（已记录偏差） |
| `minecraft/Op*`（gameoptions） | — | todo | P7 |
| `icons/IconList` `IconUtils`（文件部分） | `FlintLauncher.Core/Icons/IconFiles.cs` | ported | Qt 模型/主题不移植 |
| `RecursiveFileSystemWatcher*` `FileIgnoreProxy*` `FastFileIconProvider*` | — | n/a | GUI/Qt watcher |

## P4 账户（已完成 2026-09-12）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `minecraft/auth/AccountData*` | `FlintLauncher.Core/Auth/AccountData.cs` | ported | v3 键布局逐字节对齐；legacy 迁移与 entitlement 回退 |
| `minecraft/auth/AccountList*` | `FlintLauncher.Core/Auth/AccountList.cs` | ported | accounts.json 与真实文件互通（只读 smoke 实测） |
| `minecraft/auth/MinecraftAccount*` `AuthFlow*` `AuthStep.h` `AuthSession*` | `Auth/MinecraftAccount.cs` `AuthFlow.cs` `AuthStep.cs`；AuthSession 在 Instances/（P3c1 已对齐） | ported | |
| `minecraft/auth/steps/*` | `Auth/` 各 Step | ported | 端点经 AuthEndpoints 可注入（默认值与上游逐字一致）；device-code 轮询/XErr 阶梯逐条移植 |
| `minecraft/auth/Parsers*` | `Auth/Parsers.cs` | ported | |
| `minecraft/skins/*` | `FlintLauncher.Core/Skins/` | partial | SkinUpload/SkinDelete/CapeChange/SkinModel 持久化半；Qt 模型与预览不移植 |
| Katabasis(libraries) | `Auth/MsaInteractiveStep.cs`（回环回调+换码）+ `MSADeviceCodeStep.cs` | ported | 自定义协议回退处理延后 |
| 后台 token 刷新调度（QTimer 循环） | — | todo | P5（LaunchController 消费） |

## P5 启动（已完成 2026-09-12）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `launch/*`（LaunchTask/LaunchStep/TaskStepWrapper/LogModel/steps） | `FlintLauncher.Core/Launch/` | ported | proceed 双门（ReadyForLaunch+RequestProgress）语义保留 |
| `minecraft/launch/*`（全部 23 步骤 + MinecraftTarget） | `FlintLauncher.Core/Launch/Steps/` | ported | 含 ExtractNatives/ReconstructAssets/ModMinecraftJar(createModdedJar)/AssetUpdate |
| `LaunchController*` `LaunchMode.h` `LoggedProcess*` `JavaCommon*` | `FlintLauncher.Core/Launch/` | ported | 无头扩展：WantedLaunchMode=Offline 直达离线会话（已记录） |
| `java/JavaUtils*` `JavaChecker*` `JavaInstallList*` `JavaInstall.h` `JavaMetadata*` `java/download/*` | `FlintLauncher.Core/Java/` | ported | macOS 发现实测；tar.gz 自研 ustar；Windows 注册表跳过 |
| `libraries/launcher` + `javacheck` | `docs/tools/newlaunch-build.sh` → build/jars/*.jar | ported | javac --release 8；产物不入库 |
| `MinecraftInstance::createLaunchTask/createLaunchScript/createEnvironment` | `FlintLauncher.Core/Instances/MinecraftInstance.cs` | ported | 全链冒烟通过（fake-java + stdin 协议 + PostExit） |
| `logs/*` `MessageLevel*` | `FlintLauncher.Core/Launch/LogModel.cs` `MessageLevel.cs` | ported | Log4j XML 解析未移植 |
| `net/PasteUpload*`、SRV | SrvResolver.cs 已移植；PasteUpload | partial | PasteUpload 归 P7 |

## P6 modplatform（已完成 2026-09-12）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `modplatform/ModIndex.h` `ResourceAPI.h` | `ModPlatform/ModIndex.cs` `ResourceAPI.cs` | ported | |
| `modplatform/modrinth/*` | `ModPlatform/Modrinth/*` | ported | 搜索/版本/按哈希更新/.mrpack 导入导出往返 |
| `modplatform/flame/*` | `ModPlatform/Flame/*` | ported | 指纹批量解析、blocked-mods 数据语义、导入导出往返；API key 用户自备 |
| `modplatform/packwiz/*` + MetadataHandler | `ModPlatform/Packwiz/*` `MetadataHandler.cs` `EnsureMetadataTask.cs` | ported | 自研 MiniToml 子集（上游 Packwiz_test 原样通过） |
| `modplatform/atlauncher/*` | `ModPlatform/AtLauncher/*` | ported | 可选 mod 选择、per-type 目录布局、组件 patch |
| `modplatform/technic/*` | `ModPlatform/Technic/*` | ported | ZIP/Solder 双路径 + 三种 flavor 侦测 |
| `modplatform/ftb/*` `legacy_ftb/*` `import_ftb/*` | `ModPlatform/Ftb*/` | ported | 目标->组件映射、legacy XML 修复规则、FTB App 布局钩子 |
| `modplatform/helpers/*` | `ModPlatform/Helpers/ExportToModList.cs` `HashUtils.cs` `OverrideUtils.cs` | ported | 五种导出模板字节级对齐 |
| `libraries/murmur2` | `ModPlatform/Hashing/Murmur2.cs` | ported | 独立参考向量验证 |
| `libraries/qdcss` | — | n/a | 未发现调用点（此前登记待查） |
| `InstanceImportTask*`（modpack 分发） | `Instances/InstanceImportTask.cs` | ported | mrpack/Flame/Technic/plain/URL 五路分发 |
| `ResourceDownloadTask*`、更新既有实例流 | — | partial | 安装期资源下载已覆盖；独立"更新实例"流与 GUI 对话框（optional/blocked 交互）延后为钩子 |

## P7 周边

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `screenshots/*` | `FlintLauncher.Core/Screenshots/` | ported | Imgur 上传/相册（Client-ID + multipart） |
| `news/*` | `FlintLauncher.Core/News/` | ported | RSS 下载缓存 + Atom 解析 |
| `net/PasteUpload*` + `logs/AnonymizeLog*` | `Net/PasteUpload.cs` | ported | 四 provider + 日志匿名化 |
| `logs/LogParser*` | `Launch/LogParser.cs` | ported | Log4j XML/纯文本拆分；前导文本被吞语义保留 |
| `minecraft/GameOptions*`(Op*) | `Minecraft/GameOptions/GameOptions.cs` | ported | options.txt 读写与转义 |
| `DataMigrationTask*` | `Migration/DataMigration.cs` | ported | 文件清单/marker 幂等/聚合失败语义 |
| `tools/*` | `Tools/Profilers.cs` | ported | 注册表 + argv 构建（无进程派生） |
| `translations/*` 下载逻辑 | — | deferred | 无头核心价值低，需要时再补 |
| `updater/*` | GUI-bound / 进程外 | n/a | |
| `Application*` `main.cpp` `ApplicationMessage*` `LocalPeer` | `FlintLauncher.Web/WebHostBuilder.cs` + Services/* | ported | v1 API：instances CRUD/settings/launch/tasks+日志+SSE/accounts(含 MSA 设备码)/java/news/modplatform 搜索；数据目录注入+CWD 切换；LocalPeer→进程单例语义由宿主承担 |
| `HardwareInfo*` `SysInfo*` | `Launch/HardwareInfo.cs` `SysInfo.cs` | ported | P5 已带出 |
| `DesktopServices*` | n/a（打开文件/URL 语义按需暴露） | n/a | |
| `ui/**` `icons`(渲染) `ui/themes` `translations`(UI) | — | GUI-bound | |
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
