#Requires -Version 7.0
<#
.SYNOPSIS
    基于上游 UniGetUI.iss 生成并编译 fork 安装包（去掉 Devolutions 签名步骤）。

.DESCRIPTION
    不修改上游 .iss，而是生成一个同目录的 UniGetUI.fork.iss 副本：
      * 版本号写入 MyAppVersion / VersionInfo 字段；
      * 注释掉 SignTool=azsign 与 SignedUninstaller（fork 没有 Devolutions 证书）；
      * 输出文件名固定为 UniGetUI.Installer.x64.exe（与自更新清单匹配）。
    fork 文件在 .gitignore 中忽略，不会与上游产生冲突。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [string] $BinDir = 'unigetui_bin',
    [string] $OutputDir = 'artifacts'
)

$ErrorActionPreference = 'Stop'

$source = Join-Path $RepoRoot 'UniGetUI.iss'
if (-not (Test-Path $source)) {
    throw "未找到安装脚本：$source"
}

$clean = (($Version -split '\+')[0] -split '-')[0]
$parts = $clean -split '\.'
while ($parts.Count -lt 4) { $parts += '0' }
$fourPart = ($parts[0..3] -join '.')

$text = Get-Content $source -Raw
$text = [regex]::Replace($text, '(?m)^#define MyAppVersion "[^"]*"', "#define MyAppVersion `"$clean`"")
$text = [regex]::Replace($text, '(?m)^VersionInfoVersion=.*$', "VersionInfoVersion=$fourPart")
$text = [regex]::Replace($text, '(?m)^VersionInfoProductVersion=.*$', "VersionInfoProductVersion=$fourPart")
$text = [regex]::Replace($text, '(?m)^SignTool=.*$', '; [CustomChannels] fork 构建无 Devolutions 签名证书，已禁用签名')
$text = [regex]::Replace($text, '(?m)^SignedUninstaller=.*$', 'SignedUninstaller=no')
$text = [regex]::Replace($text, '(?m)^OutputBaseFilename=.*$', 'OutputBaseFilename=UniGetUI.Installer.x64')
$text = [regex]::Replace($text, '(?m)^OutputDir=.*$', "OutputDir=$OutputDir")
$text = [regex]::Replace(
    $text,
    '(?m)^\s*SignedUninstallerDir=.*$',
    '; [CustomChannels] 未签名卸载器，无需 SignedUninstallerDir')

$forkIss = Join-Path $RepoRoot 'UniGetUI.fork.iss'
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[IO.File]::WriteAllText($forkIss, $text, $utf8Bom)
Write-Host "已生成 $forkIss（版本 $clean）"

$bin = Join-Path $RepoRoot $BinDir
if (-not (Test-Path $bin)) {
    throw "未找到发布输出目录：$bin（请先执行 dotnet publish -o $BinDir）"
}

$candidates = @(
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw '未找到 Inno Setup 编译器 ISCC.exe，请安装 Inno Setup 6。'
}

Push-Location $RepoRoot
try {
    & $iscc $forkIss
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC 编译失败，退出码 $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$installer = Join-Path $RepoRoot (Join-Path $OutputDir 'UniGetUI.Installer.x64.exe')
if (-not (Test-Path $installer)) {
    throw "安装包未生成：$installer"
}

Write-Host "安装包已生成：$installer"
Write-Output $installer
