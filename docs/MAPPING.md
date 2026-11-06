# MAPPING — 原 C++ 文件 ↔ Flint C# 映射表

> 规则：每阶段推进时更新本表。状态：`ported` 完整移植（含测试）/ `partial` 部分移植 / `todo` 未开始 / `n/a` 不适用或被平台设施取代 / `GUI-bound` 不移植（RESEARCH.md §8）。
> C# 侧路径相对仓库顶级项目目录（如 `FlintLauncher.Core/`）。类型注释中必须标注移植来源。

## P0 已完成

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `buildconfig/BuildConfig.{h,cpp.in}` + CMake 默认值 | `FlintLauncher.Core/FlintConfig.cs` | ported | 常量按仓库 CMake 默认值固化；版本号改为 Flint 自己的 0.1.0；`LAUNCHER_CONFIGFILE`→`flint.cfg`（导入 prismlauncher.cfg 属 P7 迁移流程） |

## P1 基础设施

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `Version.{h,cpp}` | `FlintLauncher.Core/Versioning/McVersion.cs` | ported | FlexVer + 空格特例；70 条官方向量全过 |
| `java/JavaVersion.{h,cpp}` | `FlintLauncher.Core/Java/JavaVersion.cs` | ported | |
| `StringUtils.{h,cpp}` | `FlintLauncher.Core/Text/StringUtils.cs` | partial | 仅 NaturalCompare（ordinal 代替 locale）；其余按需补 |
| `FileSystem.{h,cpp}` | `FlintLauncher.Core/IO/FileSystem.cs` | partial | 平台中立子集（copy/move/delete/combine/原子写等）；reflink/trash/desktop 等记录为 skip |
| `GZip.{h,cpp}` | — | deleted（GZ UTIL） | 零生产调用方：level.dat 读写由 NbtIo 的 gzip/raw 自动探测吸收，日志端点按纯文本读；残留 gz 需求直接用 BCL GZipStream |
| `Json.{h,cpp}` | `FlintLauncher.Core/Json/JsonUtils.cs` | ported | require/ensure 全家（JsonNode 单 DOM 读模型，2026-10-03 起唯一轨道：element 双轨家族已删除）+ to/from list/map；QVariant 部分不适用 |
| `StringUtils`(其余) `Markdown.{h,cpp}` | `FlintLauncher.Core/Text/Markdown.cs` | ported | 手写 cmark 子集 |
| `Commandline.{h,cpp}` | `FlintLauncher.Core/Text/Commandline.cs` | ported | |
| `MMCZip.{h,cpp}` | `FlintLauncher.Core/IO/MMCZip.cs` | partial | extract/compress/merge；createModdedJar 待 Mod 模型；符号链接/权限位未实现 |
| `settings/INIFile.{h,cpp}` | `FlintLauncher.Core/Settings/IniFile.cs` + `QSettingsIniCodec.cs` | ported | 黄金对拍：真 QSettings 6.11 生成 golden 文件，读写字节级一致（docs/tools/qt-ini-golden） |
| `settings/SettingsObject*` `Setting*` `INISettingsObject` `OverrideSetting` `PassthroughSetting` | `FlintLauncher.Core/Settings/*.cs` | ported | QVariant→object?+`Variant` 转换层 |
| `tasks/Task*` | `FlintLauncher.Core/Tasks/*` | partial | C# 有协程，任务基类不移植：每个操作直接是 `async Task`（返回即成功、抛出即失败、取消走 token）；`ConcurrentTask` 语义保留在 `TaskWork.RunAllAsync`（子项失败不中断兄弟、排空后聚合） |
| `net/Request*` | `FlintLauncher.Core/Net/NetRequest.cs` | partial | HttpClient 底座；429/503 自动重试；HeaderProxy 简化为 AddHeader |
| `net/Sink*` `FileSink` `ByteArraySink` `MetaCacheSink` | `FlintLauncher.Core/Net/Sink.cs` `FileSink.cs` | ported | PSaveFile→临时文件+原子替换 |
| `net/Validator*` `ChecksumValidator` | 折叠进 `FlintLauncher.Core/Net/HttpDownloadTask.cs` | replaced | 下载完成校验：BCL HashData 对 temp 文件，通过才提交 |
| `net/HttpMetaCache*` | `FlintLauncher.Core/Net/HttpMetaCache.cs` | ported | metacache JSON 格式（version "1"）与原版互通；挂载表（baseId→根）+ 账本分离（E4），账本落 cache 根（E3），启动 Load()（E6） |
| `net/NetJob*` | —（不移植） | dropped | C# 有协程：一批下载就是 `TaskWork.RunAllAsync` 或 `SequenceAsync` 的项列表 |
| `net/Mode.h` `ApiRequest.h` `HeaderProxy*` | `FlintLauncher.Core/Net/Mode.cs`；头机制简化 | partial | 头注入按需在 P2/P6 恢复 |
| `net/PasteUpload*` | `FlintLauncher.Core/Net/PasteUpload.cs` | ported | P7 完成（见下）；注入 NetStack 直发 |
| `RWStorage.h` | — | n/a | 由 C# 并发设施取代 |
| `ExponentialSeries.h` `AssertHelpers.h` `Exception.h` `EnumWrapper.h` `QVariantUtils.h` `QObjectPtr.h` `Usable.h` `SeparatorPrefixTree.h` `Filter.h` `WatchLock.h` `KonamiCode` | `n/a`（Variant.cs 承接 QVariantUtils 子集） | n/a | Qt 设施，被 C# 语言设施取代 |
| `PSaveFile.h` | `FlintLauncher.Core/IO/FileSystem.cs`（原子写）+ `Net/FileSink.cs` | ported | |
| `MMCTime.{h,cpp}` | — | todo | 若仅 GUI 使用则 n/a |

