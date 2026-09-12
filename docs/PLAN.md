# Flint — 迁移计划

> 目标：把 Qt 版 PrismLauncher 逐步迁移为支持 Native AOT 的 ASP.NET Core Minimal API 启动器核心，功能操作级对齐。
> 研究依据见 [RESEARCH.md](RESEARCH.md)；每个阶段推进时必须同步更新 [MAPPING.md](MAPPING.md)。

## 1. 工程结构

每个 .NET 项目对应仓库顶级一个 `FlintLauncher.*` 目录，csproj 与源码直接放在该目录下（slnx 与项目分离）：

```
FlintLauncher.slnx                  # 仓库根
Directory.Build.props               # 通用属性（TFM/Nullable/分析器）
docs/                               # 本文档 + RESEARCH + MAPPING + 开发工具
FlintLauncher.Core/                 # 领域核心（无 ASP.NET 依赖；IsAotCompatible）
FlintLauncher.Nbt/                  # 自研 NBT 读写（P3）
FlintLauncher.Web/                  # Minimal API 宿主（PublishAot）
FlintLauncher.Tests/                # xUnit；夹具复制自原 tests/testdata
```

- 命名空间与程序集同名：`FlintLauncher.Core.*` 等；C# 文件头部加 `// SPDX-License-Identifier: GPL-3.0-only`。
- 不镜像 Qt 目录结构；但 MAPPING.md 维护「原 C++ 文件 ↔ C# 文件」对应关系，每个 C# 类型注释注明移植来源（如 `// Port of launcher/settings/INIFile.{h,cpp}`）。

## 2. 阶段划分与退出标准

| 阶段 | 内容 | 退出标准 |
|---|---|---|
| **P0 骨架** ✅ | slnx + FlintLauncher.Core/FlintLauncher.Web/FlintLauncher.Tests；FlintConfig（BuildConfig 移植）；`/healthz`；AOT publish 冒烟 | `dotnet build`、`dotnet test`、AOT 发布 + 本地跑通 healthz |
| **P1 基础设施** | 移植 utils：`Version`(版本序)、`FileSystem`、`GZip`、`Json`、`StringUtils`、`MMCZip`、`Markdown`；INI(QSettings 兼容) + Settings 框架（Setting/Override/gate）；Task 框架异步化；Net 栈（HttpClient + HttpMetaCache + Validator） | 对应 C++ 单测全部移植并通过；AOT publish 通过 |
| **P2 元数据与版本模型** | `meta/`（Index/VersionList/Version/BaseEntity sha256 缓存）；`MojangVersionFormat`/`OneSixVersionFormat`/`Library`/`GradleSpecifier`/`Rule`/`LaunchProfile`/`Component`/`PackProfile`/`ComponentUpdateTask`；`VersionFilterData` | 能对任意 MC 版本离线/在线构建 LaunchProfile；MetaComponentParse/Library/MojangVersionFormat 测试通过 |
| **P3 实例域** | `FlintLauncher.Nbt`；`BaseInstance`/`MinecraftInstance`/`InstanceList`/instance.cfg；Vanilla 创建/复制/删除/分组/图标文件；World(level.dat)/servers.dat 读写；资源模型（mod/respack/shader/texture/datapack 解析） | 指向现有 Prism 数据目录可枚举实例并读出设置；WorldSaveParse/ResourcePackParse 等测试通过 |
| **P4 账户** | AuthFlow（device-code 优先，后补本地回调授权码）；token 刷新；AccountList/accounts.json 互操作；皮肤/披风 API；离线账号 | device-code 登录→刷新→accounts.json 与原版互通 |
| **P5 启动** | LaunchTask/步骤引擎全链路；Java 扫描(macOS 先行)/Checker/下载；库/资产下载；NewLaunch.jar 复用与打包；LoggedProcess 等价物+日志模型+脱敏；实例状态(运行中/上次启动) | macOS 上真实启动原版实例进游戏；全过程日志经 API 可订阅 |
| **P6 modplatform** | Modrinth 全套→CurseForge(指纹 murmur2)→Packwiz(TOML 决策)→ATL/FTB/Technic/legacyFTB/FTBApp；导入导出（zip/mrpack/manifest）；资源搜索/安装/更新 API；ExportToModList | 从 Modrinth/CF 导入整合包并启动；mod 更新检查可用 |
| **P7 周边+API 面收口** | screenshots/imgur、news、paste 上传、gameoptions、notes、managed-pack 元数据、DataMigrationTask(全局 cfg 导入)、profiler 外部工具；API 错误模型/鉴权(本地 token)/OpenAPI(评估 AOT)；多平台 Java 扫描(Windows/Linux) | 功能矩阵与原应用对齐复查完毕 |

说明：阶段内长期处于「仅编译通过」属预期；每阶段结束必须达到该阶段退出标准再合并。

## 3. 映射与提交管理

- **MAPPING.md** 按模块维护三态：`ported` / `partial` / `todo`，另有 `GUI-bound, not ported`。新增 C# 文件时同步登记；阶段合并前复查。
- 每个阶段至少一个提交；子任务粒度建议 1-3 提交/任务，避免长生命周期未提交工作。提交信息用约定式：`flint(p1): port INIFile with QSettings compat tests`。
- 分支：`FlintLauncher` 为集成分支；风险实验开 `flint/<topic>` 分支，验证后合回。当前阶段（P0/P1 常规推进）可直接提交到 `FlintLauncher`。
- 原 C++ 测试的 testdata 直接复制到 `FlintLauncher.Tests/TestData/` 并保留原文件名，便于对拍。

## 4. API 设计约定（初步）

- 路由前缀 `/api/v1/`；资源命名对齐领域名词：`/instances`、`/instances/{id}/components`、`/meta/net.minecraft/versions`、`/accounts`、`/java`、`/modplatform/modrinth/search`…
- 长时操作（下载、导入、启动）创建「任务」资源：`POST .../x` → `202 + {taskId}`，`GET /tasks/{id}` 查进度，SSE（`/tasks/{id}/events`）订阅日志/进度；任务模型即原 Task 框架的跨进程投影。
- 启动器配置经 `/settings` 与 `/instances/{id}/settings` 读写，语义等同 Setting/OverrideSetting（含 gate）。
- JSON 命名 camelCase；错误体统一 `{ "error": { "code", "message", "detail" } }`。

## 5. 风险清单

| 风险 | 应对 |
|---|---|
| QSettings INI 兼容细节（转义/引号/base64 迁移） | 以原 INIFile_test.cpp + 真实 instance.cfg 样本做夹具；读三格式、写 1.3 |
| Tomlyn AOT 兼容性 | P6 初先写 AOT publish 探针；不行手写 packwiz 子集 |
| NewLaunch.java 构建链（需 JDK） | 开发期编译产物入 resources；CI 补 javac 步骤；备选：直发 classpath 启动（放弃 EntryPoint 注入语义需评估 legacy jarmod 影响） |
| Xbox/XSTS 鉴权细节（设备码流、token 刷新窗口） | 以原 AuthStep 请求/响应字段为准逐字段移植；先用真账号走通 |
| 进程树终止（Windows） | P5 末补 Job Object；macOS/Linux process group |
| 大文件下载进度与并发节流 | Net 栈设计阶段定：HttpClient + SemaphoreSlim + 每请求 Sink 抽象 |
| Qt 语义遗漏（如 `PSaveFile` 原子写、路径规范化） | 移植时逐文件核对 MAPPING；可疑处加测试 |
