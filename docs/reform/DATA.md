# Data 聚类设计（架构革新 · data）

状态：**决议已定**（2026-10-02）。
修订：2026-10-02 CACHE.md E2 将 java/ 改判 store（D9/§2 已同步）；§2 补 secrets.json（CONFIG.md C4 过渡位）。
事实输入：[FILE-LAYOUT-ANALYSIS.md](FILE-LAYOUT-ANALYSIS.md) + 2026-10-02 data 聚类审计（端点硬编码、死代码、寻址地基盘点）。

---

## 0. 决议

| # | 决议 |
|---|---|
| **D1** | **data root 独立可注入**（C1 五根之一，E1 修订）：`--data-dir` / `FLINT_DATA_DIR` 已有，保留；平台默认不变（macOS `~/Library/Application Support/Flint`、Win `%APPDATA%/Flint`、Linux `$XDG_DATA_HOME/flint`），由 FlintPaths 吸收。 |
| **D2** | **实例寻址链 inode → uuid → dirname**。三层句柄：**inode**（枚举时 `stat` 实例目录建索引，P/Invoke 同 FileTransfer/clonefile 先例；零写入、改名不变、实例 cfg 损坏/NullInstance 也可寻址）→ **uuid**（instance 元数据 `uuid` 键，已存在；copy/import 已 RegenerateUuid 防克隆；**唯一允许持久化的引用形态**）→ **dirname**（兼容寻址末位 + 纯显示）。`ResolveInstance(ref)` 按序解析，未命中 404；所有端点 `{ref}` 参数统一走链。`GET /instances` 每项返回 `{ uuid, inode, dirName, name, … }`（平台不支持 inode 时为 0；Windows 首版 0，链自动跳过，NTFS FileId 可后续补）。**inode 纯 local 不落盘**（用户确认）：rename 不改 inode，停运期间改名重启后照常命中；失效场景仅跨文件系统/跨机与 inode 复用（删除重建）——同步/备份/迁移一律以 **uuid** 为准；持久化引用全 uuid，天然免疫。边界：纯数字目录名与某 inode 相同 → inode 胜出（文档化，概率趋零）。 |
| **D3** | **放弃"稳定短 ID 目录"方案**（取代 FILE-LAYOUT-ANALYSIS #1 建议）：目录名保持人类可读（`DirNameFromString` 轮询去重降级为命名美观问题）；同卷 rename 是 O(1) 元数据操作，引用断裂（原 #1 的真痛点）由 D2 链解决。连带：`PATCH /instances/{ref}` 改名不再需要 LoadList 重载 + 手工搬组（InstanceEndpoints.cs:311-328 消失）；**groups 文件成员从 dirname 改 uuid 引用**（C8 落位细化）；`linkedInstances` 内容改 uuid（导入时转换）；API 响应 `id` 语义变化属破坏性变更（内部项目可接受，dirname 兜底保证旧客户端不断链）。 |
| **D4** | **实例文件拆分，与核心配置同构**（用户指令"拆分形状和核心配置一样"）：<br>`instance.json` — record 域强类型（Identity{uuid,name,iconKey,notes} / Launch{java,memory,window,commands,env,performance,nativeLib} / Managed / Export / Misc{joinServer,linkedInstances,…}），源生成注册 FlintJsonContext；<br>`instance-state.json` — lastLaunchTime / lastTimePlayed / totalTimePlayed（本机，对齐 S5 launcher-state.json 形态）；<br>`mmc-pack.json` + `patches/*.json` — 组件清单不动（读兼容承诺）；<br>`.minecraft/` — 游戏自理。<br>config/state 同构映射：instance.json 可同步、instance-state.json 本机；uuid 归 Identity（身份不是状态）；读 Prism 实例仍解析 instance.cfg（只读兼容），INI 导入按 C7 gate 语义折叠；instance.cfg↔instance.json 双轨判定标记仍归统一批次（CONFIG.md 开放点）。 |
| **D5** | **建立 IconPool 模型**：对齐 C++ IconList 能力（key→path 索引、目录扫描、安装/删除），注册 DI，替换 `IconFiles` 静态助手；**`IconsDir` 设置开始被遵守**——修掉 SettingsAndIconsEndpoints.cs:196/213/247 三处 `Path.Combine(DataDir, "icons")` 硬编码（"错误未使用的配置应该使用"，用户指令）。图标 = data（用户资产，同步可）。 |
| **D6** | **skins 激活最小本地库**（用户选 B）：`GET/POST/DELETE /skins`（列表/存入/删除，`SkinListStore` 持久化半边现成）+ 套用 = 组合现有账号上传端点；`SkinsDir` 设置被遵守。账号皮肤面（Mojang 直调的 `/accounts/{profileId}/skin|cape`）不动。 |
| **D7** | **accounts 落位（受托决策）**：保持 `<data-root>/accounts.json`、0600、`FlintPaths.AccountsFile` 锚不变——它是列表模型不是 kv 秘密，**不并入** secrets.json（C4 过渡位）；ISecretStore 落地时仅 MSA token 字段迁入 store，账号元数据留原文件。 |
| **D8** | **真死清除（忽视、不再写出，用户指令）**：`CentralModsDir`（Core 零消费——停止注册 + 从 API dirs 组移除）、MetaCache "screenshots" 死 base（LauncherRuntime.cs:92）、`versions/` 休眠锚（`FlintPaths.VersionsRoot` 属性 + `MinecraftInstance.VersionsPath` 兜底一并删）。迁移白名单中的 legacy 条目（accounts/、mods、themes）是**读 Prism 过滤器**，属读兼容，不动。 |
| **D9** | **其余现状确认无动作**：`instances/.tmp/` staging 机制保留（提交改名逻辑随 D3 简化）；cache/icons 与 data icons 双层保持分居（缓存 vs 资产）。~~java/ 不动~~ → 已被 [CACHE.md](CACHE.md) E2 改判 **store**（2026-10-02 E2 修订）。 |

