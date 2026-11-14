#Requires -Version 7.0
<#
.SYNOPSIS
    生成 fork 自更新清单 productinfo.json（格式与 Devolutions 官方一致）。

.DESCRIPTION
    应用会从
      https://github.com/<owner>/<repo>/releases/latest/download/productinfo.json
    拉取清单，因此每次发布都必须把本文件作为 Release 资产上传。
    键名固定为 Devolutions.UniGetUI，Windows 资产类型为 exe（安装包）与 zip（便携版）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $InstallerPath,
    [Parameter(Mandatory)] [string] $ZipPath,
    [Parameter(Mandatory)] [string] $Owner,
    [Parameter(Mandatory)] [string] $Repository,
    [string] $OutputPath = 'productinfo.json'
)

$ErrorActionPreference = 'Stop'

$tag = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
$base = "https://github.com/$Owner/$Repository/releases/download/$tag"

function Get-Sha256([string] $path) {
    (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$installerName = Split-Path $InstallerPath -Leaf
$zipName = Split-Path $ZipPath -Leaf

$payload = [ordered]@{
    'Devolutions.UniGetUI' = [ordered]@{
        Current = [ordered]@{
            Version = $Version.TrimStart('v')
            Files   = @(
                [ordered]@{
                    Arch = 'x64'
                    Type = 'exe'
                    Url  = "$base/$installerName"
                    Hash = Get-Sha256 $InstallerPath
                },
                [ordered]@{
                    Arch = 'x64'
                    Type = 'zip'
                    Url  = "$base/$zipName"
                    Hash = Get-Sha256 $ZipPath
                }
            )
        }
    }
}

$json = $payload | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path (Get-Location) $OutputPath), $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已生成 $OutputPath（版本 $Version）"
Write-Output (Join-Path (Get-Location) $OutputPath)
