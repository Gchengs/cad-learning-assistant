# 用 accoreconsole 把题库里带 SourceFile 的题，从配套 DWG 直接渲成 PNG。
# 说明：仓库里不带 DWG 素材（教材原图有版权），这个脚本只在你自己有素材时才有用；
# 没有素材时面板仍然正常，只是这几道题显示「本题没有结果示意图」。
#
# 用法（在项目根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\render_dwg.ps1 -Accore "<accoreconsole.exe 路径>" -AssetDir "<DWG 目录>"
param(
    [string]$Accore = "$env:ProgramFiles\Autodesk\AutoCAD 2016\accoreconsole.exe",
    [string]$AssetDir = '',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Accore)) {
    Write-Output ("找不到 accoreconsole： " + $Accore + "；请用 -Accore 指定")
    exit 1
}

$jsonPath = Join-Path $ProjectRoot 'src\CadLearningAssistant\content\questions.json'
$outDir = Join-Path $ProjectRoot 'src\CadLearningAssistant\content\images'
$assetDir = if ($AssetDir) { $AssetDir } else { Join-Path $ProjectRoot 'assets' }
$workDir = Join-Path $env:TEMP 'cad_thumb'

foreach ($dir in @($outDir, $workDir)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

$raw = [System.IO.File]::ReadAllText($jsonPath, [System.Text.Encoding]::UTF8)
$bank = $raw | ConvertFrom-Json
$done = 0

foreach ($q in $bank.Questions) {
    if (-not $q.SourceFile) { continue }
    $src = Join-Path $assetDir ($q.SourceFile -replace '/', '\')
    if (-not (Test-Path $src)) {
        Write-Output ("跳过 " + $q.Id + "：找不到 " + $src)
        continue
    }
    # 先写临时目录再拷回来：accoreconsole 的 PNGOUT 写不进含中文的输出路径。
    $png = Join-Path $workDir ($q.Id + '.png')
    $final = Join-Path $outDir ($q.Id + '.png')
    $scr = Join-Path $workDir ($q.Id + '.scr')
    # FILEDIA=0 让 PNGOUT 不弹保存框：先问文件名，再空一行表示“全选”。
    $lines = @('FILEDIA', '0', 'CMDDIA', '0', '_.ZOOM', '_E', '_.PNGOUT', $png, '')
    [System.IO.File]::WriteAllText($scr, ($lines -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)

    $log = Join-Path $workDir ($q.Id + '.log')
    # 用调用运算符而不是 Start-Process：带空格的路径 + 重定向在 5.1 上容易报 ArgumentException。
    & $Accore /i $src /s $scr 2>&1 | Out-File -FilePath $log -Encoding utf8
    if (Test-Path $png) {
        Copy-Item -Path $png -Destination $final -Force
        $done++
        Write-Output ("wrote " + $final)
    }
    else {
        Write-Output ("失败 " + $q.Id + "，日志： " + $log)
    }
}

Write-Output ("完成 " + $done + " 张（原始 DWG 渲染）")
