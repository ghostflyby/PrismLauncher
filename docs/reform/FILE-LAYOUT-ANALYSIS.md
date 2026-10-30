# 文件相关分析（架构革新前置设计输入）

状态：事实清单与建议，尚无设计决议。
日期：2026-10-01。
修订：2026-10-02 移入 `docs/reform/`；accounts.json 归属由 config 更正为 **data**（§2/§3 对应行失效，决议见 [CONFIG.md](CONFIG.md) C3）；instgroups.json 拆分决议见 CONFIG.md C8；§4 低效结构 #1（稳定短 ID 目录）**被 [DATA.md](DATA.md) D2/D3 取代**——实例寻址链（inode→uuid→dirname）下目录名保持人类可读，不做短 ID 目录；§2/§3 生命周期分类**被 [CACHE.md](CACHE.md) E1/E2 修订为五类**——assets/libraries/java 归新类 **store**（共享安装内容，禁自动清），§3 XDG 表相应失效；§4 #4 修正为"账本入 cache 根"（CACHE.md E3，账本跨 store+cache 描述）；§1.1 中 api-token/prism_nomigrate/logs 的落位由 [STATE.md](STATE.md) S1–S4 接管（api-token 零落盘、标记→state/migration、logs→StateRoot/logs）；§7.1 的"四分"句随五类分类失效。本文其余事实清单仍为有效设计输入。阶段索引见 [README.md](README.md)。
范围：Flint 数据目录的现状盘点（以本机 `~/Library/Application Support/FlintLauncher` 与 `PrismLauncher` 实测为准）、逐项用途与参与度、生命周期归类、XDG 映射、结构性问题与 Model 化审计。

前置背景：
- 兼容目标已收缩（2026-10-01 决策）：**只保证读 Prism 数据**；写侧解除字节兼容承诺，降为"Flint 自身往返一致"。
- Prism→Flint 数据迁移层已落地（`FileTransfer` + `PrismImportTask` + `/api/v1/migration/prism/*` 端点，CoW/Move/Copy 三模式）。
- 本文档是下一步"新目录布局 + 目录模型类型"设计的事实基础。

---

## 1. 现状清单：哪些文件是我们用到的

### 1.1 核心读写（每次启动/运行都碰）

| 路径 | 读 | 写 | 说明 |
|---|---|---|---|
| `flint.cfg` | ✓ | ✓ | 全局设置（INI）；启动时回落读取 `prismlauncher.cfg`/`polymc.cfg`/`mmc.cfg` |
| `accounts.json` (0600) | ✓ | ✓ | MSA/Mojang 账号与令牌 |
| `instances/*/instance.cfg` | ✓ | ✓ | 实例设置 |
| `instances/*/mmc-pack.json`、`patches/*.json` | ✓ | ✓ | 组件/补丁清单 |
| `instances/*/.minecraft/**` | ✓ | 游戏 | 游戏目录（存档/截图/日志/配置） |
| `instgroups.json` | ✓ | ✓ | 实例分组 |
| `metacache.json`（Flint）/ `metacache`（Prism 无后缀形态） | ✓ | ✓ | 下载缓存账本（MD5/ETag/时间戳） |
| `meta/**` | ✓ | ✓ | meta.prismlauncher.org 版本元数据 |
| `assets/indexes/** + objects/** + virtual/**` | ✓ | ✓ | Mojang 资源索引与对象库 |
| `libraries/**` | ✓ | ✓ | 共享库（Maven 布局） |
| `cache/icons/**` | ✓ | ✓ | 图标缓存 |
| `api-token` (0600) | ✓ | ✓ | API 本地令牌（Flint 专属） |
| `logs/Flint-{0..4}.log` | — | ✓ | 诊断日志（滚动 5 代） |
| `prism_nomigrate.txt` | ✓ | ✓ | 迁移"已处理"标记（Flint 专属） |

### 1.2 只读兼容边界

- 三代 QSettings INI 的**读**解析（QuSettingsIniCodec，编码侧已全 Ordinal）。
- `instance.cfg` 的 MultiMC/Prism 键名与语义、`mmc-pack.json` 旧格式、`patches/*.json` 的 MultiMC 字段。

### 1.3 携带但 Flint 核心不用

