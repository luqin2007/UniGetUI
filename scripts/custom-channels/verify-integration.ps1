#Requires -Version 7.0
<#
.SYNOPSIS
    合并上游更新后校验 [CustomChannels] 集成锚点是否仍然存在。

.DESCRIPTION
    当上游重构导致接线点消失时，编译期可能仍能通过（例如枚举成员被删除但代码不再引用），
    也可能以难以定位的方式失败。该脚本逐项检查锚点，缺失即返回非零退出码，
    供 CI 与本地合并后自检使用。

.EXAMPLE
    pwsh ./scripts/custom-channels/verify-integration.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()

function Check-Contains {
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $Pattern,
        [Parameter(Mandatory)] [string] $Description,
        [switch] $Regex
    )

    $path = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path $path)) {
        $failures.Add("缺少文件：$RelativePath（$Description）")
        return
    }

    $content = Get-Content $path -Raw
    $found = if ($Regex) { $content -match $Pattern } else { $content.Contains($Pattern) }
    if (-not $found) {
        $failures.Add("锚点缺失：$RelativePath 中未找到 $Description（匹配：$Pattern）")
    }
}

# --- 页面枚举与扩展注册 -------------------------------------------------------
Check-Contains 'src/UniGetUI.Avalonia/Views/MainWindow.axaml.cs' 'CustomChannels' 'PageType.CustomChannels'
Check-Contains 'src/UniGetUI.Avalonia/Infrastructure/AppExtensions.cs' 'CustomChannelsExtension' '扩展定义'
Check-Contains 'src/UniGetUI.Avalonia/Infrastructure/AppExtensions.cs' 'CustomChannelsSettingsPage' '扩展设置页'

# --- 侧边栏 -------------------------------------------------------------------
Check-Contains 'src/UniGetUI.Avalonia/Views/SidebarView.axaml' 'CustomChannelsNavBtn' '侧边栏导航项'
Check-Contains 'src/UniGetUI.Avalonia/Views/SidebarView.axaml.cs' 'CustomChannelsNavBtn' '导航项映射'
Check-Contains 'src/UniGetUI.Avalonia/ViewModels/SidebarViewModel.cs' 'PageType.CustomChannels' '扩展开关分支'

# --- 设置键与图标 -------------------------------------------------------------
Check-Contains 'src/UniGetUI.Core.Settings/SettingsEngine_Names.cs' 'EnableCustomChannels' '设置键'
Check-Contains 'src/UniGetUI.Interface.Enums/Enums.cs' 'CustomChannels' 'IconType'
Check-Contains 'src/UniGetUI.Avalonia/Views/Controls/Settings/SettingsPageButton.cs' 'IconType.CustomChannels' '图标映射'

# --- 工程引用 ---------------------------------------------------------------
Check-Contains 'src/UniGetUI.Avalonia/UniGetUI.Avalonia.csproj' 'UniGetUI.Extensions.CustomChannels.csproj' 'ProjectReference'
Check-Contains 'src/UniGetUI.Avalonia.slnx' 'UniGetUI.Extensions.CustomChannels' '解决方案登记'

# --- 自更新 fork 适配 ---------------------------------------------------------
Check-Contains 'src/UniGetUI.Avalonia/Infrastructure/AvaloniaAutoUpdater.cs' 'FORK_ALLOW_UNSIGNED_INSTALLERS' 'fork 签名策略'
Check-Contains 'src/UniGetUI.Avalonia/Infrastructure/AvaloniaAutoUpdater.cs' 'luqin2007/UniGetUI' 'fork 清单地址'

# --- 操作列表 API -------------------------------------------------------------
Check-Contains 'src/UniGetUI.Avalonia/Infrastructure/AvaloniaOperationRegistry.cs' 'OperationViewModels' '操作注册表'
Check-Contains 'src/UniGetUI.PackageEngine.Operations/AbstractOperation.cs' 'protected abstract Task<OperationVeredict> PerformOperation' 'AbstractOperation 契约'

if ($failures.Count -gt 0) {
    Write-Host "集成锚点校验失败（$($failures.Count) 项）：" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host '请参考 INTEGRATION.md 恢复上述接线后再提交。' -ForegroundColor Yellow
    exit 1
}

Write-Host '集成锚点校验通过。' -ForegroundColor Green
exit 0
