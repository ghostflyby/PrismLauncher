# State 聚类设计（架构革新 · state）

状态：**决议已定**（2026-10-02）。
事实输入：[FILE-LAYOUT-ANALYSIS.md](FILE-LAYOUT-ANALYSIS.md)（§1 现状清单、§3 XDG 映射）+ 2026-10-02 讨论；承接 config 决议的收容对象（CONFIG.md C8 hidden、C9 伪配置、C4 过渡参照）。

---

## 0. 决议

| # | 决议 |
|---|---|
| **S1** | **state root 独立可注入**（C1 五根之一，E1 修订）：`--state-dir` / `FLINT_STATE_DIR`，平台默认由 `FlintPaths` 吸收——Linux `$XDG_STATE_HOME/flint`；macOS state → `~/Library/Application Support/Flint/state`、logs 例外走 `~/Library/Logs/Flint`（平台惯例优先）；Windows `%LOCALAPPDATA%/Flint/state`（机器本地，不 roaming）。 |
| **S2** | **api-token 零落盘，宿主注入**：优先级 `--api-token`/`FLINT_API_TOKEN`（宿主全权负责客户端取 token）→ 缺省裸跑 = 临时生成（进程内持有，启动时 Information 打一行，**不写任何文件**；用户 2026-10-02 确认可行）。`--no-auth` 保留为宿主显式选择的回环信任模式。现存 `<dataDir>/api-token` 统一批次删除，不做兼容读取。语义：token 生命周期 = 进程生命周期，宿主重启即轮换；核心不拥有凭据文件（accounts 归 secret store、API 凭据归宿主）。 |
| **S3** | **nomigrate 空标记 → 迁移决定记录**：`<state-root>/migration/<source>.json`，schema `{ source, decision: "imported"|"declined", at, mode: "cow"|"move"|"copy", instances, bytes }`。`DataMigration.Decide` 改读记录；统一批次读到旧 `<source>_nomigrate.txt` → 生成 `declined@unknown` 记录并删除旧文件。收益：可审计、API 可展示上次迁移结果。 |
| **S4** | **logs = logger 后端锚点，不进状态模型**：`RollingFileLoggerProvider` 锚点从 `dataDir/logs` 改 `StateRoot/logs`（macOS 落 `~/Library/Logs/Flint`）。滚动 5 代、逐行 flush、`--log-level` 全部不动。logs 是诊断输出，不是状态存储的业务成员。 |
| **S5** | **launcher-state.json 单文件强类型**：收容 C9 伪配置 + C8 hidden + java 探测缓存（见 §2 schema）。无分层、变更即时原子写、源生成 record 注册进 FlintJsonContext（AOT 红线）。**java 探测缓存从五个全局设置键互覆盖改为路径键控 map**——顺带修掉"同机多 JVM 只记得最后一个"的真缺陷，同路径免重扫。 |
| **S6** | **取舍原则**：少量 kv + 小集合 → 单文件（launcher-state.json）；有生命周期/会增长的实体 → 每实体一文件（migration/*.json）。状态**不分层**（全为进程/机器级）；实例级运行时数据（lastLaunchTime/totalTimePlayed）归实例自己，data 聚类议题。state-root 整体注入即满足集成前提，不做文件级替换。 |
| **S7** | **顺手修**（并入统一批次）：`PrismImport.cs:213` 硬编码 `SetDoNotMigrate(…, "prism")` → 传实际源名（candidates 已支持 polymc/multimc，现状无害、属待修隐患）。 |

---

## 1. 现状机制存档（讨论依据）

- **api-token**（`Web/Services/ApiAuthService.cs`）：解析序 = `--no-auth`/`FLINT_NO_AUTH=1` 关闭 → `--api-token`/`FLINT_API_TOKEN` 显式提供（**当前会回写 `<dataDir>/api-token`**，"keep the file in sync"）→ 读现存文件 → 随机生成 32 字节落盘 0600。问题：宿主提供的 token 也被持久化，启动后任何同用户进程可永久读取。S2 后全部消失。
- **nomigrate 标记**（`Core/Migration/DataMigration.cs:178-182,254-266`）：`<dataDir>/<source>_nomigrate.txt` 空文件；写 = PrismImportTask 成功尾（硬编码 "prism"，PrismImport.cs:213）+ decline 端点按源名；读 = `DataMigration.Decide` 的 `File.Exists` → `MarkerPresent`（该源不再提议迁移）。语义 = 每源一次性决定，防重复打扰；上游 C++ 同名同位。
- **logs**（`RollingFileLoggerProvider`）：`dataDir/logs/Flint-{0..4}.log`，双 sink（控制台 + 文件），级别 `--log-level`/`FLINT_LOG_LEVEL`。

## 2. launcher-state.json schema 草案（实施批次定稿）

```csharp
public sealed record LauncherState          // stateVersion = 1 起
{
    public required int StateVersion { get; init; }
    public PlaytimeState Playtime { get; init; } = new();          // ← TotalPlayTime / TotalPlayTimeMigrated
    public bool UserAskedAboutAutomaticJavaDownload { get; init; }
    public string LastOfflinePlayerName { get; init; } = "";
    public JavaDetectState Java { get; init; } = new();            // ← LastHostname + Java 五件套（map 化）
    public UiState Ui { get; init; } = new();                      // ← instgroups 的 hidden / ungrouped.hidden（C8）
}
public sealed record PlaytimeState { public long Total { get; init; } public bool Migrated { get; init; } }
public sealed record JavaDetectState
{
    public string LastHostname { get; init; } = "";
    public Dictionary<string, JavaDetectEntry> Detections { get; init; } = new();  // key = java 可执行路径
}
public sealed record JavaDetectEntry(string Signature, string Architecture, string RealArchitecture, string Version, string Vendor);
public sealed record UiState { public IReadOnlyList<string> HiddenGroups { get; init; } = []; public bool UngroupedHidden { get; init; } }
```

读取方改造点（统一批次）：`InstanceList.MigrateTotalPlayTime`（PlaytimeSettings 钩子改状态文件）、`LaunchController` 离线名、JavaUtils 探测缓存、`InstanceList` 折叠态读写、settings 层相关键摘除。

## 3. 目标布局

```
<state-root>/
├── logs/                      # S4：logger 锚点（滚动 5 代）
├── migration/<source>.json    # S3：每源迁移决定记录
└── launcher-state.json        # S5：运行时状态单文件
```

data-dir 根清理（统一批次）：`api-token` 删除（S2）、`*_nomigrate.txt` 删除（S3）、TotalPlayTime 等伪配置键随 INI→JSON 导入时剥出（C9/C5）。

## 4. 开放点

无——本聚类决议完备。唯一实施细节（migration 记录的 `bytes` 统计口径：计划量 vs 实成量）随统一批次定。