| 路径 | 原用途（Prism） | Flint 现状 |
|---|---|---|
| `catpacks/` | 界面吉祥物主题包 | 不读不写（本机为空） |
| `iconthemes/` | 图标主题包 | 不读不写 |
| `translations/` | 界面翻译（.qm） | 不读不写 |
| `skins/` | 启动器皮肤管理存储 | 不读（皮肤 API 走账号不落这里） |
| `themes/` | 界面主题包 | 不读不写 |
| `mods/` | 历史遗留的全局 mods 位置 | 空，不使用 |
| `java/`（Prism 侧） | Prism 自动安装的 JDK | Flint 有自己的 `java/` 根 |
| `metacache`（无后缀文件） | Prism 的缓存账本 | 不读（Flint 用 `metacache.json`）——迁移层应排除 |

### 1.4 垃圾

- `.DS_Store`（macOS Finder）。

### 1.5 刻意保留的 CWD/词法行为（审计已裁定不换）

| 位置 | 行为 | 保留原因 |
|---|---|---|
| `IO/FileSystem.cs:430,661,667`（NormalizePath/QDirAbsolutePath/RelativeFilePath 的 CWD 锚定） | 相对形态**用户输入**的 QDir 词法锚定 | Qt 语义 shim；读 Prism 文件仍需 `/` 分隔与词法折叠 |
| `Instances/MinecraftInstance.cs:471`（BuildLaunchServices 的 CWD 兜底） | 仅裸测试夹具（Paths 未盖章）触发 | 生产宿主恒走注入；已注释 |

---

## 2. 归类：生命周期四类

