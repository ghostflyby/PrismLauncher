# Config 聚类设计（架构革新 · config）

状态：**决议已定**（2026-10-02）。实施细节（record 全集、层栈 API、导入器规格）随统一迁移批次出。
事实输入：[FILE-LAYOUT-ANALYSIS.md](FILE-LAYOUT-ANALYSIS.md)（§1 现状清单、§5 Model 审计）+ 2026-10-02 两轮讨论。

---

## 0. 决议

| # | 决议 |
|---|---|
| **C1** | **五根独立可注入**（E1 修订，原四根，通用前提适用全部聚类）：config/data/**store**/cache/state 各自支持启动时单独覆盖（CLI `--config-dir` 等 + 对应 env），缺省回平台默认，由 `FlintPaths` 单点吸收。目的：Flint 核心可被任意启动器界面集成。现状只有整目录覆盖（`--data-dir`/`FLINT_DATA_DIR`）。store 类定义见 [CACHE.md](CACHE.md) E2。 |
| **C2** | **系统（所有用户）层暂不做**：对 MC 启动器实用性/紧迫性低。层栈当前 = user → instance 两阶段；JSON 分层格式允许未来在栈首插入 system 层，**格式无需变更**。矩阵保留 Sys 列作为格式预留。 |
| **C3** | **accounts 出 config**：目标 = secret store 模型（C4 同一接口）；过渡 = data 根（保持 0600）。XDG 映射更正：accounts.json → `XDG_DATA_HOME/flint/`，不进 config。 |
| **C4** | **config 内秘密同批出**：`ProxyPass`、`ModrinthToken`、`FlameKeyOverride` 随 accounts 进 secret store 模型；过渡落位 `data/secrets.json`（0600）。`MSAClientIDOverride` 是公开 client id，留 config。config 格式中**不设秘密字段**，代码经 secret store 按名读取。 |
| **C5** | **格式换 JSON**：弃 Qt INI 写侧，只保留**读导入**。文件：`flint.json`（user 层）/ `instance.json`（实例层，设置+元数据——拆分形状见 [DATA.md](DATA.md) D4；组件清单 mmc-pack/patches 本就是 JSON，不动）。实现 = System.Text.Json 源生成 record，AOT 零警告红线不变（FlintJsonContext 既有管线与三条铁律）。 |
| **C6** | **强类型访问**：每域一个 record；成员可空 = 本层未设置（继承下层）。**默认值单源化** = record 属性默认值；`GlobalSettingsRegistration` 与 Web `SettingsAndIconsEndpoints.Groups` 双表退役（后者已实测漂移：缺 PreLoadCommand/PastebinURL/ModMetadataDisabled/ModUpdateReleaseTypes/LastOfflinePlayerName/TotalPlayTime* 等）。 |
| **C7** | **分层语义**：层栈 Fold = 上层非 null 覆盖、null 继承。现有 Override gate（`OverrideJavaLocation` 等）语义**保持 C++**：gate=false 时实例值视为未设置——INI 导入时该值丢弃转继承。`PassthroughSetting`（ConsoleMaxLines/ConsoleOverflowStop、Java 五件套）非分层，映射见 C9。 |
| **C8** | **instgroups 拆分**：分组定义（组名→成员集合）= 用户配置、可同步，保持独立文件（集合型数据，不合入 flint.json）；`hidden`/折叠态 = 本机状态，迁 state 侧。具体文件名落位随统一批次定。 |
| **C9** | **state 伪配置迁出**：`TotalPlayTime`、`TotalPlayTimeMigrated`、`LastHostname`、`JavaSignature/Architecture/RealArchitecture/Version/Vendor`（Java 探测回写）、`UserAskedAboutAutomaticJavaDownload`、`LastOfflinePlayerName` 是运行状态不是配置，迁 state 侧；探测回写不再伪装成设置。 |
| **C10** | **统一迁移**：不逐聚类逐文件迁移——**全部聚类（config/state/data/cache）讨论完成后统一设计、一批实施**，与静态残余清理（collections 并行 14 例失败清单）合并，共用一次全量验证。 |

导入器附带决议：Prism/PolyMC/MultiMC 的 INI 导入链补齐 MultiMC 回落，`multimc.cfg` 与 `mmc.cfg` 双名同时探测（上游用 multimc.cfg，Flint 迁移检测器历史用 mmc.cfg，导入器双收编，命名分歧就此了结）。

