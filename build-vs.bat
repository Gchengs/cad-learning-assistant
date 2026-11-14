@echo off
chcp 65001 >nul
setlocal
rem ---------------------------------------------------------------------------
rem 构建 CAD 学习助手（AutoCAD 2016 / .NET Framework 4.5 / x64）
rem
rem 需要：
rem   1. Visual Studio 2017 或更新版本（装「.NET 桌面开发」就有 MSBuild）
rem   2. AutoCAD 2016（只为取三个托管程序集）
rem   3. .NET Framework 4.5 参考程序集（VS 自带目标包，或先跑 tools\prepare-refs.ps1）
rem
rem AutoCAD 装到非默认目录时，先设环境变量再运行：
rem   set AUTOCAD2016_DIR=D:\Apps\AutoCAD 2016
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0"
set "PROJ=%ROOT%src\CadLearningAssistant\CadLearningAssistant.csproj"

rem --- 1. 找 MSBuild：优先 vswhere（VS 2017+ 标准位置） ---
set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
)
if not defined MSBUILD (
    for %%p in (
        "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        "%ProgramFiles(x86)%\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe"
        "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
    ) do if not defined MSBUILD if exist %%p set "MSBUILD=%%~p"
)
if not defined MSBUILD (
    echo [FAILED] 找不到 MSBuild.exe
    echo          装 Visual Studio 时勾选「.NET 桌面开发」，或手工改本文件里的搜索路径。
    if not defined CI pause
    exit /b 1
)

echo [INFO] MSBuild: %MSBUILD%
echo [INFO] 工程   : %PROJ%

rem --- 2. 确认 AutoCAD 托管程序集在 ---
if not defined AUTOCAD2016_DIR set "AUTOCAD2016_DIR=%ProgramFiles%\Autodesk\AutoCAD 2016"
if not exist "%AUTOCAD2016_DIR%\acmgd.dll" (
    echo [WARN] 在 "%AUTOCAD2016_DIR%" 没找到 acmgd.dll
    echo        如果 AutoCAD 装在别处，先执行： set AUTOCAD2016_DIR=你的安装目录
)

rem --- 3. 编译 ---
"%MSBUILD%" "%PROJ%" /t:Build /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo > "%ROOT%build.log" 2>&1
set "CODE=%ERRORLEVEL%"
type "%ROOT%build.log"

if not "%CODE%"=="0" (
    echo.
    echo [FAILED] exit code %CODE% —— 上面 error 开头的行就是原因
    if not defined CI pause
    exit /b %CODE%
)

echo.
echo [OK] Build succeeded.
echo      Output: %ROOT%src\CadLearningAssistant\bin\Debug\
echo      用 AutoCAD 的 NETLOAD 加载其中的 CadLearningAssistant.dll
if not defined CI pause
endlocal
