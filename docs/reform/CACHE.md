# Cache/Store 聚类设计（架构革新 · cache）

状态：**决议已定**（2026-10-02）。
事实输入：[FILE-LAYOUT-ANALYSIS.md](FILE-LAYOUT-ANALYSIS.md) + 2026-10-02 cache 聚类审计（含两个生产缺陷的代码级定位与归因）。

---

## 0. 决议

| # | 决议 |
|---|---|
| **E1** | **五根（C1 修订：四根 → 五根）**：config/data/**store**/cache/state 各自独立可注入，新增 `--store-dir` / `FLINT_STORE_DIR`。store 平台默认：Linux `$XDG_DATA_HOME/flint/store`；macOS `~/Library/Application Support/Flint/store`；Windows `%LOCALAPPDATA%/Flint/store`（机器本地，不 roaming）。 |
| **E2** | **新增 store 生命周期类（内容存储）**：`assets/`、`libraries/`、`java/` 从 cache/data 重新归类为 **store**——它们是"下载与安装同树"的共享安装内容（类 Steam depotcache），**删除 = 功能不可用、GB 级重下**，与 cache"丢失只变慢"本质不同。内部布局不动（Mojang objects/Maven 契约管内部形状，不管挂载根；FILE-LAYOUT #6 不受影响）。**治理语义（单独立类的存在理由）**：对备份工具"可排除（确定性可重建）但禁止自动清理"；内容寻址 → 跨机共享为未来可选能力，不做承诺。 |
| **E3** | **cache 归属收缩**：`meta/`、账本、`icons/`、`fmllibs/` 归 cache。**账本位置 = cache 根 `metacache.json`（名称保留，用户决议 2）**；meta 内容 `cache/meta/`。FILE-LAYOUT #4 修正：账本跨 store+cache 描述下载状态，不与单目录内容合住，入 cache 根。 |
| **E4** | **账本与存储分离设计**（用户指令）：存储层 = 挂载表（baseId → store/cache 内相对根）+ key→path 解析，`MetaEntry.BasePath` 运行时字段删除（已验证磁盘格式不落盘，**结构零变化**）；账本层 = 纯单文件下载状态，逻辑键 `(baseId, relativePath)`，JSON 结构保留（version "1"）。 |
| **E5** | **分层 API**：外层 = 配置完备性**派生视图**（清单驱动，不落盘）——`GET /store/status/libraries/{versionRef}`（走版本清单 libraries[] 逐个查账本+文件存在性+可选校验和 → `{complete, missing[], bytes}`）、`GET /store/status/assets/{assetsId}`（走资产索引 objects），即 `dpkg --verify` 模型；内层 = 账本条目查询（per-file 状态）。孤儿/GC 检测亦清单派生。**账本不记归属**（决议不做）：清单是"配置→文件"关系的唯一事实来源，账本记归属产生第二事实源 + 孤儿 tag GC 负担，派生遍历（万级文件）毫秒级——理由保留，派生变慢再议归属 tag。 |
| **E6** | **base 注册表单源化 + 三缺陷修复**（回归上游形态）：组合根一次注册全部 base（对齐 C++ Application.cpp:1036-1049 的启动全注册形态，Flint 侧按实际需要裁剪）+ **恢复启动 `Load()` 接线**。归因记录：①账本只写不读（`Load()` 生产零调用）②懒 base 未注册（asset_indexes/fmllibs/java → `BasePath=""` → CWD 相对路径落点）③news feed 兜底 `%TEMP%`——**均为移植引入**（C++ 原貌在案），运行时表现待红绿验证。任务内五处 `new HttpMetaCache()` 裸夹具兜底随之删除（测试夹具改盖章注入）。 |
| **E7** | **news 整体删除**（用户决议：对我们不存在）：`NewsChecker`、`/news` 端点、feed base、`FLINT_NEWS_RSS_URL` env。归因：纯 GUI 功能（C++ 仅 MainWindow 消费），按"GUI-bound 不移植"约定本不该移植。 |
| **E8** | **存量清理**：目标目录中已存在的 Prism 无后缀 `metacache` 一次性删除（迁移白名单已验证天然不含它，FILE-LAYOUT #3 的"排除"成立，仅剩清理）。 |
| **E9** | **不做**：淘汰/容量管理（store+cache 全部确定性重建，evict 端点够用）；`virtual/.lastused` TODO；账本 record 化（JSON 结构保留）。 |

---

## 1. 现状与归因存档

- **账本机制**：`HttpMetaCache` = base 注册表 + 二级字典 + 30s 延迟保存/Dispose SaveNow + EvictEntry/EvictAll（端点已有）。磁盘条目 = `base + path + md5sum + etag + 时间戳`（BasePath 为运行时字段）。
- **生产缺陷归因**（Application.cpp:1036-1049 为证）：C++ 启动注册全部 17 base 并立即 `Load()`；Flint 移植只注册 7 个（root/meta/libraries/assets/general/icons/screenshots）且丢 Load 接线。
- **base 全集**（现生产 7 + 懒 4）：asset_indexes、fmllibs、java、feed（懒，且 feed 被 NewsChecker 兜底到 `%TEMP%/flint-news-cache`）。
- **五处 `new HttpMetaCache()`**（MetaEntity.SharedCache、Libraries/AssetUpdate/LegacyFMLLibraries 的 RequireCache、NewsChecker owned）均为裸夹具兜底，生产恒走注入——非 DI 散点，E6 后删除。
- **双格式**：Prism 无后缀 `metacache` 不被读、白名单天然不含（PrismImport/DataMigration 零引用，已验证）。
- **重建性**：meta/账本/icons/fmllibs 秒级重建（真 cache）；assets/libraries/java 确定性重建但 GB 级（→ store，E2）。

## 2. 目标布局

```
<store-root>/                       # E1/E2：共享安装内容（可排除备份、禁自动清）
├── assets/                         # objects/ indexes/ virtual/ —— Mojang 契约布局不动
├── libraries/                      # Maven 布局
└── java/                           # 自装 JDK

<cache-root>/                       # 真缓存（秒级重建，可自动清）
├── metacache.json                  # E3：账本（名称保留，跨 store+cache 描述）
├── meta/                           # 元数据内容
├── icons/                          # 图标下载缓存
├── fmllibs/                        # legacy FML 库
└── …                               # general base（导入暂存等）
```

## 3. 开放点（归统一迁移批次）

- base 表最终裁剪清单（C++ 17 base 中 modplatform pack 缓存等 Flint 是否需要——导入暂存现用 general）。
- ~~`/store/status/*` 路由形状与响应 schema~~ **已定**（E5 实施批次落地）：`GET /store/status/libraries/{versionRef}`（versionRef = net.minecraft 的 meta 版本描述符，如 `1.21.4`）与 `GET /store/status/assets/{assetsId}`，响应同型 `{complete, missing[], bytes}`（`StoreStatusReport`，根类型注册进 FlintJsonContext）；派生纯读不落盘（清单/索引缺失 → 404）。
- store/data 双根在 macOS 同目录树下的嵌套（`App Support/Flint/store` ⊂ `App Support/Flint`）——纯默认值问题，实现时确认无环即可。
