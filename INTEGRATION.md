# Custom Channels（自定义渠道）集成说明

本仓库是 [Devolutions/UniGetUI](https://github.com/Devolutions/UniGetUI) 的 fork，在 Avalonia 版 UI 上增加了
「自定义渠道」扩展：把 GitHub/GitLab Release、OpenList、百度网盘、夸克网盘与 HTTPS 直链作为应用更新来源，
并把更新任务接入 UniGetUI 现有的底部操作列表。

上游同步策略：`main` 仅镜像上游，所有自研提交都在 `custom` 分支，CI 每日自动合并上游正式 Release tag。

---

## 1. 功能范围

保留（用户必需）：

- 渠道：GitHub Release、GitLab Release（含自建实例）、OpenList（外部实例 / 本机托管）、百度网盘、夸克网盘、HTTPS 直链
- 多版本列表与更新日志（Release Notes）预览、README 展示
- 压缩包解压（含密码尝试与失败后询问）、单文件原子替换、ZIP 覆盖、MSI / EXE 安装、备份与回滚、受保护目录提权
- 断点续传、分片并发、限速、SHA256 校验、网盘自动转存与清理
- 任务进入 UniGetUI 操作列表，点击卡片查看分阶段日志

放弃 / 简化（无法自然融入 UniGetUI 的部分）：

- 独立的主窗口与自绘任务时间线（改为复用 UniGetUI 操作列表与日志详情）
- WPF 专用实现（Markdig → FlowDocument、WebView2 内嵌登录、WPF 对话框）
- 网盘登录改为「系统浏览器登录 + 粘贴 Cookie」（跨平台且不依赖 WebView 运行时）

---

## 2. 代码布局（新增代码全部独立）

| 路径 | 说明 |
| --- | --- |
| `src/UniGetUI.Extensions.CustomChannels/` | 核心库（`net10.0`，与界面无关）：配置、渠道、下载、解压、安装、提权、网盘、OpenList、版本策略、日志/密钥/HTTP 基础设施 |
| `src/UniGetUI.Extensions.CustomChannels.Tests/` | xUnit 单元测试（110 个用例，全部纯逻辑） |
| `src/UniGetUI.Avalonia/Integrations/CustomChannels/` | 集成在同一程序集内的 UI：页面、设置页、操作任务、编辑器/密码/网盘登录对话框 |
| `config/custom-channels/targets.sample.json` | 配置示例（JSONC） |
| `scripts/custom-channels/verify-integration.ps1` | 合并上游后自检集成锚点 |
| `scripts/custom-channels/make-installer.ps1` | 复制上游 `.iss` 生成无签名 fork 安装包 |
| `scripts/custom-channels/make-productinfo.ps1` | 生成自更新清单 `productinfo.json` |
| `.github/workflows/upstream-sync.yml` | 每日检查上游 Release、合并、冲突建 Issue、写状态文件 |
| `.github/workflows/custom-channels-build.yml` | 构建 / 测试 / 打包 / 发布（含自更新清单） |

数据目录：`%LOCALAPPDATA%\UniGetUI\custom-channels\`（配置 `settings.json`，以及 `cache/ downloads/ staging/ backup/ logs/ state/ tools/openlist/`）。
配置可用环境变量 `UNIGETUI_CUSTOMCHANNELS_ROOT` 覆盖（测试用）。凭据（网盘 Cookie、OpenList 口令）使用 Windows DPAPI（CurrentUser）加密保存。

---

## 3. 上游文件的接线点（每处均带 `[CustomChannels]` 注释）

| 文件 | 改动 |
| --- | --- |
| `src/UniGetUI.Avalonia/Views/MainWindow.axaml.cs` | `PageType` 末尾新增 `CustomChannels` |
| `src/UniGetUI.Avalonia/Infrastructure/AppExtensions.cs` | `using` 新增；定义 `CustomChannelsExtension`；加入 `AppExtensions.All`（放在最前，使侧边栏位于「软件包合集」之后） |
| `src/UniGetUI.Avalonia/Views/SidebarView.axaml` | `Bundles` 之后插入 `CustomChannelsNavBtn` |
| `src/UniGetUI.Avalonia/Views/SidebarView.axaml.cs` | `NavButtonFor` / `SyncListBoxSelection` 两个 switch 各加一行 |
| `src/UniGetUI.Avalonia/ViewModels/SidebarViewModel.cs` | `SetExtensionEnabled` 中触发导航重算 |
| `src/UniGetUI.Core.Settings/SettingsEngine_Names.cs` | 设置键 `EnableCustomChannels` 及映射 |
| `src/UniGetUI.Interface.Enums/Enums.cs` | `IconType.CustomChannels` |
| `src/UniGetUI.Avalonia/Views/Controls/Settings/SettingsPageButton.cs` | 图标映射到 `Sources.svg` |
| `src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj` | 新增 `ProjectReference` |
| `src/UniGetUI.Avalonia.slnx`、`src/UniGetUI.Windows.slnx` | 登记两个新工程 |
| `src/UniGetUI.Avalonia/Infrastructure/AvaloniaAutoUpdater.cs` | fork 清单地址；`FORK_ALLOW_UNSIGNED_INSTALLERS` 放行未签名安装包（SHA256 仍强制） |

导航顺序无需改 `NavigationPageOrder`：`DefaultPages` 对扩展使用 `_ => extension.IsEnabled` 兜底，
且已保存的导航顺序会通过 `Resolve` 自动追加新页面。

设置页无需改 `ExtensionsHomepage`：扩展设置页列表由 `AppExtensions.All` 数据驱动。

---

## 4. 合并上游

```powershell
git fetch upstream --tags
git checkout custom
git merge --no-ff v2026.x.y
pwsh ./scripts/custom-channels/verify-integration.ps1
dotnet build src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj
dotnet test src/UniGetUI.Extensions.CustomChannels.Tests/UniGetUI.Extensions.CustomChannels.Tests.csproj
git push origin custom
```

冲突一般只会出现在上表的 11 个接线点。处理原则：

1. 先接受上游改动，再补回 `[CustomChannels]` 片段（`git diff upstream/main -- <file>` 可快速定位）。
2. 若 `AbstractOperation` / `AppExtension` / `OperationViewModel` 的签名变化，只改
   `Integrations/CustomChannels/CustomChannelsOperation.cs` 与 `CustomChannelsExtension` 定义。
3. `AvaloniaAutoUpdater.cs` 的 fork 分支若与上游冲突，保证两点：清单地址指向本仓库 Release、
   `FORK_ALLOW_UNSIGNED_INSTALLERS` 分支仍在（否则自动更新会因缺少 Devolutions 证书而失败）。
4. 自检脚本通过后再推送；CI 会再次执行同样的检查。

自动化：`.github/workflows/upstream-sync.yml` 每日 04:00 UTC 检查上游最新正式 Release，
成功则合并并触发构建；冲突则 `git merge --abort`、创建/更新 `upstream-sync` 标签的 Issue，
并把 `{ upstreamTag, merged, conflicts, issueUrl, updatedAt }` 写入 `sync-status` 分支的 `sync-status.json`。
应用启动时会读取该文件（`raw.githubusercontent.com/<owner>/<repo>/sync-status/sync-status.json`），
冲突或构建失败时在窗口顶部显示 InfoBar 横幅。

---

## 5. 自更新（fork 发行版）

- 清单地址：`https://github.com/luqin2007/UniGetUI/releases/latest/download/productinfo.json`
  （主机 `github.com` 命中上游 URL 白名单；`latest` 只解析正式 Release）。
- 清单键名沿用 `Devolutions.UniGetUI`，结构 `{ Current: { Version, Files[{Arch,Type,Url,Hash}] } }`，
  Windows 资产类型为 `exe`（安装包）与 `zip`（便携版），Hash 为 SHA256 小写十六进制。
- 版本号 = 上游最新正式版 + 构建号，例如 `2026.3.3.12`（4 段保证单调递增）。发布由
  `scripts/set-version.ps1` 写入程序集与安装脚本。
- 安装包不签名：fork 无法获得 Devolutions 的 Authenticode 证书，因此
  `AvaloniaAutoUpdater.CheckInstallerSignerThumbprint` 在 `FORK_ALLOW_UNSIGNED_INSTALLERS` 下放行，
  **SHA256 校验与 HTTPS 仍强制执行**。首次安装可能触发 SmartScreen 提示。
- 安装包由 `make-installer.ps1` 从上游 `UniGetUI.iss` 生成 `UniGetUI.fork.iss` 后编译，
  不修改上游脚本（`UniGetUI.fork.iss` 已在 `.gitignore` 中）。

发布流程（CI 自动完成）：`dotnet publish`（NativeAOT，失败回退自包含）→ Inno 打包 →
`UniGetUI.x64.zip` → `checksums.txt` → `productinfo.json` → `gh release create`。

---

## 6. 本地开发

```powershell
# 需要 .NET 10 SDK（global.json 锁定 10.0.4xx）
dotnet build src/UniGetUI.Extensions.CustomChannels/UniGetUI.Extensions.CustomChannels.csproj
dotnet test  src/UniGetUI.Extensions.CustomChannels.Tests/UniGetUI.Extensions.CustomChannels.Tests.csproj
dotnet run   --project src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj
```

启动后：侧边栏「自定义渠道」→ 添加应用；「设置 → 扩展 → 自定义渠道」配置 OpenList/网盘/下载项。
扩展默认开启（首次启动写入 `EnableCustomChannels=true`），关闭后可在设置中重新启用。

---

## 7. 已完成的端到端验证

- 本地：核心库单测 110/110 通过；`UniGetUI.Avalonia` 编译 0 错误；启动应用后侧边栏出现「自定义渠道」，
  页面可加载示例配置、发起检查、展示版本/说明/README 与安装按钮（已截图确认布局）。
- CI（`custom-channels-build.yml`）：`dotnet publish ... Win-x64-NativeAot` 成功，安装包、便携 zip、
  `checksums.txt`、`productinfo.json` 生成并发布为 Release `v2026.3.4.4`。
- 自更新清单可访问：`https://github.com/luqin2007/UniGetUI/releases/latest/download/productinfo.json`
  返回 `Version=2026.3.4.4` 与两个 Windows 资产（SHA256 与 Release 资产一致）。
- 上游同步（`upstream-sync.yml`）：手动触发成功，识别到上游 `v2026.3.4` 已包含，写入
  `sync-status` 分支的 `sync-status.json`（`merged: true`）。
- 未自动验证：真实旧版本安装 → 自动更新安装（需要实际安装旧包并接受 SmartScreen 提示）；
  合并冲突路径（Issue 创建/横幅提示）仅做代码审查，可在需要时用一次人为冲突触发验证。

## 8. 仓库设置要点

- fork 的**默认分支已改为 `custom`**：GitHub 只在默认分支上运行 `schedule` 与 `workflow_dispatch`，
  因此定时同步与手动触发都要求 `custom` 为默认分支；`main` 保持上游镜像用途。
- Actions 已启用，工作流令牌具备写权限（`contents/issues/actions: write`）。

## 9. 已知限制

- 网盘登录使用「系统浏览器 + 粘贴 Cookie」，不内置 WebView；Cookie 过期需重新登录。
- 编辑器只覆盖常用字段（安装方式、路径、版本读取、密码、渠道），`PathMappings`、`TokenEnv`、
  转存覆盖、`ExtractSubPath` 等高级项仍通过 `settings.json` 编辑。
- 上游 `DownloadOperation` / `PackageOperations` 与 `IPackage` 强耦合，本项目只复用其操作展示层，
  下载与安装沿用自有实现（已支持断点续传、加密解压、原子替换与提权）。
- 若上游把 Avalonia 版重新迁回 WinUI，需要重写 `Integrations/CustomChannels` 的 UI 层；
  核心库与 CI 不受影响。

---

## 10. 迭代总结与收尾（2026-11-14）

### 10.1 本次改动

| 项目 | 内容 |
| --- | --- |
| README 显示 | `SelectedCardReadme` 在选中历史版本时回退到「最新一个带 README 的版本」；新增 `SelectedReadmeTitle` 与之配套，标题与内容不再错位 |
| README 空白 | `CustomChannelsPage` 在 README 为空时隐藏标题与内容框，不再出现空区块 |
| 添加应用后的引导 | 安装根目录不存在时：自动选中新卡片 → 检查一次 → 取首个可安装候选 → 弹「立即安装 / 稍后」；确认后同步 `SelectedVersion` 再调用 `Install`，使界面选中状态与实际安装一致 |
| 无候选版本 | 不再静默返回，改为 `_shell.Notify("暂时无法安装", ...)`，并带上渠道返回的错误信息 |
| 构建修复 | `MarkdownPresenter` 中 `Avalonia.Controls.Primitives.ScrollBarVisibility` 被解析为 `UniGetUI.Avalonia.Controls...`，导致整个项目编译失败；改为 `using Avalonia.Controls.Primitives;` + 非限定名 |

相关文件：`CustomChannelsViewModel.cs`、`CustomChannelsPage.cs`、`MarkdownPresenter.cs`（新增 `ConfirmDialog.cs`、`MarkdownPresenter.cs` 也在此次一并入库）。

### 10.2 验证与发布

- 本地：`dotnet build src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj` → 0 错误（仅历史警告）。
- 提交：`af54232 fix(custom-channels): fall back to latest README, offer install right after adding an app`。
- CI：`Build fork release`（run `43597129266`）成功，`Unit tests (custom channels)` 与 NativeAOT 发布均通过。
- 发布：Release `v2026.3.4.11`（`Custom Channels 2026.3.4.11`），含安装包、便携 zip、`checksums.txt`、`productinfo.json`。

### 10.3 仓库位置变更

本仓库原先嵌套在旧的 AppUpdater 仓库里（`c:/Dev/projects/program_manager/UniGetUI`）。
旧的 AppUpdater 项目（源码、测试、本地 Git 历史，无远端备份）已按要求删除，本仓库已迁出至：

```text
c:/Dev/projects/UniGetUI
```

迁移后状态：工作区干净，`HEAD = af54232`，`origin` 仍指向 `https://github.com/luqin2007/UniGetUI.git`。

### 10.4 收尾清单

- [ ] 把 IDE 工作区切换到 `c:/Dev/projects/UniGetUI`（当前仍指向已清空的 `program_manager`）。
- [ ] 工作区切换后删除遗留空目录 `c:/Dev/projects/program_manager`：它被 IDE 占用，
      `Remove-Item` 与回收站操作均被拒绝（`being used by another process`）。
- [ ] 确认 `git status` 为空、`git log -1` 为 `af54232`，再按需重新构建。
- [ ] 可选：把 `Integrations/CustomChannels/*.cs` 统一转为 UTF-8。这些文件当前是 UTF-16，
      Git 视为二进制，`git diff --stat` 显示为 `Bin`，代码评审看不到行级差异。
- [ ] 可选：`docs/` 中记录的自更新验证版本（`v2026.3.4.4`）已过期，如需可更新为 `v2026.3.4.11`。
