# Flint — PrismLauncher → ASP.NET Core 迁移研究笔记

> 本文是对 Qt/C++ 版 PrismLauncher（本仓库 `launcher/` 树）的迁移前调研结论。
> 来源：2026-09 两轮子代理全库扫描 + 关键文件核对。后续实现中如与本文冲突，以源码为准并回改本文。
> 对应计划见 [PLAN.md](PLAN.md)，文件级映射见 [MAPPING.md](MAPPING.md)。

## 0. 迁移目标与既定决策

- 把 Qt GUI 启动器迁移为 **ASP.NET Core Minimal API「启动器核心」**，功能点与原应用操作级对齐（凡改变文件/网络/进程状态的功能都要有对应 API）。
- **Native AOT 必须支持**（`PublishAot=true`）。
- 纯 GUI 表现层（主题/猫包/动画、翻译 UI、3D 皮肤预览、桌面快捷方式、Sparkle 更新器 UI 等）**不移植**，在 MAPPING.md 中记录为 `GUI-bound, not ported`。
- **完全兼容现有 PrismLauncher 数据目录**：instances 布局、`instance.cfg`、`prismlauncher.cfg`、`accounts.json`、meta 缓存格式全部互通。
- 目标框架 **net10.0**；解决方案 `FlintLauncher.slnx` 在仓库根，每个项目对应顶级 `FlintLauncher.*` 目录（csproj 直接位于目录下）。

## 1. 原应用架构总览

| 目录（`launcher/` 下） | 职责 |
|---|---|
| `Application.*` / `main.cpp` | 顶层服务装配：网络、metacache、meta index、settings、instances、accounts、icons、translations、updater、profilers；数据目录定位与 PolyMC 数据迁移 |
| `BaseInstance.*` / `InstanceList.*` / `MinecraftInstance.*`(minecraft/) | 实例模型/枚举/CRUD；`instance.cfg` + 全局设置覆盖解析 |
| `minecraft/` | OneSix 组件栈：`PackProfile`(组件表)、`Component`、`VersionFile`、`Library`、`LaunchProfile`、`Rule`、`ComponentUpdateTask`；世界/皮肤/资源模型 |
| `meta/` | meta.prismlauncher.org 的本地对象化缓存（Index→VersionList→Version，sha256 校验） |
| `launch/` + `minecraft/launch/` | 启动步骤引擎与 Minecraft 专用步骤 |
| `java/` | JVM 扫描（平台分支）、`JavaChecker`（外部 jar 探测）、Java 运行时下载（meta `java` 列表） |
| `net/` | `NetJob`/`Request`/`Sink`/`Validator`、`HttpMetaCache`、PasteUpload(0x0.st 风格) |
| `tasks/` | Task 状态机、`SequentialTask`/`ConcurrentTask` 组合器 |
| `settings/` | INI 设置框架（QSettings 兼容格式）、`OverrideSetting`+gate |
| `modplatform/` | Modrinth/Flame/ATL/FTB/legacyFTB/Technic/Packwiz/FTBApp导入 |
| `minecraft/auth/` | MSA→Xbox→XSTS→MinecraftServices 全链路，`accounts.json` 持久化 |
| `minecraft/skins/`、`screenshots/`、`news/` | 皮肤/披风 Mojang API、Imgur 上传、RSS 新闻 |
| `minecraft/World*` + `ui/pages/instance/ServersPage` | level.dat / servers.dat 的 NBT 读写 |
| `icons/`、`translations/`、`updater/`、`tools/`、`ui/` | GUI 侧（部分有网络/文件行为需保留） |
| `libraries/launcher/`(repo 根 libraries 下) | `NewLaunch.jar` 源码：`org.prismlauncher.EntryPoint`，启动时经 stdin 接收 launch script，在 java 进程内装配 natives/mainclass —— **可原样复用** |
| `libraries/libnbtplusplus` 等 | C++ 依赖：NBT、quazip、tomlplusplus、katabasis、murmur2、qdcss、qrcodegenerator、rainbow、LocalPeer |

