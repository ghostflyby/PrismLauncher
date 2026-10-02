# 全局设计总纲（架构革新 · global）

状态：**决议已定**（G1–G4，2026-10-02）；基建与装配序列为统一迁移批次的约束性输入。
输入：[CONFIG.md](CONFIG.md) / [STATE.md](STATE.md) / [DATA.md](DATA.md) / [CACHE.md](CACHE.md) 四决议 + 2026-10-02 全局联动讨论。本文是统一迁移批次设计的总纲；四份聚类文档的决议原文不动。

---

## 0. 决议

### G1 根解析三层旗标（派生只存在一处）

| 层 | 旗标 | 语义 |
|---|---|---|
| 精确旗标 | `--config-dir / --data-dir / --store-dir / --cache-dir / --state-dir`（+ 各自 env） | 只改自己，**对兄弟根零隐含**；未覆盖的根走平台默认 |
| home 旗标 | `--home-dir=X` / `FLINT_HOME` | 五根 = `X/{config,data,store,cache,state}`——嵌入场景"一个目录给全所有"的唯一入口，**唯一允许隐含派生的地方** |
| 平台默认 | 全未覆盖 | 五根分居平台默认 |

- 优先级：精确旗标 > home > 平台默认。
- **可写性纪律（回应"兄弟根一定可写吗"）**：隐含建目录只发生在 home 下（宿主给 home 即声明 provision）；精确旗标零跨根假设；启动时五根逐一 EnsureDir + 可写探测，**任一根失败即拒绝启动并报出根名**——不静默降级、不半装配。
- **破格**：`--data-dir`/`FLINT_DATA_DIR` 语义从"总目录"收窄为精确 data 根（G3 授权）；总目录用法改 `--home-dir`。
- 真机旧平面目录迁移入口：`--home-dir=X` + G2 原地导入。

### G2 统一迁移引擎，自迁移一次完成

- Prism→Flint 导入管线（FileTransfer 合并语义 + 白名单）升格为**通用迁移引擎**，源 profile 化：`prism-family`（prism/polymc/multimc，现有白名单，API 驱动不自动跑）+ `flint-legacy`（旧平面布局 → home 五根挂载映射，启动探测自动跑一次）。
- flint-legacy 探测：home 下出现旧平面锚（`flint.cfg`/`metacache.json`/`instances/` 等位于根层）且无 flint-legacy 决定记录 → **一次性 eager 导入**（用户指令"迁移应该一次完成"，不做惰性双读）→ 写 `state/migration/flint-legacy.json`（S3 决定记录机制复用；prism-family 的空标记升级同 S3）。
- 导入管线**单遍完成全部分拣**：flint.json + instance.json/s + secrets.json（三键重定向）+ launcher-state.json（C9 摘键）+ groups 成员 uuid 化（D3）+ 无后缀 metacache 清理（E8）——不做三套转换器。

### G3 API v1 原地破格

这批改动全面破格（settings 强类型化、instances 寻址链、rename 语义、`/store` 新面、news 删除、`--data-dir` 语义收窄）。**v1 原地演进，不升 v2**；消费方只有用户自有工具链，随批次同步。各聚类文档不再单独声明"破坏性可接受"。

### G4 跨根一致性的所有者（设计原则：凡是拆，就要指定级联的所有者）

- **InstanceGroupModel**：config 侧 groups 文件（C8）+ state 侧 `ui.hiddenGroups`（S5）的**唯一所有者**。对外只暴露 `RenameGroup/DeleteGroup` 原子操作；内部定序"先写 config、后更新 state、失败回滚"（两文件皆小，回滚成本可忽略）。hidden 继续以**组名为键，不引入组 ID**——组名是显式用户标识，重命名本就是显式操作，跟随即可；规避级联而上组 ID 属过度设计。背景：拆分前 hidden 与成员关系同居 instgroups.json 单文件单写入者（InstanceList.RenameGroup 一并搬 `_collapsedGroups`），一致性问题是拆分（生命周期异质：分组可同步、hidden 本机）引入的显式代价。
- **秘密读写重定向**：`PUT /settings` 的 ProxyPass/ModrinthToken/FlameKeyOverride 改写 `ISecretStore`（settings wire 格式不变，宿主无感）；`RuntimeNetConfig.Apply` 与 Auth options 改从 store 读。同构教训：拆分前秘密就是 settings 键、读写同一条路，C4 分离后写路径必须显式改道，否则秘密继续落明文 config。

