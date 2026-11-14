# 准备 .NET Framework 4.5 参考程序集
#
# VS 装了「.NET Framework 4.5 目标包」时不需要这一步。
# 没有目标包时，csproj 会去找 build-support\net45-refs\，这个脚本负责把它填好：
# 从 nuget.org 下载 Microsoft.NETFramework.ReferenceAssemblies.net45 并解压到该目录。
#
# 用法： powershell -ExecutionPolicy Bypass -File tools\prepare-refs.ps1
param(
    [string]$Version = '1.0.3',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $ProjectRoot 'build-support\net45-refs'
$nupkg = Join-Path $env:TEMP ("net45refs-" + $Version + ".nupkg")
$url = "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net45/$Version/microsoft.netframework.referenceassemblies.net45.$Version.nupkg"

Write-Output ("下载 " + $url)
Invoke-WebRequest -Uri $url -OutFile $nupkg -UseBasicParsing

Write-Output ("解压到 " + $target)
$expand = Join-Path $env:TEMP 'net45refs-expand'
if (Test-Path $expand) { Remove-Item $expand -Recurse -Force }
Expand-Archive -Path $nupkg -DestinationPath $expand -Force

# 包内结构：build\.NETFramework\v4.5\*.dll
$source = Join-Path $expand 'build\.NETFramework\v4.5'
if (-not (Test-Path $source)) {
    Write-Output "包结构不符合预期，请手工检查 $expand"
    exit 1
}

if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
New-Item -ItemType Directory -Path (Join-Path $target '.NETFramework\v4.5') -Force | Out-Null
Copy-Item (Join-Path $source '*.dll') (Join-Path $target '.NETFramework\v4.5') -Force

$count = (Get-ChildItem (Join-Path $target '.NETFramework\v4.5') -Filter *.dll).Count
Write-Output ("就绪： " + $count + " 个参考程序集在 " + $target)