## 2. 启动管线（`MinecraftInstance::createLaunchTask()` 的步骤顺序）

1. `TextPrint` — 打印实例 Minecraft 目录
2. `CreateGameFolders` — 建 `.minecraft`、`server-resource-packs`（MCL-3732）
3. `LookupServerAddress` — SRV `_minecraft._tcp` 解析（仅自动端口场景）
4. `LaunchCommand("Pre-Load")`（用户配置了才有）
5. `TaskStepWrapper(MinecraftLoadAndCheck)` — 刷新组件元数据，构建最终 `LaunchProfile`
6. `AutoInstallJava` — 无 JVM 时按 meta `java` 列表下载
7. `CheckJava` / `VerifyJavaInstall` — 版本/位数/主版本兼容校验
8. `LaunchCommand("Pre-Launch")`
9. `ClaimAccount` — 在线模式记账账号使用
10. 更新任务组：创建游戏目录 → `LibrariesTask` → `LegacyFMLLibrariesTask`(≤1.5.2 FML) → `AssetUpdateTask`
11. `EnsureOfflineLibraries` — 离线模式校验本地库齐全
12. `ModMinecraftJar` — jarmod/自定义 jar 合成 `minecraft.jar`
13. `ScanModFolders` — 扫 mods/资源包目录
14. `EnsureAvailableMemory` — 内存预警
15. `PrintInstanceInfo`
16. `ExtractNatives` — 解压 natives classifier jar 到 `<instance>/natives/`
17. `ReconstructAssets` — 旧版资源虚拟目录 `assets/virtual/<id>`
18. `LauncherPartLaunch` — 起 java 进程：`java [jvmArgs] -Djava.library.path=natives -cp NewLaunch.jar+libs org.prismlauncher.EntryPoint`，launch script 写入 stdin，等待 `readyForLaunch` 交互（`launch`/`abort`）
19. 游戏退出后：`LaunchCommand("Post-Exit")` →（可选）`QuitAfterGameStop`

`LaunchController` 在此之前负责：选账号（在线/离线/演示）、token 刷新、构建 `AuthSession`（日志脱敏来源）。

## 3. 网络端点清单（BuildConfig 实际值）

| 用途 | URL |
|---|---|
| 元数据仓库 | `https://meta.prismlauncher.org/v1/` |
| 资产对象 | `https://resources.download.minecraft.net/` |
| Mojang 库 | `https://libraries.minecraft.net/` |
| legacy 资产索引回退 | `https://piston-meta.mojang.com/mc/assets/legacy/<hash>/legacy.json` |
| legacy FML 库 | `https://files.prismlauncher.org/fmllibs/` |
| 新闻 RSS | `https://prismlauncher.org/feed/feed.xml` |
| 翻译文件 | `https://i18n.prismlauncher.org/` |
| Modrinth API | `https://api.modrinth.com/v2`（staging 备用；下载域 `cdn.modrinth.com`，mrpack 白名单含 github/raw.githubusercontent/gitlab） |
| CurseForge API | `https://api.curseforge.com/v1`（`x-api-key`；下载域 `edge.forgecdn.net`） |
| FTB(新) | `https://api.feed-the-beast.com/v1/modpacks/public` |
| FTB legacy CDN | `https://dist.creeper.host/FTB2/` |
| ATLauncher | API `https://api.atlauncher.com/v1/`，下载 `https://download.nodecdn.net/containers/atl/` |
| Technic | `https://api.technicpack.net/`（build 参数 `multimc`） |
| MSA OAuth | `https://login.microsoftonline.com/consumers/oauth2/v2.0/{authorize,token,devicecode}`；client id `c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb` |
| Xbox | `https://user.auth.xboxlive.com/user/authenticate`、`https://xsts.auth.xboxlive.com/xsts/authorize`（RP `rp://api.minecraftservices.com/`） |
| Minecraft 服务 | `https://api.minecraftservices.com/` 的 `launcher/login`、`entitlements/license`、`minecraft/profile`、`/rollout/v1/msamigration`、`minecraft/profile/skins`、`/capes/active` |
| Imgur | `https://api.imgur.com/3/`（client id `5b97b0713fba4a3`） |
| 更新器 | GitHub releases `https://github.com/PrismLauncher/PrismLauncher`；macOS Sparkle appcast |

