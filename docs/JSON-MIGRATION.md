# JSON 迁移：原则与批次

> 状态：进行中。本文档记录 JSON 迁移（JsonObject/JsonNode 手工读写 → 类型化探针 + 源生成序列化）的**约束性原则**与批次台账。原则由项目所有者裁定，不允许以"测试有没有断言"为由遗忘或倒退。

## 1. 约束性原则

**判据只有一个：断言的结果的消费者是否存在。**

- 面向**外部行为**（跨启动器文件、第三方协议、外部程序要读的形状）→ 该行为保留。
- 面向**内部使用** → 迁移到更加合理的失败行为，并用**自定义 Converter 保持输入兼容**。
- "**错误面固定**"（错误文案、逐字段降级、字节形状是我们自己写自己读）**不构成保留理由**——那是内部失败路径。
- **读兼容，不保证写兼容**：读必须解析外部/历史文件（含上游写出的全部真实形状，用 Converter 保持）；写形状自由，我们写什么就是什么。
- **凡是有固定结构化格式且我们要消费的，都使用数据类型和序列化**。游戏和加载器本身不容忍奇怪格式——"启发式读取"不是豁免。
- **不透明子区域建模成字典**（动态键 → `Dictionary<string, T>`；真正的多态片段 → `JsonElement` + mapper）。
- 测试有没有断言**不是判据**。钉住内部行为的断言随迁移更新为新契约。

## 2. 源生成器事实（已验证，勿再踩）

1. 嵌套 `JsonSerializerContext` 无生成元数据（CS0534）——context 必须命名空间层级、一文件一声明。
2. `[JsonExtensionData]` 属性不能 init-only（报误导性的 ctor 绑定错误）。
3. **init 属性 + 命名策略/参数化 creator 的真正坑是命名碰撞**：getter-only 计算属性按策略取名，与显式 wire 名相撞时 Configure 抛 InvalidOperationException。计算视图一律 `[JsonIgnore]`；**与 init-only 无关**（init 全保留）。
4. 集合/字典的**元素** null 不被 `RespectNullableAnnotations` 覆盖——mapper 入口需 `?? throw` 守卫。
5. `JsonConverter` 类定义不生效——必须 `[JsonConverter]` 挂到属性；**ctor 参数绑定路径用类型级 converter（Dictionary 子类）最稳**。

## 3. 已完成批次

| 批次 | 提交 |
|---|---|
| Modrinth 全链（snake 拆分）、Flame index、ATL/Technic/FTB、metacache、版本格式读侧、命名策略批量、审查修复、instgroups、meta 三层、blob 升型 | `344727916`…`e91f667cf`、`79af88df8`、`85e448388`、`6a13cf659` |
| ProfileUtils override-order | `54d56b032` |
| blob converter 双形态读专项测试 | `21695cd95` |
| 批次1 accounts.json 读写类型化（AccountWire + 字节门退役） | `a82e3859f` |
| 批次2 Qt 写尾迁移（版本格式 *OutJson、mmc-pack、instgroups Save、皮肤 index、mod-list 导出、paste.gg） | `191fef389` |
| 批次3 mod 信息读取类型化（ModMetadataWire、pack.mcmeta/ChatComponentJson、Technic version.json、LegacyFtb pack.json、status ping） | `9fd411303` |
| 批次4 Flame 指纹流类型化 + ParseRequires/SerializeRequires 收敛（单一 MetaRequireJson wire） | （本提交） |

## 4. 剩余批次

（全部完成。后续新格式按 §1 原则直接走类型化。）

判定**保留**的有据清单：settings 动态形状、`QtJson/JsonUtils` 基础设施（含 FlameJson 剩余用户：categories、导出指纹流）、auth 协议响应探测助手（`AccountData.GetString(JsonNode)` 家族——服务 MSA/XSTS 步骤，非 accounts.json）。

## 5. 验证

每批次：`dotnet build` + `dotnet exec FlintLauncher.Tests/bin/Debug/net10.0/FlintLauncher.Tests.dll` 全量绿（本机 `dotnet test` SDK 包装器有管道问题，勿用）。