---

## 1. 审计存档（讨论依据）

- **寻址地基**：`uuid` 键已存在（BaseInstance.cs:86-93，空则自动生成），`GetInstanceById` 已支持 uuid 匹配（InstanceList.cs:224）——D2 是把既有事实升格为寻址契约。
- **改名现场**（D3 消除对象）：PATCH → SetName → `SyncInstanceDirName` → `LoadList()` → 手工搬组（InstanceEndpoints.cs:289-329）。
- **死代码/死设置清单**（D8 处置）：CentralModsDir（注册+API 暴露、零消费）、SkinListStore（零消费 → D6 激活）、MetaCache screenshots base、versions 锚。
- **错误未使用**（D5 处置）：IconsDir 设置不被图标端点遵守。
- **混装平面**（D4 处置）：instance.cfg 同时承载设置/元数据/运行时数据。

## 2. 目标布局

```
<data-root>/
├── accounts.json (0600)            # D7：不动
├── secrets.json (0600)             # C4：秘密过渡位（FileSecretStore；宿主同步 data 时应排除本文件与 accounts.json）
├── icons/                          # D5：IconPool 锚点（IconsDir 设置可改）
├── skins/                          # D6：本地皮肤库（SkinsDir 设置可改）
└── instances/
    ├── <人类可读名>/                # D3：目录名=显示，改名自由
    │   ├── instance.json           # D4：设置+元数据（可同步）
    │   ├── instance-state.json     # D4：运行时数据（本机）
    │   ├── mmc-pack.json + patches/  # 不动
    │   └── .minecraft/             # 游戏自理
    └── .tmp/                       # staging，保留
```

## 3. 开放点（归统一迁移批次）

- instance.cfg ↔ instance.json 双轨判定标记（CONFIG.md 开放点，与 C5 导入器合流）。
- groups 独立文件的命名与 `GroupFilePath` override 去留（C8 残留）。
- inode 索引的重建时机细节（仅 LoadList vs 目录事件）——首版随 LoadList，够用。