---

## 1. 全局基建（定义一次，各批共用）

| 基建 | 内容 |
|---|---|
| **TypedJsonFile\<T\>** | 强类型 JSON 文件基建：load-or-default + record 合并 + 原子写 + `stateVersion` 演进。覆盖：flint.json、groups 文件、instance.json、instance-state.json、launcher-state.json、migration/\*.json（secrets.json 在 FileSecretStore 内部同机制）。全部源生成注册 FlintJsonContext，AOT 冒烟清单集中管理 |
| **ResolveInstance 单一入口** | D2 链（inode→uuid→dirname）的唯一实现点；**全部**端点（含 worlds/screenshots/mods/skins 内容端点）禁直接 `GetInstanceById`——列入代码审查清单 |
| **统一测试夹具** | 五根临时目录盖章助手（TestFlintPaths），替代 E6 删除的裸夹具兜底；与静态残余清理（collections 14 例清单）、PortAllocator 协作 |
| **装配序列** | 见 §2，顺序即依赖 |

## 2. 装配序列（composition root）

1. **五根解析**（G1 优先级）+ fail-fast EnsureDir/可写探测
2. **logger**（StateRoot/logs，S4）——尽早，后续步骤皆可诊断
3. **secrets init**（data/secrets.json，0600）
4. **state 装载**（launcher-state.json + migration 决定记录读）——先于 config 导入，因为导入要向 state 摘键（C9）
5. **flint-legacy 迁移探测/执行**（G2，eager 一次）
6. **config 加载/INI 导入**（flint.json ← flint.cfg ← prismlauncher.cfg/polymc.cfg/multimc.cfg|mmc.cfg 双名；三键重定向 secrets；gate=C7 折叠）
7. **InstanceGroupModel 装配**（config groups + state hidden，G4）
8. **store/cache 挂载表注册 + 账本 `Load()`**（E6 回归上游形态）
9. **data 装配**：accounts（0600）→ instances 发现（D2 inode 索引）→ IconPool（IconsDir，D5）→ skins（SkinsDir，D6）
10. **API**（v1，G3）

## 3. 连线清单（统一批次内的具体接线）

- `JavaDir` 设置语义随 E2 指向 store；`JavaRoot` 锚从 data 移 store（JavaInstallList/JavaUtils/LaunchServices 接线）。
- dirs 组收拾：CentralModsDir 死（D8）、IconsDir/SkinsDir 由模型遵守（D5/D6）、DownloadsDir 保持本机。
- instance-state.json 与 launcher-state.json 共用 TypedJsonFile 的 `stateVersion` 演进（D4/S5 对齐）。
- 全部实例端点切 ResolveInstance；rename 流程简化落地（D3：去 LoadList 重载与手工搬组）。
- `--data-dir` 语义收窄波及 LauncherRuntime/WebHostBuilder 的解析与全部测试旗标用法。
- 旧文档落停：docs/MAPPING.md、UI-API-MAPPING.md 的相应条目随批次更新（news 删除、settings 强类型、/store 新面）。

## 4. 开放点（归统一迁移批次实施设计）

- home 布局子目录名即 `X/{config,data,store,cache,state}`（本文明示）；`FLINT_HOME` env 名。
- 各聚类文档既有开放点继续有效：base 表裁剪与 `/store/status` schema（CACHE §3）、groups 文件命名与 GroupFilePath 去留（DATA §3 / C8）、instance.cfg↔instance.json 双轨判定（C5/D4）、migration 记录 bytes 口径（STATE §4）。
