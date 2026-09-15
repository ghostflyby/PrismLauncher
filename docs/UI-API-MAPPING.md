# UI → API 映射设计

> 把 Qt 界面层（Page/Dialog/Model）映射为 Flint 的 Web API 面。原则：
> 1. **Page/Dialog = 功能簇** → 一组 REST 端点；对话框的输入 = 请求体 schema，耗时操作 = 202 任务。
> 2. **QAbstractListModel** = 内部服务的数据查询 → GET 列表端点；排序/过滤下沉为查询参数（如实例列表的 search/group）。
> 3. **设置页** = 具名设置键集合 → `/settings`（全局）与 `/instances/{id}/settings`（覆盖）的键值读写；键名与 C++ registerSetting 一一对应。
> 4. **纯表现层**（主题、猫包、动画、3D 预览、布局）不映射（RESEARCH.md §8 的 GUI-bound 清单）。
> 状态标记：`done`（已有）/ `todo`（本设计新增）/ `n/a`。

## 1. 全局（MainWindow + global/）

| Qt 源 | 功能 | API | 状态 |
|---|---|---|---|
| MainWindow 实例视图/排序过滤 | 实例枚举/分组索引 | `GET /instances?search=&group=`、`GET /instances/groups` | done |
| AccountListPage | 账号列表/默认/删除 | `GET/DELETE /accounts`、`PUT /accounts/{id}/default` | done |
| AccountListPage 登录 | MSA 设备码/离线 | `POST /accounts/msa/devicecode(+poll)`、`POST /accounts/offline` | done |
| JavaPage | Java 列表/扫描 + JavaPath/JvmArgs 等键 | `GET/POST /java`；`PUT /settings java` 组 | done |
| LauncherPage | 行为键（并发任务/下载数/重试/超时） | `GET/PUT /settings`（分组） | done |
| MinecraftPage | 全局 MC 默认（内存/窗口/命令行等） | 同上 | done |
| APIPage | FlameKeyOverride/ModrinthToken/MetaURLOverride | `PUT /settings api` 组 | done |
| 鉴权（P7 收口新增） | 本地 token | `Authorization: Bearer`/`X-Api-Token` 门（--no-auth 可关） | done |
| ExternalToolsPage | JsonEditor/JProfilerPath/JVisualVMPath/MCEditPath | `PUT /settings tools` 组 | done |
| ProxyPage | ProxyType/Addr/Port/User/Pass | `PUT /settings proxy` 组 | done |
| LanguagePage / AppearancePage | 语言键可留（翻译加载不做）；外观 GUI-bound | `PUT /settings {Language}` | partial |
| NewsDialog | 新闻 | `GET /news` | done |

设计：`GET /api/v1/settings?group=launcher|minecraft|java|proxy|api|tools` 返回该组键值；`PUT /api/v1/settings` 写回。分组表 = C++ Application::registerSettings 键的子集。

## 2. 实例域（instance/）

| Qt 源 | 功能 | API | 状态 |
|---|---|---|---|
| NewInstanceDialog | 原版创建/URL 导入 | `POST /instances/vanilla`、`POST /instances/import` | done |
| CopyInstanceDialog | 选择性复制 | `POST /instances/{id}/copy {name, prefs}` | done |
| MainWindow 重命名/图标/分组/删除 | CRUD | `PATCH/DELETE /instances` | done |
| IconPickerDialog | 图标库 | `GET /icons`、`POST /icons`、`PUT /instances/{id}/icon` | done |
| NotesPage | 实例备注 | `PATCH /instances/{id} {notes}` | done |
| InstanceSettingsPage | 覆盖设置 | `GET/PUT /instances/{id}/settings` | done |
| **VersionPage** | 组件栈管理（见下） | `/instances/{id}/components` | done |
| ModFolderPage | mods 列表/启停/删除/安装/更新 | `/instances/{id}/mods` + install/update + enable/disable/delete | done |
| ResourcePack/TexturePack/ShaderPack/DataPackPage | 各资源类型列表/启停/删除 | `/instances/{id}/resources/{type}` | done |
| WorldListPage | 世界列表/安装/改名/删除 | `/instances/{id}/worlds` | done |
| ServersPage | servers.dat CRUD + 状态 ping | `/instances/{id}/servers(+/{i}/ping)` | done |
| ScreenshotsPage | 截图列表/Imgur 上传/删除 | `/instances/{id}/screenshots(+upload)` | done |
| LogPage（运行日志） | 任务日志 | `GET /tasks/{id}/log`、`/stream` | done |
| OtherLogsPage | 历史/崩溃日志文件 | `GET /instances/{id}/logs(+/{dir}/{name})` | done |
| GameOptionsPage | options.txt | `GET/PUT /instances/{id}/gameoptions` | done |
| ManagedPackPage | 托管包信息/更新检查 | `GET .../managedpack`、`POST .../managedpack/update-check` | done |
| ExportInstanceDialog / ExportPackDialog | mrpack/CF 导出 | `POST /instances/{id}/export` | done |
| ExportToModListDialog | 模组清单 | `POST /instances/{id}/export {format:"modlist"}` | done |
| modplatform 各页 | 搜索/安装/更新 | `/modplatform/*`、`/instances/{id}/mods/*` | done（资源类型参数待扩） |

### VersionPage → `/instances/{id}/components`

C++ 操作 → 端点：列表=组件数组（uid/version/name/important/customize/relevant 问题）；`Change version`=PATCH；`Install loader`=POST（forge/fabric/quilt + 版本号）；`Add empty/customize/edit`=POST/PATCH（自定义补丁 JSON）；`Revert`=DELETE 补丁文件；`Move up/down`=PATCH order；`Remove`=DELETE；jarmod/agent 导入=POST 文件。数据源：PackProfile（P2b 已全量移植）。

## 3. 实施波次

- **Wave A（实例资源面）**：worlds、resources/{type}（含 enable/disable）、servers CRUD+ping、screenshots、instances 查询参数、copy、icons。
- **Wave B（版本组件面）**：components 全套、export（mrpack/CF/modlist）、logs、managedpack、settings 分组。✅ 全部完成；另外完成了端点合并审计：`/modplatform/{provider}/search` 统一两 provider 搜索；resources 家族排除 `mods`（统一走更富的 `/mods` 面，含 enable/disable/delete）。
- 每波：stub 测试 + 全量验证 + 提交；AOT 冒烟。
