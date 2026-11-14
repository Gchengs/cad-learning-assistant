# 使用说明

## 0. 环境

| 项 | 要求 |
|---|---|
| AutoCAD | 2016 简体中文 **x64**（32 位加载不了这个 DLL） |
| .NET Framework | 4.5（Windows 10/11 自带更高版本也能跑） |
| 编译 | Visual Studio 2017+，或直接跑 `build-vs.bat` |

## 1. 编译

第一次编译前先生成 26 道题的**结果图**（仓库不放 PNG，约 10 秒，需要 Python 3）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\gen-images.ps1
```

输出应是 `完成：22 张图在 …\content\images`。不想装 Python 也可以跳过——插件照样能跑，只是面板图区显示「本题没有结果示意图」。

然后双击 `build-vs.bat`，等窗口停住。认这两行：

```
[OK] Build succeeded.
     Output: ...\src\CadLearningAssistant\bin\Debug\
```

失败时窗口里会有 `[FAILED]` 和 `error CS…` 的行，按提示处理：

| 提示 | 原因 | 处理 |
|---|---|---|
| 找不到 MSBuild.exe | 没装 VS，或装在非标准位置 | 装 VS 并勾「.NET 桌面开发」，或改 `build-vs.bat` 里的搜索路径 |
| `acmgd.dll` 相关错误 | AutoCAD 不在默认目录 | `set AUTOCAD2016_DIR=你的安装目录` 后重跑 |
| 缺 .NET Framework 4.5 参考程序集 | 没装目标包 | `powershell -ExecutionPolicy Bypass -File tools\prepare-refs.ps1` |
| `MSB3021` / 文件被占用 | AutoCAD 还开着，DLL 锁住了 | 关掉所有 CAD 进程再编译 |

## 2. 检查产物

`src\CadLearningAssistant\bin\Debug\` 下必须同时有：

| 文件 | 说明 |
|---|---|
| `CadLearningAssistant.dll` | 插件本体，约 120–150 KB |
| `questions.json` | 题库，约 490 KB；缺了面板会显示「题库加载失败」 |
| `images\` | 每题一张结果图；缺了只在面板上显示「本题没有结果示意图」 |

## 3. 加载

1. 打开 AutoCAD 2016，`Ctrl+N` 新建空白图（`acadiso.dwt`）
2. 命令行 `NETLOAD` 回车，选 `CadLearningAssistant.dll`
3. 命令行应打印：

```
CAD学习助手已加载（…，共 26 题）。输入 CADLEARN 打开教学面板。
```

没有这行 = DLL 没加载成功，或加载的是旧 DLL（退出所有 CAD 重来）。

> `SECURELOAD` 为 1 时，需要先把 DLL 所在目录加入「选项 → 文件 → 受信任的位置」。

## 4. 面板

命令行 `CADLEARN`，右侧出现可停靠面板：

1. **选题目**：下拉里 26 道题，带「· 可自动验收」后缀的才支持代输命令 + 自动检查
2. **当前步骤**：`3. 图形界限` + `第一单元 · 第 3/15 步`
3. **结果图**：这道题做完应该是什么样；**点一下出大图，`Esc` 或再点一下关**
4. **这步怎么做**：三行
   - `① 点` 该点哪个菜单/面板按钮
   - `② 敲` 该在命令行输入什么，回车写作 `↵`
   - `③ 记` 最容易错的那一句（全文点【我不会】时进命令行）
5. **两个按钮**
   - 【我不会，帮我继续】：把这一步的做法（含命令原文和要点全文）写进命令行，`Ctrl+9` 看；能定位到的菜单会画箭头
   - 【我做好了，下一步】：翻页。**它不代表验收通过**
   - 最后一步会变成灰色的【已到最后一步】

## 5. 两类题的区别

| | 22 道题库题（1-A … 8-B） | 3.1 / 3.18 / 4.1 / 6.1 |
|---|---|---|
| 面板 | 图 + 三行指令 | 同上 |
| 按钮行为 | 只解释、只翻页 | 3.1 可代输命令并自动检查 |
| 验收 | 无（没有起始图，也不该替你画） | 3.1 走「准备 → 执行 → 检查 → 下一步」 |
| 图 | 按题面尺寸画的示意图 | 有配套 DWG 时为原图渲染 |

## 6. 常见问题

| 现象 | 处理 |
|---|---|
| 下拉只有几道题 | 加载的是旧 DLL，退出所有 CAD 重来 |
| 面板写「题库加载失败」 | 确认 `questions.json` 和 DLL 在同一目录；或跑 `python tools\validate_question_bank.py <json 路径>` |
| 图是空的 / 显示「本题没有结果示意图」 | `images\<题号>.png` 缺失（仓库不带图） | 跑 `tools\gen-images.ps1`，再重新编译 |
| 点【我不会】没箭头 | 经典菜单栏没开：命令行 `MENUBAR` → `1` |
| 按钮点不动 | CAD 命令正在执行，按 `Esc` 两次再试 |
| 提示「不是本题的练习副本」 | 3.1 先点【我不会，帮我继续】生成练习副本 |

## 7. 反馈问题时请带上

1. 题号 + 第几步
2. 点的哪个按钮
3. CAD 命令行最后 5–10 行（`Ctrl+9` 打开命令行）
4. 面板截图
