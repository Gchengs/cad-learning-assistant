# CAD 学习助手（AutoCAD 2016 插件）

给 AutoCAD 零基础用户用的「一步一步照着做」面板。它不替你画图，只回答三个问题：**这一步点哪里、敲什么、做完该长什么样**。

- 运行环境：AutoCAD 2016 简体中文 **x64**、.NET Framework 4.5、C# 5
- 形态：`NETLOAD` 加载的 DLL + 一个可停靠面板（`CADLEARN` 打开），不修改 CAD 安装目录

> 26 道题各配一张最终结果图，但**图不入库**（二进制、且完全由 `tools/image_specs.json` 确定性生成）。
> 克隆后跑一次 `tools\gen-images.ps1` 就有了，详见[配图是怎么来的](#配图是怎么来的)。

## 面板长什么样

```
[ 1-A  建立 A3 机械样板                          ▼ ]   ← 选题
3. 图形界限                                            ← 当前步骤
第一单元 · 第 3/15 步                                  ← 进度
最终结果示意（按题面尺寸画）· 点图放大                 ← 做完应该是什么样
┌──────────────────────────────┐
│            （结果图）           │
└──────────────────────────────┘
这步怎么做
① 点　菜单 → 格式 → 图形界限
② 敲　LIMITS↵ 0,0↵ 420,297↵ ZOOM↵ A↵
③ 记　界限只是栅格与 ZOOM A 的范围

[        我不会，帮我继续        ]
[        我做好了，下一步        ]
```

- **图**：每题一张最终结果图，点开可放大；没有对应图片时只显示「本题没有结果示意图」，不影响其它功能
- **三行指令**：回车写作 `↵`，`③ 记` 只留最容易错的那一句
- **【我不会，帮我继续】**：把这一句的完整做法（含命令原文、面板路径、要点全文）写进 CAD 命令行，`Ctrl+9` 看
- **【我做好了，下一步】**：只翻页，不宣称验收通过

## 目录结构

```
src/CadLearningAssistant/        插件工程（VS 打开 .sln 即可）
  Plugin.cs                      命令入口、面板宿主、命令行输出
  UI/LearningPalette.cs          面板（图 + 三行指令 + 两个按钮）
  Services/AutoCadChecker.cs     可自动判定项的检查器（图层、单位、实体…）
  Services/RibbonProbe.cs        菜单/功能区定位与屏幕箭头
  Services/ClickPointFinder.cs   封闭区域内找最远点（填充点提示）
  Infrastructure/QuestionBank.cs 题库加载与校验
  content/questions.json         题库（26 题 / 353 步）
  content/images/<题号>.png      每题一张结果图（跑 tools\gen-images.ps1 生成）
tools/                           题库与配图的生成、校验脚本
build-vs.bat                     一键编译（自动找 MSBuild）
```

## 编译

第一次编译前先生成结果图（约 10 秒，需要 Python 3；跳过后插件仍能跑，只是面板显示「本题没有结果示意图」）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\gen-images.ps1
```

然后编译：

```bat
build-vs.bat
```

需要：Visual Studio 2017+（含「.NET 桌面开发」）、AutoCAD 2016。AutoCAD 不在默认目录时先设：

```bat
set AUTOCAD2016_DIR=D:\Apps\AutoCAD 2016
```

若提示缺少 .NET Framework 4.5 参考程序集：

```powershell
powershell -ExecutionPolicy Bypass -File tools\prepare-refs.ps1
```

也可以直接用 VS 打开 `src\CadLearningAssistant\CadLearningAssistant.sln`，选 **Debug | x64** 生成。

产物：`src\CadLearningAssistant\bin\Debug\CadLearningAssistant.dll`（同目录还有 `questions.json` 和 `images\`，**三个都要在**）。

## 使用

1. 打开 AutoCAD 2016，`Ctrl+N` 新建空白图
2. 命令行 `NETLOAD` → 选上面的 DLL，应打印 `CAD学习助手已加载（…，共 26 题）`
3. 命令行 `CADLEARN` 打开面板
4. 下拉选题 → 照三行指令做 → 做完点【我做好了，下一步】

`SECURELOAD` 为 1 时 NETLOAD 需要先把 DLL 所在目录加入受信任位置。

## 题库格式

`content/questions.json` 是 `DataContractJsonSerializer` 能直接吃的结构，加载器对字段很严（任一处不合规会整库拒载），改动后先跑校验：

```powershell
python tools\validate_question_bank.py src\CadLearningAssistant\content\questions.json
```

主要字段：

| 字段 | 说明 |
|---|---|
| `Id` / `Title` / `Unit` | 题号、标题、单元 |
| `WizardReady` | `true` 才启用「代输命令 + 自动验收」链路（目前只有 3.1 为真） |
| `Steps[].PanelPath` | 面板/菜单路径，面板第一行「① 点」用它 |
| `Steps[].Command` | 命令行输入原文，「回车」可写成 `回车` 或直接换行 |
| `Steps[].Tip` | 要点，面板只取第一句，全文进命令行 |
| `Steps[].CheckTypes` | 可自动判定的项（`layer` / `units` / `circle-count` / `manual` …） |
| `Steps[].CommandScript` | 仅 `WizardReady` 题使用，一行一条命令 |

## 配图是怎么来的

- **22 张按题面尺寸画**：`tools/build_image_specs.py`（几何 → `image_specs.json`）+ `tools/render_specs.ps1`（GDI+ 出 PNG）
- **配套 DWG 直接渲染**：`tools/render_dwg.ps1`（`accoreconsole` + `ZOOM E` + `PNGOUT`），需要自备 DWG，仓库不附带

```powershell
# 一条命令跑完前两步
powershell -ExecutionPolicy Bypass -File tools\gen-images.ps1
```

这两步都是确定性的：同样的 `questions.json` 跑出来逐字节相同，所以仓库里不放 PNG 也不会丢东西。

这些图是**示意图**：按题面给的尺寸和形状摆出来，用来看「方向和比例对不对」，不能拿它逐线对答案。面板角标会区分「按题面尺寸画」和「配套 DWG 渲染」。

## 已验证 / 未验证

- 已验证：题库能被加载（26 题 / 353 步逐一切换，无异常）；`CADLEARNQ` / `CADLEARNSTEP` 的越界输入有明确报错；无界面环境（`accoreconsole`）下加载、高亮、箭头创建与清理可用
- 未验证：面板在真 CAD 里的观感（字号、行高、图区比例、放大窗口）；22 道题的图形区域与最终结果没有标准答案可比对

面板在 `accoreconsole` 里跑不起来（`PaletteSet` 需要停靠宿主窗口，会以栈溢出退出），这是环境限制，不是插件缺陷。

## 声明

- 题目内容整理自公开的 CAD 习题册，仅用于学习交流；教材原图、配套 DWG 不在本仓库内
- 代码按 MIT 协议发布，见 `LICENSE`