| 类别 | 内容 | 特征 | 丢失后果 |
|---|---|---|---|
| **config（配置）** | flint.cfg、instgroups.json | 用户意图 | 丢偏好/分组 |
| **data（数据）** | accounts.json、instances/**、icons/**、skins/** | 用户资产 | 数据损失 |
| **cache（缓存）** | meta/**、metacache.json、assets/**、libraries/**、cache/** | **全部可从官方源确定性重建** | 只变慢 |
| **state（状态）** | api-token、nomigrate 标记、logs/** | 运行状态/日志 | 重新登录/重看日志 |

反直觉点：`assets/`(2.3G)、`libraries/`(1.6G)、`meta/`(4.8M) 是体积大头，但严格按 XDG 语义它们是 **cache**（可确定性重建），不是 data。

---

## 3. XDG 映射

| 现位置（数据目录根下） | XDG 类别 | 目标基址（Linux 形态；macOS/Windows 由 FlintPaths 吸收） |
|---|---|---|
| flint.cfg、instgroups.json | config | `XDG_CONFIG_HOME/flint/` |
| accounts.json（0600） | config（凭据收紧权限） | `XDG_CONFIG_HOME/flint/` |
| instances/**、icons/**、skins/** | data | `XDG_DATA_HOME/flint/` |
| meta/**、metacache.json、assets/**、libraries/**、cache/** | cache | `XDG_CACHE_HOME/flint/` |
| api-token、nomigrate 标记 | state | `XDG_STATE_HOME/flint/` |
| logs/** | state（log/history） | `XDG_STATE_HOME/flint/logs/` |
| java/**（自动安装运行时） | data | `XDG_DATA_HOME/flint/java` |

要点：
- 平台差异（macOS `~/Library/{Application Support,Caches,Logs}` / Windows `%APPDATA%` 等）必须继续由 `FlintPaths` 一个类型吸收——它已经是这个角色，扩展字段即可。
- `api-token` 属持久凭据，**不进** `XDG_RUNTIME_DIR`（per-boot tmpfs，重启即失）。

---

## 4. 低效结构清单与建议

| # | 现状 | 问题 | 建议 |
|---|---|---|---|
| 1 | **实例目录名 = 实例 ID**（`instances/GregTech-NewHorizons/`） | 改名 = 重命名 3.4GB 目录；charset/冲突依赖 `DirNameFromString` 轮询去重 | **稳定 ID 目录**：`instances/<short-uuid>/`，显示名进元数据。最有价值的结构改动；迁移层是引入时机 |
| 2 | 根层 7+ 个散文件（cfg/accounts/instgroups/metacache.json/api-token/标记们） | 平面命名空间，"文件属于哪个子系统"靠约定 | 按第 3 节四类收敛为 `config/ data/ cache/ state/` 子目录（或最小化：只把 state 类挪进 `state/`） |
| 3 | `metacache`（Prism 无后缀账本）与 `metacache.json` 双格式并存 | 迁移层已把无后缀版带进目标目录，但它不被读取 | 迁移层排除清单加入 `metacache`；存量清理一次 |
| 4 | `meta/` 内容与账本（metacache.json）分居 | 同一份缓存状态的两个位置 | 账本与内容同住 `cache/meta/` 之下（内容 + 索引一体） |
| 5 | nomigrate 标记散在根层（多源后会越来越多） | `prism_nomigrate.txt`、`polymc_nomigrate.txt`… 平面铺开 | `state/migration/<source>.json`（带时间戳/选项） |
| 6 | `assets/objects/<aa>/<hash>`、`libraries/` Maven 布局 | — | **不动**：与官方源对账的契约布局 |

---

## 5. Model 化审计

| 子系统 | Model 现状 | 结论 |
|---|---|---|
| 全局设置 | `IniSettingsObject` + `SettingsObject` 层级 | ✅ |
| 账号 | `AccountList` + `AccountData`/`MsaToken` | ✅ |
| 实例 | `InstanceList` + `BaseInstance/MinecraftInstance` + `PackProfile/Component` | ✅ 最完整 |
| 版本元数据 | `MetaEntity` 族（`Index/VersionList/MetaVersion`）+ `HttpMetaCache` | ✅（双格式账本遗留待清） |
| 资源/库 | `AssetObject/AssetsIndex`、`Library` | ✅ |
| 皮肤 | `SkinModel` | ✅ |
| 任务/日志 | `TaskWork`/`TaskProgress` + `LogModel` + `RollingFileLoggerProvider` | ✅ |
| Java 运行时 | `JavaInstallList` | ✅ |
| **实例分组** | ❌ instgroups.json 解析散在 `InstanceList` 内部字典（`_groupNameCache/_instanceGroupIndex/_collapsedGroups`），无 `InstanceGroup` 模型 | **缺**，建议随 #1 稳定 ID 改造一并补 |
| **图标池** | ❌ 仅 `Icons/IconFiles.cs` 无状态助手；C++ 有 `IconList`（按 key 索引 + 文件监视） | **缺** |
| 迁移标记/状态 | ❌ 裸 `File.WriteAllBytes` 静态（`DataMigration.SetDoNotMigrate`） | 半缺——应归入迁移服务状态模型 |
| 目录布局本身 | ✅ `FlintPaths`（A 批落地） | ✅ |

---

## 6. 与四指令的对照（完成度）

| 指令 | 状态 |
|---|---|
| 目录可配置/可注入，取消相对 CWD | ✅ 生产路径完成（`FlintPaths` 全覆盖；chdir 已删）。残留：① 上表 FileSystem ×3 的相对输入锚定（刻意保留）；② MinecraftInstance 裸夹具 CWD 兜底（已注释）。可选小收尾：把 `Paths` 改为不可空 |
| 真 DI 替代静态替换 | 🔶 大部完成。已实例化：Modrinth/Flame/Technic/AtLauncher/FTB/Imgur 六平台 API、Auth 端点 options、启动管线 `LaunchServices`/`UiHooks`、`NetStack`、`FileTransfer`。残留：AtLauncher/Flame 的测试查找钩子（AtLauncherInstallTask×2、FlameCreationTask×1）、`Http` 静态 shim 的最终删除、PasteUpload/SkinUpload 直发改造 |
| 单一端口分配源 | ✅ `PortAllocator/PortLease`（TestPorts），16 文件收敛 |
| 状态局部化 | 🔶 大部完成；并行试验（collections）14 例失败的静态残余清单是验收输入 |

---

## 7. 下一步设计的输入要点

1. 新布局按 config/data/cache/state 四分；平台差异继续由 `FlintPaths` 吸收（`FlintPaths` 已是事实上的布局模型，扩展为四根字段即可）。
2. 实例改稳定 ID + 分组模型化，是结构改动里收益最高、风险也最高的一项（涉及迁移、分组、world/screenshot 端点的路径推导）——建议单独一批。
3. 迁移层已有"目标存在→跳过"的合并语义，天然支持"从旧布局迁到新布局"的同类搬运；`FlintPaths` 双布局可并存过渡（旧位置读、新位置写，惰性搬迁）。
4. 静态残余清理（collections 并行 14 例失败的输入清单）与新布局迁移建议合并为一个批次推进，避免两次全量验证。