## P2 元数据与版本模型（已完成 2026-09-11）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `meta/JsonFormat*` | `FlintLauncher.Core/Meta/MetaJsonFormat.cs` | ported | Require/RequireSet（uid 相等语义） |
| `meta/BaseEntity*` + BaseEntityLoadTask | `FlintLauncher.Core/Meta/MetaEntity.cs` | ported | sha256 校验、本地缓存优先、下载完成解析门禁防坏提交；注入盖章的 HttpMetaCache(base "meta"，无静态兜底，E6) |
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
| `InstanceList*` | `FlintLauncher.Core/Instances/InstanceList.cs` + `Instances/InstanceGroupModel.cs` | ported | 组半边归 InstanceGroupModel（G4：config 侧 groups.json 成员 uuid 化 C8/D3 + state 侧 hidden S5）；D2 寻址链（inode→uuid→dirname）ResolveInstance 唯一入口；QAbstractListModel/Watcher 不移植 |
| `InstanceCopyPrefs*` `InstanceCopyTask*` `InstanceTask*` | `FlintLauncher.Core/Instances/InstanceCopy*.cs` `InstanceTask.cs` | ported | link/clone 传输清晰报错（上游 create_link/clone 未移植） |
| `InstanceImportTask*` | — | todo | P6（依赖 modplatform 分发） |
| `InstanceDirUpdate*` | — | n/a | GUI 对话框耦合；纯文件半已在 BaseInstance/InstanceList |
| `VanillaInstanceCreationTask`(minecraft/) | `FlintLauncher.Core/Instances/Creation/VanillaInstanceCreationTask.cs` | ported | mmc-pack/instance.cfg 字节兼容 |
| `ProfileUtils*`(minecraft/) | `FlintLauncher.Core/Minecraft/ProfileUtils.cs` | ported | P2b 已完成；此检出版本无 legacy 导入半 |
| `minecraft/World*` `WorldList*` `WorldTasks*` | `FlintLauncher.Core/Minecraft/Worlds/` | ported | Qt model 不移植；World::install 上游反向检查 bug 已修 |
| `LocalWorldSaveParseTask` | `Worlds/WorldSaveIdentification.cs` | partial | 内容级判定（folder/zip 的 level.dat 探测）+ 导入时的类型回退；解析任务/abort 那层不移植 |
| `minecraft/mod/*`（资源模型与解析） | `FlintLauncher.Core/Minecraft/Resources/` | ported | pack_format 表全量；QImage→PNG IHDR 读取（已记录偏差） |
| `minecraft/Op*`（gameoptions） | — | todo | P7 |
| `icons/IconList` `IconUtils`（文件部分） | `FlintLauncher.Core/Icons/IconFiles.cs` + `Icons/IconPool.cs` | ported | D5：IconPool 模型（key→path 索引/扫描/安装/删除，DI 注入，IconsDir 设置生效）；Qt 模型/主题不移植 |
| `skins/SkinList`（模型半） | `FlintLauncher.Core/Skins/SkinsLibrary.cs` | ported | D6 方案 B：本地皮肤库（SkinListStore 持久化半现成）；套用=组合 SkinUpload |
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
| `libraries/launcher` + `javacheck` | MSBuild 目标 `BuildJavaJars`（javac/jar 直入 Exec）→ build/jars/*.jar 嵌入 FlintLauncher.Core | ported | javac --release 8；产物不入库 |
| `Application::appDebugOutput` + `Application::init` 日志文件初始化（Application.cpp:236-264/513-528） | `FlintLauncher.Core/Diagnostics/FlintLog.cs`（静态门面+Categories）+ `FlintLauncher.Web/Logging/RollingFileLoggerProvider.cs` + WebHostBuilder 级别解析/请求日志中间件 | ported | 诊断通道与启动控制台(LogModel)分离如上游；滚动 `<dataDir>/logs/Flint-{0..4}.log` 每次启动顺移一代；级别 `--log-level`/`FLINT_LOG_LEVEL` 取代 qtlogging.ini 规则文件；Core 经静态门面共用宿主 ILoggerFactory（Core 无 DI 的文档化偏差）；[LoggerMessage] 源生成保证 AOT |
| `MinecraftInstance::createLaunchTask/createLaunchScript/createEnvironment` | `FlintLauncher.Core/Instances/MinecraftInstance.cs` | ported | 全链冒烟通过（fake-java + stdin 协议 + PostExit） |
| `logs/*` `MessageLevel*` | `FlintLauncher.Core/Launch/LogModel.cs` `MessageLevel.cs` | ported | Log4j XML 解析未移植 |
| `net/PasteUpload*`、SRV | SrvResolver.cs 已移植；PasteUpload | partial | PasteUpload 归 P7 |

## P6 modplatform（已完成 2026-09-12）

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `modplatform/ModIndex.h` `ResourceAPI.h` | `ModPlatform/ModIndex.cs` `ResourceAPI.cs` | ported | |
| `modplatform/modrinth/*` | `ModPlatform/Modrinth/*` | ported | 搜索/版本/按哈希更新/.mrpack 导入导出往返 |
| `modplatform/flame/*` | `ModPlatform/Flame/*` | ported | 指纹批量解析、blocked-mods 数据语义、导入导出往返；API key 用户自备 |
| `modplatform/packwiz/*` + MetadataHandler | `ModPlatform/Packwiz/*` `MetadataHandler.cs` `EnsureMetadataTask.cs` | ported | Tomlyn 类型化 DTO（上游 Packwiz_test 原样通过） |
| `modplatform/atlauncher/*` | `ModPlatform/AtLauncher/*` | ported | 可选 mod 选择、per-type 目录布局、组件 patch |
| `modplatform/technic/*` | `ModPlatform/Technic/*` | ported | ZIP/Solder 双路径 + 三种 flavor 侦测 |
| `modplatform/ftb/*` `legacy_ftb/*` `import_ftb/*` | `ModPlatform/Ftb*/` | ported | 目标->组件映射、legacy XML 修复规则、FTB App 布局钩子 |
| `modplatform/helpers/*` | `ModPlatform/Helpers/ExportToModList.cs` `HashUtils.cs` `OverrideUtils.cs` | ported | 五种导出模板字节级对齐 |
| `libraries/murmur2` | `ModPlatform/Hashing/Murmur2.cs` | ported | 独立参考向量验证 |
| `libraries/qdcss` | — | n/a | 未发现调用点（此前登记待查） |
| `InstanceImportTask*`（modpack 分发） | `Instances/InstanceImportTask.cs` | ported | mrpack/Flame/Technic/plain/URL 五路分发 |
| `ResourceDownloadTask*` | `ModPlatform/ResourceDownloadTask.cs` | ported | 下载+元数据；旧文件的删除发生在下载成功之后，由元数据步骤的返回值带回 |
| `minecraft/mod/tasks/GetModDependenciesTask*` | `ModPlatform/Dependencies/GetModDependenciesTask.cs` | ported | 递归依赖解析（required-only/quilt-fabric override/环防护） |
| `minecraft/mod/tasks/LocalResourceUpdateTask*` | `ModPlatform/Dependencies/LocalResourceUpdateTask.cs` | ported | |
| 更新应用编排（C++ 在 UI 层） | `ModPlatform/ModUpdateTask.cs` | ported | 失败不中止兄弟、末尾统一失败（C++ 语义） |
| Web API modplatform 端点 | `FlintLauncher.Web/Endpoints/ModPlatformEndpoints.cs` | ported | 搜索(modrinth/flame)/versions/install/update/mods 列表 |

## P7 周边

| 原文件 | C# 目标 | 状态 | 备注 |
|---|---|---|---|
| `screenshots/*` | `FlintLauncher.Core/Screenshots/` | ported | Imgur 上传/相册（Client-ID + multipart） |
| `news/*` | — | deleted（CACHE E7） | 纯 GUI 功能，整体删除（checker + `/news` 端点 + feed base + FLINT_NEWS_RSS_URL） |
| `net/PasteUpload*` + `logs/AnonymizeLog*` | `Net/PasteUpload.cs` | ported | 四 provider + 日志匿名化 |
| `logs/LogParser*` | `Launch/LogParser.cs` | ported | Log4j XML/纯文本拆分；前导文本被吞语义保留 |
| `minecraft/GameOptions*`(Op*) | `Minecraft/GameOptions/GameOptions.cs` | ported | options.txt 读写与转义 |
| `DataMigrationTask*` | `Migration/DataMigration.cs` | partial | 决策树/文件清单/marker 幂等已移植；拷贝任务本身不移植——`PrismImportTask` 用同一清单执行传输，并自带计划与 dry run |
| `tools/*` | `Tools/Profilers.cs` | partial | 注册表 + argv 构建（无进程派生）；`GET /tools/profilers` 暴露注册表、check 结果与命令行 |
| `translations/*` 下载逻辑 | — | deferred | 无头核心价值低，需要时再补 |
| `updater/*` | GUI-bound / 进程外 | n/a | |
| `Application*` `main.cpp` `ApplicationMessage*` `LocalPeer` | `FlintLauncher.Web/WebHostBuilder.cs` + Services/* | ported | v1 API：instances CRUD/settings/launch/tasks+日志+SSE/accounts(含 MSA 设备码)/java/modplatform 搜索+分页+install/update 结果负载+本地 token 鉴权(--api-token/--no-auth)；五根注入（news 已随 CACHE E7 删除）；账号后台刷新(FLINT_ACCOUNT_REFRESH_MINUTES, 默认关) |
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
| `tomlplusplus` | Tomlyn 2.10 类型化 DTO（source-gen context） | ported |
| `katabasis` | 自写 OAuth（P4） | n/a |
| `rainbow`（颜色工具） | GUI-bound | n/a |
| `QJsonModel` | GUI-bound | n/a |
| `qrcodegenerator` | device-code 登录二维码（API 场景可输出 URL，二维码前端事） | n/a（如需可在 Web 层用 SkiaSharp 另议） |

## 迁移注记（2026-10-01，编排工作流批量）

- **JSON 读侧单 DOM**：Modrinth/Flame/Technic/AtLauncher/FTB 家族/Meta/Minecraft 的解析器已统一迁到 JsonNode 读模型（JsonUtils node 侧 Require*/Ensure* 家族）；JsonUtils 的 element 家族保留供未迁移的少数读取点，最终删除待定。
- **DI 注入进度**：Modrinth/Flame/Technic/AtLauncher/FTB/Imgur 静态类已全部实例化（组合根持有、构造注入 NetStack、可选参数贯穿调用链）；Auth 端点已 AuthEndpointsOptions 注入；启动管线已接 LaunchServices（全部启动钩子静态改实例成员）；FlintLog 静态门面已删除（ILoggerFactory 构造注入）；测试端口分配已收敛 PortAllocator。测试并行保持串行（collections 试验 14 例失败，失败清单记录于提交 e8add2daa 后的编排运行报告，为后续静态清理批次的验收输入）。

