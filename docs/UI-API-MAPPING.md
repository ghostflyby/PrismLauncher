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
| NewsDialog | 新闻 | — | deleted（CACHE E7：GUI-bound，整体删除——checker、`GET /news`、feed base、FLINT_NEWS_RSS_URL） |
| 存储内容校验（CACHE E5 新增，无 Qt 对应页） | store 完备性派生视图（`dpkg --verify` 模型） | `GET /store/status/libraries/{versionRef}`（versionRef = net.minecraft 的 meta 版本描述符，如 `1.21.4`；走版本清单 libraries[] 逐个查账本+文件存在性 → `{complete, missing[], bytes}`）、`GET /store/status/assets/{assetsId}`（资产索引 objects 同型；对象文件按存在+尺寸核对，资产对象不走账本） | done（纯派生不落盘：清单在 `cache/meta/net.minecraft/` 与 `store/assets/indexes/` 读盘，缺清单/索引 → 404） |

设计：`GET /api/v1/settings?group=launcher|minecraft|java|proxy|api|tools` 返回该组键值；`PUT /api/v1/settings` 写回。落停后（C5/C6）：设置存储 = 强类型 `flint.json`（FlintConfigStore，record 域 + 源生成注册，原子写），端点分组只是其上的**派生视图**——分组键名保持 C++ registerSetting 线格式兼容，读写折进类型化域；秘密三元组（ProxyPass/ModrinthToken/FlameKeyOverride）走 C4 secrets.json（0600），状态键（C9）走 launcher-state.json。INI（flint.cfg/prismlauncher.cfg）只读导入、不再写回。

## 2. 实例域（instance/）

> 实例寻址（DATA D2）：所有 `/instances/{id}`、`/instances/{ref}` 路径参数统一经 **D2 链（uuid→inode→dirname）** 解析（`InstanceList.ResolveInstance` 唯一入口）——uuid 为规范引用，目录名仅展示；端点禁直接 `GetInstanceById`。

| Qt 源 | 功能 | API | 状态 |
|---|---|---|---|
| NewInstanceDialog | 原版创建/URL 导入 | `POST /instances/vanilla`、`POST /instances/import` | done |
| CopyInstanceDialog | 选择性复制 | `POST /instances/{id}/copy {name, prefs}` | done |
| MainWindow 重命名/图标/分组/删除 | CRUD | `PATCH/DELETE /instances` | done（D3：改名=纯元数据，目录名不动；引用走 D2 链 uuid） |
| IconPickerDialog | 图标库 | `GET /icons`、`POST /icons`、`DELETE /icons/{key}`、`PUT /instances/{id}/icon` | done（D5：IconPool 模型 + IconsDir 设置生效） |
| SkinManageDialog（本地库半） | 本地皮肤库 | `GET/POST /skins`、`GET/DELETE /skins/{name}`、`POST /skins/{name}/apply` | done（D6 方案 B：apply 组合账号上传 SkinUpload；SkinsDir 设置生效） |
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
