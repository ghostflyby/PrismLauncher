# 架构革新（reform）

Hard fork（2026-09-30，上游同步收尾于 merge b89de9aa2）后，Flint 转入内部优化与架构迁移/升级模式。本目录承载该阶段的设计输入、聚类决议与统一迁移设计。文档即决议：每份文档含决议编号（C* 等），修订须留痕。

## 阶段原则

1. **Native AOT 零警告红线**：任何格式/模型选型先过 AOT（源生成优先，禁无 TypeInfo 反射序列化）。
2. **只保证读 Prism 数据**；写侧只承诺 Flint 自身往返一致（2026-10-01 决策）。
3. **五根独立可注入**（CACHE.md E1 修订，原四根）：config/data/store/cache/state 每根启动时可单独覆盖——Flint 核心可被任意启动器界面集成。
4. **强类型一切**：配置 record 化、目录模型类型化，静态字典/字符串键尽量退役。
5. **秘密走 secret store 模型**：accounts 与配置内凭据出 config；过渡放 data（0600）。
6. **统一一批迁移**（CONFIG.md C10）：全部聚类讨论完 → 统一设计 → 一批实施（含静态残余清理），只做一次全量验证。
7. 每批真机验证 + 全量测试绿（基线 1039）。

## 聚类进度

| 聚类 | 文档 | 状态 |
|---|---|---|
| config | [CONFIG.md](CONFIG.md) | ✅ 决议已定（C1–C10，2026-10-02） |
| state | [STATE.md](STATE.md) | ✅ 决议已定（S1–S7，2026-10-02） |
| data | [DATA.md](DATA.md) | ✅ 决议已定（D1–D9，2026-10-02） |
| cache | [CACHE.md](CACHE.md) | ✅ 决议已定（E1–E9，2026-10-02；含 store 新类与账本归因） |
| 全局总纲 | [GLOBAL.md](GLOBAL.md) | ✅ 决议已定（G1–G4 + 基建 + 装配序列，2026-10-02） |
| 统一迁移 | — | 待出**批次实施设计**（以 GLOBAL.md 为总纲；五根改造、record 全集、INI 导入器、迁移引擎双 profile、静态残余清理、真死清除、缺陷修复合并一批） |

## 文档索引

- [FILE-LAYOUT-ANALYSIS.md](FILE-LAYOUT-ANALYSIS.md) — 全数据目录事实清单（2026-10-01；含 2026-10-02 修订注记）。所有聚类讨论的共同事实输入。
- [CONFIG.md](CONFIG.md) — config 聚类：现状、决议 C1–C10、层级矩阵、JSON 格式设计方向。
- [STATE.md](STATE.md) — state 聚类：决议 S1–S7、api-token/nomigrate 现状机制存档、launcher-state.json schema 草案、目标布局。
- [DATA.md](DATA.md) — data 聚类：决议 D1–D9、实例寻址链（inode→uuid→dirname）、实例文件拆分、IconPool、skins 本地库。
- [CACHE.md](CACHE.md) — cache/store 聚类：决议 E1–E9、五根与 store 新类、账本/存储分离、分层完备性 API、缺陷归因。
- [GLOBAL.md](GLOBAL.md) — 全局总纲：G1 根解析三层旗标（home）、G2 迁移引擎双 profile（flint-legacy 自迁移）、G3 API v1 原地破格、G4 跨根一致性所有者；TypedJsonFile 等基建与装配序列。统一迁移批次设计的总纲。

## 关键背景事实（速查）

- ~~体积大头 assets(2.3G)/libraries(1.6G)/meta 严格属 cache~~ → CACHE.md E1/E2 修订：assets/libraries/java 归 **store**（共享安装内容，删除=不可启动，禁自动清）；meta 才是真 cache。
- ~~实例目录名 = 实例 ID 是结构改动收益最高也风险最高的一项~~ → 已被 DATA.md D2/D3 取代：寻址链（inode→uuid→dirname）下目录名保持人类可读、改名自由，不做短 ID 目录。
- 迁移层"目标存在→跳过"的合并语义天然支持旧布局→新布局的同类搬运；可双布局并存惰性搬迁。
- 现状访问锚分散实例：flint.cfg 路径未进 FlintPaths、设置默认值双表已漂移、instgroups 无模型（散在 InstanceList 四个私有容器）。