---

## 1. 层级矩阵

列：**Sys**（所有用户层，C2 暂不实现，格式预留）、**User**（当前用户）、**Inst**（实例）；**机/同** = 本机（机器路径/硬件/网络耦合，不同步）vs 同步（可漫游）。"—" = 明确不参与。

### A. 全局设置（现 flint.cfg 全部键）

| 域 | 键 | Sys | User | Inst | 机/同 |
|---|---|---|---|---|---|
| 并发/网络 | NumberOfConcurrentTasks / Downloads / ManualRetries, RequestTimeout | ✓ 限流 | ✓ | — | 同步 |
| 代理 | ProxyType / Addr / Port / User | ✓ 企业代理 | ✓ | — | 本机 |
| 代理 | ProxyPass | — | ✓ → secret store | — | 本机·秘密 |
| 镜像 | MetaURLOverride, ResourceURLOverride, LegacyFMLLibsURLOverride | ✓ 最强系统用例 | ✓ | — | 本机 |
| API 凭据 | FlameKeyOverride, ModrinthToken | — | ✓ → secret store | — | 秘密 |
| API 定制 | MSAClientIDOverride, UserAgentOverride, PastebinType, PastebinCustomAPIBase | ✓ 部署定制 | ✓ | — | 同步 |
| 目录布局 | CentralModsDir, IconsDir, SkinsDir, JavaDir（相对 dataDir） | — | ✓ | — | 本机 |
| 下载 | DownloadsDir（绝对路径）, DownloadsDirWatchRecursive, MoveModsFromDownloadsDir | — | ✓ | — | 本机 |
| 语言 | Language, UseSystemLocale | ✓ | ✓ | — | 同步 |
| 实例列表 | InstSortMode, InstRenamingMode | — | ✓ | — | 同步 |
| 游戏时间 | ShowGameTime, ShowGlobalGameTime, ShowGameTimeWithoutDays, RecordGameTime | — | ✓ | ✓ | 同步 |
| 命令 | PreLoadCommand, PreLaunchCommand, WrapperCommand, PostExitCommand | ✓ 部署注入 | ✓ | ✓ | 本机 |
| 控制台 | ShowConsole, AutoCloseConsole, ShowConsoleOnError, LogPrePostOutput | — | ✓ | ✓ | 同步 |
| 控制台 | ConsoleMaxLines, ConsoleOverflowStop | — | ✓ | 透传 | 同步 |
| Java 路径 | JavaPath, JvmArgs, IgnoreJavaCompatibility | — | ✓ | ✓ | 本机 |
| Java 探测 | JavaSignature / Architecture / RealArchitecture / Version / Vendor | — | ✓ | 透传 | 本机·**转 state**（C9） |
| Java 自动化 | AutomaticJavaSwitch, AutomaticJavaDownload, IgnoreJavaWizard | — | ✓ | — | 同步 |
| Java 一次性 | UserAskedAboutAutomaticJavaDownload | — | — | — | **state**（C9） |
| 窗口 | LaunchMaximized, MinecraftWinWidth / Height | — | ✓ | ✓ | 同步 |
| 内存 | MinMemAlloc, MaxMemAlloc, PermGen, LowMemWarning | ✓ 依 RAM 默认 | ✓ | ✓ | 本机 |
| 原生库 | UseNativeOpenAL / GLFW / SDL, CustomOpenAL / GLFW / SDLPath | — | ✓ | ✓ | 本机 |
| 性能 | EnableFeralGamemode, EnableMangoHud, UseDiscreteGpu, UseZink | — | ✓ | ✓ | 本机 |
| 杂项 | CloseAfterLaunch, QuitAfterGameStop, OnlineFixes | — | ✓ | ✓ | 同步 |
| 环境 | Env（JSON 对象） | — | ✓ | ✓ | 本机 |
| Mods | ModMetadataDisabled, ModDependenciesDisabled, SkipModpackUpdatePrompt, ShowModIncompat, DownloadGameFilesDuringInstanceCreation, ModUpdateReleaseTypes | — | ✓ | ✓（ModDownloadLoaders 门） | 同步 |
| 状态 | TotalPlayTime, TotalPlayTimeMigrated, LastHostname, LastOfflinePlayerName | — | — | — | **state**（C9，非配置） |