## 迁移注记（2026-10-03，静态残余收尾批量）

- **JsonUtils element 双轨家族删除**：Core 10 文件（OneSixVersionFormat/FtbAppPackParse/PackProfile/JavaMetadata/ProfileUtils/Flame PackManifest/MetaEntity/HttpMetaCache/DataPackUtils/FtbAppPaths）+ Web 4 文件（ModPlatformEndpoints/ModPlatformTasks/TechnicEndpoints/ServerStatusPing）的残余读取点全部 node 化后，JsonDocument/JsonElement 轨道的 require*/ensure*、(JsonObject,key) 桥接重载、ToNode/ToOwnedElement 转换缝与缓存 element 站桩全部删除；JsonObject 键控重载保留为纯 node 委托（身份转换优先级防止 2 参 (parent,key) 调用误绑 (value,what) 重载）。零生产调用方的 element 专用成员（RequireByteArray/DateTime/Url/Uuid/ArrayOfStrings、Write/ToText、ToJsonByteArray/DateTime/Uuid）随轨删除。仓库另存若干**局部** System.Text.Json element 解析点（ImgurAPI/PasteUpload/InstanceGroupModel/AssetsUtils/BaseInstance/SkinModel/ExportToModList/MojangVersionFormat/LocalModParse/QtJson 缩进 shim 等）——它们不经 JsonUtils，不属双轨家族，转 node 另立批次。
- **DI 残余清零**：AtLauncherPackInstallTask 的 MetaVersionDataLookup/RecommendedLoaderVersionLookup、FlameCreationTask 的 RecommendedVersionLookup 由静态改 per-task 注入（测试经构造/初始化器设置，无可污染的全局态）；PasteUpload/SkinUpload 直发改注入 NetStack（端点传 runtime.Net，默认回落 NetStack.Default）；`Http` 静态 shim 删除（消费点仅存测试，已重指 NetStack.Default）。collections 并行试验的失败输入清单按此收敛，重验证随全量测试批次。
- **TechnicPackProcessor 异步化**：同步 `Run`（内部 `SetComponentVersion(...).GetAwaiter().GetResult()` 阻塞元数据网络解析）改 `async Task<string?> RunAsync`，三个调用点（SingleZip/Solder/InstanceImport）await 直连并贯穿 CancellationToken。