CurseForge key 为构建期注入的 bcrypt 形字符串（仓库 CMakeLists 公开存放），经 `SupportsFlame` capability 门控；用户可用 `FlameKeyOverride`/`MSAClientIDOverride` 设置覆盖。

## 4. 磁盘格式（兼容目标）

- **数据目录**（macOS `~/Library/Application Support/PrismLauncher`，可被便携目录/`PRISMLAUNCHER_DATA_DIR` 覆盖）下：
  - `instances/`（每实例目录含 `instance.cfg`、`minecraft/`、`mmc-pack.json`、`patches/*.json`）
  - `meta/index.json`、`meta/<uid>/index.json`、`meta/<uid>/<version>.json`（sha256 校验，坏文件删除重下）
  - `metacache`（HttpMetaCache 索引：base 目录 + md5/etag/时间戳/TTL）
  - `accounts.json`（明文 JSON：msaToken/userToken/xsts/yggdrasilToken/profile/entitlement/skins，含默认账号；**此分支无加密 keystore**）
  - `assets/{indexes,objects,virtual}/`、`libraries/`、`cache/`、`icons/`、`java/`（托管 JDK）、`skins/`（`skins.json` 本地皮肤库）
  - `prismlauncher.cfg`（全局设置；Flint 使用 `flint.cfg`，首次运行从 prismlauncher.cfg/polymc.cfg/multimc.cfg 导入——对齐原 `DataMigrationTask` 思路）
- **instance.cfg / 全局 cfg 格式**：QSettings IniFormat，`ConfigVersion=1.3`；旧格式（1.1/1.2）有转义怪癖（`\n`、`\t`、`\#`、含 `;,=` 的值加引号），QByteArray 曾用 base64 迁移。 Flint 的 INI 读写器必须兼容三种格式读、按 1.3 写。
- **组件栈**：`mmc-pack.json`（组件列表 uid/version/flag）+ `patches/<uid>.json`（本地补丁）+ meta 缓存的 `VersionFile`，按依赖序合并进 `LaunchProfile`。

## 5. 认证链路（AuthFlow 步骤序）

`MSAStep`（浏览器授权码 + 本地回调/自定义协议，或 `MSADeviceCodeStep` 设备码）→ `XboxUserStep` → `XboxAuthorizationStep`（XSTS + Mojang services token）→ `LauncherLoginStep` → `EntitlementsStep` → `MinecraftProfileStep` → `GetSkinStep`。离线账号 = 用户名确定性 UUID。Token 存 `accounts.json` 明文。 Flint 首期实现 device-code + refresh-token 静默刷新，浏览器回调其次。

## 6. modplatform 能力矩阵

| 平台 | 搜索 | 整合包导入 | 资源更新 | 导出 | 密钥 |
|---|---|---|---|---|---|
| Modrinth | ✅ | ✅ `.mrpack` | ✅ sha1/murmur2 | ✅ `.mrpack` | 用户可选 PAT |
| CurseForge | ✅ | ✅ manifest.zip | ✅ 指纹(POST /fingerprints, murmur2) | ✅ CF zip | 构建期 key |
| FTB(新) | ✅ 列表 | ✅ | ❌ | ❌ | 无 |
| FTB legacy | ✅ XML | ✅ | ❌ | ❌ | 无 |
| ATLauncher | ✅ | ✅（可选 mod/share code） | ❌ | ❌ | 无 |
| Technic | ✅ | ✅（ZIP + Solder 两种） | ❌ | ❌ | 无 |
| Packwiz | ❌ | ✅ 本地 TOML | ✅（作为安装元数据底座） | ❌ | 无 |
| FTB App 导入 | ❌ | ✅ 读本地安装 | ❌ | ❌ | 无 |

