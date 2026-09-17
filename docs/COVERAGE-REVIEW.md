# 迁移覆盖复查报告（2026-09-16）

> 三路并行审计（领域核心 / modplatform / Web API vs C++ UI）的合并结论。分类：完整 / 部分 / 缺失 / n-a（GUI-bound 或被平台设施取代）。
> 总体：**核心功能面已对齐**；复查新发现一批真实缺口，按影响排序如下，供后续波次排期。

## TOP 缺口（按行为影响排序）

1. **LocalModParseTask 缺失**——无 fabric.mod.json/quilt/forge mods.toml/legacy 解析；目录扫描后 mod 的名称/版本/作者/modId 恒为空（导出的 modlist 也为空）。影响最大的单点。
2. **非 mod 资源不可从 provider 安装/搜索**——search 硬编码 ResourceType.Mod；resourcepack/shader/texture/datapack 页面的下载流端到端不可达。Core 的 DataPackUtils 等解析是死代码。
3. **ATL/FTB(new)/legacyFTB/FTBApp/Solder 安装任务无 Web 路由**——五个完整移植的核心任务没有任何调用点（仅 zip 导入可达）。
4. **可选 mod 选择无输入通道**——Flame/Modrinth/ATL 创建任务的可选内容一律按禁用安装；ATL 的 OptionalModsSelection 钩子无调用者。
5. **全局设置白名单缺口**——LauncherPage 目录/下载/控制台键、MinecraftPage 游玩/性能键、Language、Proxy 生效路径等未进 `PUT /settings` 白名单（已注册但不可写）。
6. **C# 全局默认值与 C++ 分歧**——JavaPath ""→"java"、ShowConsoleOnError/ConsoleOverflowStop true→false、PermGen 128→64、MaxMemAlloc RAM 推导→固定 4096。改变开箱行为。
7. **代理/网络配置未接**——Proxy* 键无人读；RequestTimeout/NumberOfManualRetries/NumberOfConcurrent* 硬编码。
8. **AccountList 后台刷新队列缺失**（fillQueue/tryNext 等）——过期 token 不自动续；Web 版刷新服务默认关且粒度粗。
9. **LaunchTask parseXmlLogs 未接线**——log4j XML 事件解析器存在但不在启动日志链路上，多行事件/时间戳丢失。
10. **Skins API 未暴露**——SkinUpload/SkinDelete/CapeChange/SkinModel 已移植但零路由。

## 次级缺口（摘录）

- MetaURLOverride/LegacyFMLLibsURLOverride/ResourceURLOverride 只读未注册（自定义镜像默认失效）
- LibraryUtils.findMangoHud 缺失 → MangoHud 分支插入空 LD_PRELOAD；switcheroo 探测缺失
- InstanceCopyTask 丢文件属性/exec 位（Unix 上可执行文件复制后损坏）
- FileSystem::createShortcut 缺失（快捷方式只有数据无写入器）
- Windows JavaUtils 注册表扫描被跳过（Windows 用户大量 JDK 发现不了）
- VersionPage 的 jarmod/replace-jar/agents/import-components/add-empty/download-all 端点
- World 的 copy/copy-seed/reset-icon；launch 请求无 world/server join 目标参数；servers 无排序
- ModFolderModel 的 requires/requiredBy 记账未接线；categories/changelog/description 端点缺失
- /about、/paste、metadata-cache 清理端点缺失
- Utils 类部分函数缺失：humanReadableFileSize、htmlListPatch、truncateUrlHumanFriendly、MMCTime.humanReadableDuration、SysInfo.defaultMaxJvmMem

## 明确 n/a（无需行动）

GUI 表现层（主题/猫包/3D 预览/QAIM 模型/ watcher）、LocalPeer/IPC、katabasis（已被自写 OAuth 替代）、rainbow、BaseInstaller、updater、trash 语义（用删除替代，已记录）、Windows 注册表 Java 扫描（P7 记录）。

## 与原计划对照

- MAPPING.md 中标 `ported` 的文件经方法集比对基本属实；标 `partial` 的条目与本次结论一致。
- 新发现的缺口主要来自两类：**任务类移植后的 API 接线遗漏**（ATL/FTB 安装、paste、skins）和**Qt UI 交互语义的无头化欠账**（可选 mod、代理/并发设置生效、日志 XML 解析接线）。

## 建议排期（后续波次）

- **fix-1（高影响）✅ 2026-09-16 完成**：LocalModParseTask + ModFolderModel 接线；provider search 的资源类型参数；categories/changelog/description 端点。（commit bcb793b1c，902 测试）
- **fix-2（高影响）✅ 2026-09-16 完成**：provider 路由扩展到 ATL/FTB/Technic 的包安装（含浏览/搜索）+ 可选 mod 选择输入（含导入流）+ 皮肤端点。（commit 228166d9c，945 测试）
- **fix-3（中）**：settings 白名单补全 + 默认值对齐 + 代理/并发设置生效 + parseXmlLogs 接线 + AccountList 刷新队列下沉 Core。
- **fix-4（低）**：world 补充操作、servers 排序、launch join 目标参数、paste/about/cache-evict 端点、Utils 补零。