### B. instgroups.json（C8 拆分）

| 内容 | 层 | 机/同 | 去向 |
|---|---|---|---|
| 分组定义（组名 → 成员实例集合） | User | 同步 | 独立文件（config 侧） |
| hidden / 折叠态（含 ungrouped.hidden） | User | 本机 | state 侧 |

### C. 实例元数据（instance.cfg 中不参与全局分层者）

| 类 | 键 | 层 | 机/同 |
|---|---|---|---|
| 配置 | name, notes, iconKey, shortcuts, JoinServerOnLaunch(+Address), JoinWorldOnLaunch, InstanceAccountId, UseAccountForInstance, AutomaticJava, CountGameTime, GlobalDataPacksEnabled, Profiler, linkedInstances, Export 族, ManagedPack 族, UseLatestMinecraftVersion(+Type) | 实例 | 同步 |
| 配置 | GlobalDataPacksPath | 实例 | 本机（路径） |
| 状态 | uuid, InstanceType, lastLaunchTime, lastTimePlayed, totalTimePlayed | 实例 | state（lastTimePlayed/totalTimePlayed 可随实例同步；uuid 不可） |
| 组件 | mmc-pack.json + patches/*.json | 实例 | 同步（实例可复制性核心） |

### D. 实例层 Override gate 参与键（C7 映射对象）

java（OverrideJavaLocation：JavaPath/JvmArgs/IgnoreJavaCompatibility）、window（OverrideWindow）、memory（OverrideMemory）、native workarounds（OverrideNativeWorkarounds）、performance（OverridePerformance）、misc（OverrideMiscellaneous）、commands、console（OverrideConsole）、game time（OverrideGameTime）、env（OverrideEnv）、mod download（OverrideModDownloadLoaders）。新格式下 gate 布尔退化为"实例层成员是否为 null"，导入时按 C7 语义折叠。

---

## 2. 格式设计方向（细节随统一批次）

1. **文件与层栈**：`<config-root>/flint.json`（user）+ 分组定义独立文件（C8，命名待定）→ `<instance-root>/<id>/instance.json`（实例，设置+元数据，拆分形状见 D4）；读序 user → instance，typed Fold。组件清单维持 mmc-pack/patches 读兼容。
2. **record 域草案**：Launcher、Console、GameTime、Window、Memory、Performance、NativeLib、Commands、MinecraftMisc（OnlineFixes/Env）、ModDownload、Java、Dirs、Language、Mirror、Proxy（无 Pass 字段）、Api（无凭据字段）、Tools。默认值 = record 属性默认，单源。
3. **秘密接口**：`ISecretStore`（Get/Set/Delete by name）+ 过渡后端 `FileSecretStore`（`data/secrets.json`，0600，原子写）；未来后端 Keychain/DPAPI/libsecret 不改接口。导入器把 INI 中三个秘密键（ProxyPass/ModrinthToken/FlameKeyOverride）搬入 store；accounts.json 非 INI 键，随 C3 另行处置。
4. **INI 导入**：`QSettingsIniCodec` 降级为导入器专用（仅读）；`flint.json` 缺失时一次性导入 INI 链（flint.json ← flint.cfg ← prismlauncher.cfg/polymc.cfg/multimc.cfg|mmc.cfg），导入后写 JSON、INI 保留不删。秘密键重定向 C4；gate=false 实例值按 C7 丢弃。
5. **FlintPaths 五根**（C1/E1 修订）：`ConfigRoot/DataRoot/StoreRoot/CacheRoot/StateRoot` 各自 required + 平台默认推导 + CLI/env 覆盖；现 DataDir 单根派生全部字段的形态退役，伪配置键迁 StateRoot 侧文件。

## 3. 开放点（随统一迁移批次定）

- record 全集定稿与 `/settings` API 分组映射（现十组 → record 域）。
- instance.cfg ↔ instance.json 双轨过渡：读 Prism 实例恒读 cfg（兼容承诺），Flint 自建/自写实例落 json 的切换时机与判定标记。
- instgroups 拆分后的文件命名与 GroupFilePath override 去留。
- ~~state 伪配置的具体收容文件~~ → 已决：`launcher-state.json` 单文件收容（含 hidden 折叠态、java 探测缓存 map 化），见 [STATE.md](STATE.md) S4/S5。
