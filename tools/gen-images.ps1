# 生成 22 道题的最终结果示意图（PNG）。
#
# 为什么图不入库：这些 PNG 全部由 tools/image_specs.json 确定性生成，
# 二进制放仓库里既占体积，又会在每次改图时产生无法 review 的 diff。
# 克隆后跑一次本脚本即可，约 10 秒。
#
# 用法（项目根目录）：
#   powershell -ExecutionPolicy Bypass -File tools\gen-images.ps1
#   可选 -OutDir 指定输出目录（默认 src\CadLearningAssistant\content\images）
param(
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) {
    $OutDir = Join-Path $root 'src\CadLearningAssistant\content\images'
}
if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

# 1) 题面 → 矢量描述（尺寸、坐标、文字）
$specPath = Join-Path $root 'tools\image_specs.json'
python (Join-Path $root 'tools\build_image_specs.py') $specPath
if ($LASTEXITCODE -ne 0) {
    Write-Output '生成 image_specs.json 失败：请确认已安装 Python 3 并在 PATH 中。'
    exit 1
}

# 2) 矢量描述 → PNG
& (Join-Path $PSScriptRoot 'render_specs.ps1') -SpecPath $specPath -OutDir $OutDir

$count = (Get-ChildItem $OutDir -Filter *.png -ErrorAction SilentlyContinue).Count
Write-Output ("完成：$count 张图在 " + $OutDir)
if ($count -lt 22) {
    Write-Output '少于 22 张，说明有题目没生成成功；检查上面的输出。'
}