共享组件：`HashUtils`(sha1/sha256/murmur2)、`OverrideUtils`、`ExportToModList`(MD/HTML/CSV/JSON)。

## 7. Java 子系统

- 扫描：Windows（注册表+常见目录+javaw.exe）、macOS（`/usr/libexec/java_home`、`/Library/Java`）、Linux/BSD（`JAVA_HOME`、`/usr/lib/jvm`、sdkman 等）。
- 校验：`java -jar JavaCheck.jar` 输出解析（架构/厂商/版本/64位）。
- 版本比较：`JavaVersion`（`1.8.0_312` 数值序）。
- 下载：meta `java` 列表 → `ManifestDownloadTask`(清单+sha1) / `ArchiveDownloadTask`(zip/tar) 安装到 `<data>/java/`。

## 8. GUI-bound 不移植清单（记录用）

`ui/themes/`（含 IconTheme/CatPack）、`KonamiCode`、`ui/dialogs/skins/draw`（OpenGL 预览）、Qt widget 层全部、`translations` 的 UI 装配（网络下载逻辑保留价值低，先不做）、`updater`（Sparkle/prismupdater 二进制更新器）、`LocalPeer`（单实例锁——Web 服务用进程管理替代）、`macsandbox/`、`MTPixmapCache`、桌面快捷方式/`DesktopServices` 的 GUI 打开行为（API 可保留「返回路径」语义）。

## 9. AOT 技术选型决策

| 领域 | 选择 | 说明 |
|---|---|---|
| JSON | `System.Text.Json` source-gen（`JsonSerializerContext`） | 关 `JsonSerializerIsReflectionEnabledByDefault` |
| NBT | 自研 `FlintLauncher.Nbt`（对标 libnbtplusplus 所需子集：gzip/raw、Tag 全集、二进制读写） | 无 AOT 安全的现成库满足需求 |
| ZIP | `System.IO.Compression.ZipArchive` | 替代 QuaZip；注意符号链接/权限位保留（模组包） |
| GZip | `System.IO.Compression.GZipStream` | 替代 `GZip.cpp` |
| TOML(packwiz) | Tomlyn 2.10 类型化 DTO + source-gen context（AOT 分析器 0 警告） | 已迁移 |
| murmur2 | 移植 `libraries/murmur2` | CurseForge 指纹必需 |
| HTTP | `HttpClient` + 自实现 HttpMetaCache（ETag/If-None-Match/sha 校验） | 替代 NetJob 栈 |
| 任务 | async/await；顺序=逐个 await、并发=`Parallel.ForEachAsync` 节流（失败收成 `AggregateException`） | 保留 Task 语义（进度/取消/失败聚合） |
| 进程 | `System.Diagnostics.Process` + stdout/stderr 行事件 | 替代 LoggedProcess；需进程树终止（Windows Job Object / POSIX process group） |
| Java 助手 | `libraries/launcher` 源码原样复用，开发期 javac 编译为资源内嵌 jar | GPL 同源无许可问题；构建机需 JDK |
| 设置 | 自研 QSettings 兼容 INI 读写器 | 兼容目标见 §4 |
| 版本比较 | 移植 `Version.cpp`（Minecraft 版本序）与 `JavaVersion` | 有 C++ 单测可对拍 |

## 10. 原有 C++ 单元测试（tests/，迁移对拍来源）

GradleSpecifier、GZip、INIFile、Version、Library、MojangVersionFormat、MetaComponentParse、ParseUtils、JavaVersion、Packwiz、FileSystem、ResourcePackParse、TexturePackParse、DataPackParse、ShaderPackParse、WorldSaveParse、Index、Task、XmlLogs、CatPack(不移植)。testdata/ 中的样例 JSON 需要复制为测试夹具。
