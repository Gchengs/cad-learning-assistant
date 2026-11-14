using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;
using CadLearningAssistant.Infrastructure;
using CadLearningAssistant.Services;
using CadLearningAssistant.UI;

[assembly: CommandClass(typeof(CadLearningAssistant.Plugin))]

namespace CadLearningAssistant
{
    public class Plugin : IExtensionApplication
    {
        private static PaletteSet _palette;
        private static LearningPalette _paletteControl;
        private static QuestionBank _questionBank;
        private static AutoCadChecker _checker;
        private static bool _eventsAttached;
        private static bool _idleAttached;
        private static readonly Dictionary<Document, string> _practiceDocuments = new Dictionary<Document, string>();
        private static bool _refreshingWizard;
        private static long _lastWizardRefresh;
        private static long _lastBusyRefresh;
        private static bool _pendingVisualCleanup;
        private static readonly HashSet<Document> _commandDocuments = new HashSet<Document>();

        internal static bool IsLearningPaletteVisible
        {
            get { return _palette != null && _palette.Visible; }
        }

        private static void AttachCommandEvents(Document document)
        {
            if (document == null || !_commandDocuments.Add(document))
            {
                return;
            }
            document.CommandWillStart += PracticeCommandStarted;
            document.CommandEnded += PracticeCommandFinished;
            document.CommandCancelled += PracticeCommandInterrupted;
            document.CommandFailed += PracticeCommandInterrupted;
        }

        private static void DetachCommandEvents(Document document)
        {
            if (document == null || !_commandDocuments.Remove(document))
            {
                return;
            }
            document.CommandWillStart -= PracticeCommandStarted;
            document.CommandEnded -= PracticeCommandFinished;
            document.CommandCancelled -= PracticeCommandInterrupted;
            document.CommandFailed -= PracticeCommandInterrupted;
        }

        private static bool IsIgnoredCommandEvent(string commandName)
        {
            string name = (commandName ?? string.Empty).Trim().TrimStart('_', '.');
            return name.StartsWith("CADLEARN", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("NETLOAD", StringComparison.OrdinalIgnoreCase);
        }

        private static void PracticeCommandStarted(object sender, CommandEventArgs e)
        {
            Document document = sender as Document;
            string commandName = e.GlobalCommandName;
            if (IsIgnoredCommandEvent(commandName))
            {
                return;
            }
            if (_paletteControl != null && !_paletteControl.IsDisposed)
            {
                _paletteControl.NotifyCommandStarted(document, commandName);
            }
        }

        private static void PracticeCommandFinished(object sender, CommandEventArgs e)
        {
            // 事件中只标记待检查；真正的数据库读取仍在空闲且命令结束之后执行。
            Document document = sender as Document;
            string commandName = e.GlobalCommandName;
            if (IsIgnoredCommandEvent(commandName))
            {
                return;
            }
            if (_paletteControl != null && !_paletteControl.IsDisposed)
            {
                _paletteControl.QueueWizardCheck(document, commandName);
            }
        }

        private static void PracticeCommandInterrupted(object sender, CommandEventArgs e)
        {
            Document document = sender as Document;
            string commandName = e.GlobalCommandName;
            if (IsIgnoredCommandEvent(commandName))
            {
                return;
            }
            if (_paletteControl != null && !_paletteControl.IsDisposed)
            {
                _paletteControl.NotifyCommandInterrupted(document, commandName, "当前命令被取消或执行失败");
            }
        }

        // 指不到时返回 false，面板据此自动改用命令方式，避免“点了按钮什么也没发生”。
        internal static bool TryShowMenuPointer(LearningStep step, out string message)
        {
            return RibbonProbe.TryShowStepPointer(step, out message);
        }

        internal static void ClearMenuPointer()
        {
            RibbonProbe.ClearPointer();
        }

        private static void DocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            AttachCommandEvents(e.Document);
        }

        public void Initialize()
        {
            _questionBank = QuestionBank.LoadFromPluginDirectory();
            _checker = new AutoCadChecker();
            Application.DocumentManager.DocumentToBeDeactivated += DocumentToBeDeactivated;
            Application.DocumentManager.DocumentToBeDestroyed += DocumentToBeDestroyed;
            Application.DocumentManager.DocumentBecameCurrent += DocumentBecameCurrent;
            Application.DocumentManager.DocumentCreated += DocumentCreated;
            foreach (Document document in Application.DocumentManager)
            {
                AttachCommandEvents(document);
            }
            Application.Idle += ApplicationIdle;
            _eventsAttached = true;
            _idleAttached = true;
            // 第十版起界面不再有“指给我看”，加载后不再自动写 ribbon-dump.txt；
            // 需要时仍可手工输入 CADLEARNRIBBON 导出一次，不改图、不改设置。
            _probePending = false;
            _probeStartTick = 0;
            WriteMessage("\nCAD学习助手已加载（第十二版·图表面板，共 " + _questionBank.Questions.Count
                + " 题）。输入 CADLEARN 打开教学面板。\n");
            if (!string.IsNullOrEmpty(_questionBank.LoadError))
            {
                WriteMessage("\n[CAD学习助手] " + _questionBank.LoadError + "\n");
            }
        }

        public void Terminate()
        {
            _practiceDocuments.Clear();
            _refreshingWizard = false;
            _lastWizardRefresh = 0;
            _pendingVisualCleanup = false;
            if (_eventsAttached)
            {
                Application.DocumentManager.DocumentToBeDeactivated -= DocumentToBeDeactivated;
                Application.DocumentManager.DocumentToBeDestroyed -= DocumentToBeDestroyed;
                Application.DocumentManager.DocumentBecameCurrent -= DocumentBecameCurrent;
                Application.DocumentManager.DocumentCreated -= DocumentCreated;
                foreach (Document document in new List<Document>(_commandDocuments))
                {
                    DetachCommandEvents(document);
                }
                _eventsAttached = false;
            }
            if (_idleAttached)
            {
                Application.Idle -= ApplicationIdle;
                _idleAttached = false;
            }
            try
            {
                if (_checker != null)
                {
                    _checker.ClearHighlights();
                }
                if (_paletteControl != null && !_paletteControl.IsDisposed)
                {
                    _paletteControl.SetDocument(null);
                }
            }
            catch (System.Exception exception)
            {
                WriteMessage("\n[CAD学习助手] 退出时未能清理显示高亮：" + exception.Message + "\n");
            }
            finally
            {
                if (_palette != null)
                {
                    _palette.Visible = false;
                    _palette.Dispose();
                    _palette = null;
                    _paletteControl = null;
                }
            }
        }

        [CommandMethod("CADLEARN", CommandFlags.Session)]
        public void ShowLearningPalette()
        {
            RunSafely("打开教学面板", delegate
            {
                EnsureChecker();
                // _questionBank 在 Initialize 里才会赋值；NETLOAD 异常或加载顺序不对时
                // 这里可能是 null，直接取 .Questions.Count 会抛 NRE，面板就打不开了。
                if (_palette != null && (_questionBank == null || _questionBank.Questions.Count == 0))
                {
                    _palette.Dispose();
                    _palette = null;
                    _paletteControl = null;
                }
                if (_palette == null)
                {
                    // 题库加载失败后可修复 JSON，再次输入 CADLEARN 重试。
                    _questionBank = QuestionBank.LoadFromPluginDirectory();
                    CreatePalette();
                }
                _palette.Visible = true;
                // 首次创建时子控件句柄可能尚不存在，显示后重新同步活动文档。
                _paletteControl.SetDocument(Application.DocumentManager.MdiActiveDocument);
            });
        }

        [CommandMethod("CADLEARNCHECK", CommandFlags.Modal)]
        public void CheckCurrentQuestion()
        {
            RunSafely("检查当前图纸", delegate
            {
                EnsureChecker();
                Document document = RequireDocument(false);
                if (_questionBank == null || _questionBank.Questions.Count == 0)
                {
                    throw new InvalidOperationException("题库未成功加载，请输入 CADLEARN 查看诊断信息。");
                }
                QuestionDefinition question = _questionBank == null ? null : _questionBank.Find(_checker.ActiveQuestionId);
                CheckResult result = _checker.CheckActiveQuestion(document, question);
                ShowResult(result, document);
            });
        }

        [CommandMethod("CADLEARNSTATUS", CommandFlags.Modal)]
        public void ShowStatus()
        {
            RunSafely("查看助手状态", delegate
            {
                EnsureChecker();
                EnsureQuestionBank();
                Document document = Application.DocumentManager.MdiActiveDocument;
                WriteMessage("\n[CAD学习助手] 第十二版·图表面板；题库题数 " + _questionBank.Questions.Count
                    + "；当前题号 " + _checker.ActiveQuestionId + "\n");
                WriteMessage("  题库来源：" + _questionBank.SourcePath + "\n");
                if (!string.IsNullOrEmpty(_questionBank.LoadError))
                {
                    WriteMessage("  题库错误：" + _questionBank.LoadError + "\n");
                }
                if (document != null)
                {
                    WriteMessage("  当前图纸：" + document.Name + "\n");
                }
            });
        }

        // 只读导出功能区结构：把每个选项卡、面板、按钮的名字和屏幕坐标写到 ribbon-dump.txt。
        // 这是做“指到功能区按钮”的指针前必须先有的事实依据，不改图、不改设置。
        [CommandMethod("CADLEARNRIBBON", CommandFlags.Modal)]
        public void DumpRibbonStructure()
        {
            RunSafely("导出功能区结构", delegate
            {
                string path = RibbonProbe.WriteDump();
                WriteMessage("\n[CAD学习助手] 功能区结构已导出：\n  " + path + "\n");
                WriteMessage("  打开这个文件即可看到每个选项卡/面板/按钮的名字；能算出坐标的项会带 Rect。\n");
            });
        }

        [CommandMethod("CADLEARNQ", CommandFlags.Modal)]
        public void SelectQuestionCommand()
        {
            RunSafely("切换题目", delegate
            {
                EnsureChecker();
                EnsureQuestionBank();
                Document document = RequireDocument(false);
                if (_questionBank.Questions.Count == 0)
                {
                    throw new InvalidOperationException("题库未成功加载，请输入 CADLEARNSTATUS 查看诊断信息。");
                }

                string available = BuildIdList();
                PromptResult prompt = document.Editor.GetString(
                    "\n选择题号（" + available + "），当前 " + _checker.ActiveQuestionId + "：");
                string input = prompt.Status == PromptStatus.OK ? prompt.StringResult : null;
                if (string.IsNullOrEmpty(input) || input.Trim().Length == 0)
                {
                    WriteMessage("\n[CAD学习助手] 未改变题号，当前仍为 " + _checker.ActiveQuestionId + "\n");
                    return;
                }

                QuestionDefinition target = _questionBank.Find(input);
                if (target == null)
                {
                    throw new InvalidOperationException("题库中不存在题号“" + input.Trim() + "”。可用题号：" + available);
                }

                _checker.SetActiveQuestion(target.Id);
                _checker.SetActiveStep(target.Steps != null && target.Steps.Count > 0 ? target.Steps[0] : null);
                if (_paletteControl != null && !_paletteControl.IsDisposed)
                {
                    _paletteControl.SelectById(target.Id);
                }
                WriteMessage("\n[CAD学习助手] 当前题号已切换到 " + target.Id + "\n");
            });
        }

        [CommandMethod("CADLEARNHIGHLIGHT", CommandFlags.Modal)]
        public void HighlightCommand()
        {
            RunSafely("高亮相关对象", delegate
            {
                EnsureChecker();
                Document document = RequireDocument(false);
                CheckResult result;
                using (DocumentLock documentLock = document.LockDocument())
                {
                    result = _checker.HighlightActiveQuestion(document);
                }
                ShowResult(result, document);
            });
        }

        // 以下两个命令只为“脱离界面自检”而存在：面板上的“显示本步箭头”走的是同一套
        // 检查器方法，但它们原本只能由按钮触发，导致无界面环境无法验证步骤与箭头。
        [CommandMethod("CADLEARNSTEP", CommandFlags.Modal)]
        public void SelectStepCommand()
        {
            RunSafely("切换学习步骤", delegate
            {
                EnsureChecker();
                EnsureQuestionBank();
                Document document = RequireDocument(false);
                QuestionDefinition question = _questionBank.Find(_checker.ActiveQuestionId);
                if (question == null || question.Steps == null || question.Steps.Count == 0)
                {
                    throw new InvalidOperationException("当前题号 " + _checker.ActiveQuestionId + " 没有可用步骤，请先用 CADLEARNQ 选择题号。");
                }

                int current = _checker.ActiveStepOrder.HasValue ? _checker.ActiveStepOrder.Value : 0;
                PromptResult prompt = document.Editor.GetString(
                    "\n输入步骤序号（1-" + question.Steps.Count + "），当前第 " + current + " 步：");
                string input = prompt.Status == PromptStatus.OK ? prompt.StringResult : null;
                int order;
                if (string.IsNullOrEmpty(input) || !int.TryParse(input.Trim(), out order))
                {
                    WriteMessage("\n[CAD学习助手] 未改变步骤，当前仍为第 " + current + " 步\n");
                    return;
                }

                LearningStep target = null;
                foreach (LearningStep step in question.Steps)
                {
                    if (step.Order == order)
                    {
                        target = step;
                        break;
                    }
                }
                if (target == null)
                {
                    throw new InvalidOperationException("题库中不存在第 " + order + " 步。可用步骤：1-" + question.Steps.Count + "。");
                }

                _checker.SetActiveQuestion(question.Id);
                _checker.SetActiveStep(target);
                if (_paletteControl != null && !_paletteControl.IsDisposed)
                {
                    _paletteControl.SelectStepByOrder(order);
                }
                ClearSelectionHintsWhenIdle();
                WriteMessage("\n[CAD学习助手] 当前步骤已切换到第 " + target.Order + " 步：" + target.Title
                    + "（HintTarget=" + (string.IsNullOrEmpty(target.HintTarget) ? "<空>" : target.HintTarget) + "）\n");
            });
        }

        [CommandMethod("CADLEARNGUIDE", CommandFlags.Modal)]
        public void ShowGuideCommand()
        {
            RunSafely("显示本步箭头", delegate
            {
                EnsureChecker();
                Document document = RequireDocument(false);
                LearningStep step = _checker.ActiveStep;
                if (step == null)
                {
                    throw new InvalidOperationException("当前没有选中步骤，请先用 CADLEARNSTEP 指定步骤。");
                }
                _checker.ClearHighlights();
                CheckResult result;
                using (DocumentLock documentLock = document.LockDocument())
                {
                    result = _checker.ShowStepGuide(document, step);
                }
                ShowResult(result, document);
            });
        }

        [CommandMethod("CADLEARNCLEAR", CommandFlags.Modal)]
        public void ClearHighlightCommand()
        {
            RunSafely("清除高亮", delegate
            {
                EnsureChecker();
                _checker.ClearHighlights();
                ShowResult(new CheckResult { Summary = "已清除助手记录的高亮" },
                    Application.DocumentManager.MdiActiveDocument);
            });
        }

        internal static void SelectQuestion(string questionId)
        {
            EnsureChecker();
            // 先同步题号，即使清理显示失败也不会继续检查上一题。
            _checker.SetActiveQuestion(questionId);
            QuestionDefinition question = _questionBank == null ? null : _questionBank.Find(questionId);
            _checker.SetActiveStep(question != null && question.Steps != null && question.Steps.Count > 0 ? question.Steps[0] : null);
            ClearSelectionHintsWhenIdle();
        }

        internal static void SelectStep(string questionId, LearningStep step)
        {
            RunSafely("切换学习步骤", delegate
            {
                EnsureChecker();
                _checker.SetActiveQuestion(questionId);
                _checker.SetActiveStep(step);
                // 步骤变化后清除上一阶段留下的高亮/箭头，避免旧提示与当前步骤混在一起。
                ClearSelectionHintsWhenIdle();
            });
        }

        internal static void ShowStepGuideFromPalette(string questionId, LearningStep step)
        {
            RunSafely("显示本步箭头", delegate
            {
                EnsureChecker();
                Document document = RequireDocument();
                _checker.SetActiveQuestion(questionId);
                _checker.SetActiveStep(step);
                _checker.ClearHighlights();
                CheckResult result;
                using (DocumentLock documentLock = document.LockDocument())
                {
                    result = _checker.ShowStepGuide(document, step);
                }
                ShowResult(result, document);
            });
        }

        // open 只打开助手准备的新副本；其他动作只提供教学提示，不执行命令或修改实体。
        internal static CheckResult StartWizardAction(string questionId, LearningStep step)
        {
            EnsureChecker();
            QuestionDefinition question = RequireWizardStep(questionId, step);
            if (!question.WizardReady)
            {
                throw new InvalidOperationException("本题尚未完成题图核实，当前只允许查看教学资料，不能启动傻瓜式向导。");
            }
            Document document;
            if (IsOpenAction(step))
            {
                // 无图状态也允许准备副本，不能在这里调用 RequireDocument。
                document = Application.DocumentManager.MdiActiveDocument;
                if (document != null)
                {
                    RequireWizardIdle(document);
                }
                if (!IsPracticeDocument(document, question.Id))
                {
                    string projectRoot;
                    string source = FindPracticeSource(question.SourceFile, out projectRoot);
                    string directory = Path.GetFullPath(Path.Combine(projectRoot, "practice", question.Id.Replace('.', '-'), Guid.NewGuid().ToString("N")));
                    string copy = Path.Combine(directory, Path.GetFileName(source));
                    RequireProjectPath(projectRoot, copy);
                    Directory.CreateDirectory(directory);
                    RequireProjectPath(projectRoot, copy);
                    File.Copy(source, copy, false);
                    document = Application.DocumentManager.Open(copy, false);
                    if (document == null)
                    {
                        throw new InvalidOperationException("练习副本已创建，但 AutoCAD 未返回打开的文档。");
                    }
                    _practiceDocuments[document] = question.Id;
                    Application.DocumentManager.MdiActiveDocument = document;
                }
            }
            else
            {
                document = RequireDocument();
                RequireWizardIdle(document);
                RequirePracticeDocument(document, question.Id);
            }

            _checker.SetActiveQuestion(question.Id);
            _checker.SetActiveStep(step);
            _checker.ClearHighlights();
            CheckResult result;
            string target = (step.HintTarget ?? string.Empty).Trim();
            bool click = string.Equals((step.Action ?? string.Empty).Trim(), "click", StringComparison.OrdinalIgnoreCase);
            // 推算出的内部点不代表题图目标区域；单按钮模式不自动代点，也不绘制猜测箭头。
            if (click || target.Length == 0 || string.Equals(target, "none", StringComparison.OrdinalIgnoreCase))
            {
                result = new CheckResult
                {
                    Summary = click ? "本步点击区域尚未精确核实，未绘制定位箭头" : "请按当前动作手工操作"
                };
                if (click)
                {
                    result.Details.Add("目标尚未生成时没有可定位的对象；即使已有同类对象，也不能用其中心代替正确点击区域。请对照题图确认封闭区域。");
                }
            }
            else
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    result = string.Equals(target, "overview", StringComparison.OrdinalIgnoreCase)
                        ? _checker.HighlightActiveQuestion(document)
                        : _checker.ShowStepGuide(document, step);
                }
                // 显示定位成功不等于本步验收通过。
                result.Passed = false;
            }
            result.Details.Add("当前动作：" + (step.Action ?? string.Empty));
            if (!string.IsNullOrWhiteSpace(step.Command))
            {
                result.Details.Add("命令：" + step.Command);
            }
            if (!string.IsNullOrWhiteSpace(step.InputText))
            {
                result.Details.Add("需要键入：" + step.InputText);
            }
            if (!string.IsNullOrWhiteSpace(step.Hint))
            {
                result.Details.Add("提示：" + step.Hint);
            }
            result.Summary = WizardText(result.Summary);
            for (int index = 0; index < result.Details.Count; index++)
            {
                result.Details[index] = WizardText(result.Details[index]);
            }
            ShowResult(result, document);
            return result;
        }

        internal static CheckResult HelpWizardStep(string questionId, LearningStep step)
        {
            // 帮助始终只读：不能创建/打开副本、切换文档、改变视图或启动验收。
            QuestionDefinition question = RequireWizardStep(questionId, step);
            if (!question.WizardReady)
            {
                throw new InvalidOperationException("本题待核实，暂不可启动向导帮助。");
            }
            Document document = Application.DocumentManager.MdiActiveDocument;
            bool busy = IsCommandRunning(document);
            // 帮助只解释，不校验绑定：用户直接打开起始图（而不是助手建的副本）时，
            // 这里若抛 RequirePracticeDocument，面板只会显示一条异常，等于把求助变成了报错。
            bool bound = IsOpenAction(step) || IsPracticeDocument(document, question.Id);
            CheckResult help = new CheckResult
            {
                Summary = busy ? "当前命令仍在进行；仅显示帮助，未执行或取消任何命令" : "本步帮助（未启动操作）"
            };
            if (IsOpenAction(step))
            {
                help.Details.Add("点击【我不会，帮我继续】后才会准备并打开练习副本；查看帮助不会打开文件。");
            }
            else if (!bound)
            {
                help.Details.Add("当前图纸不是助手为本题准备的练习副本：帮助可以照看，但本步不会被自动验收。请点【我不会，帮我继续】，让助手准备副本后再操作。");
            }
            if (busy)
            {
                help.Details.Add("先阅读 CAD 当前命令行提示；不想继续就直接按键盘 Esc，再从本步第一条重新输入。");
            }
            // 菜单定位由所选帮助模式显式触发；不从闭合曲线猜测题图要求的内部点击点。
            if (!string.IsNullOrWhiteSpace(step.Command))
            {
                help.Details.Add("照着做：" + step.Command);
            }
            if (!string.IsNullOrWhiteSpace(step.InputText))
            {
                help.Details.Add("需要键入：" + step.InputText);
            }
            // 题库题的 Hint 留空（面板已经显示要点），空项不再拼成“提示：”这种半行。
            if (!string.IsNullOrWhiteSpace(step.Hint))
            {
                help.Details.Add("提示：" + step.Hint);
            }
            return help;
        }

        internal static CheckResult RunStepScript(string questionId, LearningStep step)
        {
            // 把本步的命令序列排队送进命令行；排队发送不等于命令已执行或本步已通过，
            // 也不保证所有输入一定执行。交互命令只负责启动，不代选对象、不代填输入。
            QuestionDefinition question = RequireWizardStep(questionId, step);
            if (!question.WizardReady)
            {
                throw new InvalidOperationException("本题待核实，暂不可一键输入命令。");
            }
            string action = (step.Action ?? string.Empty).Trim().ToLowerInvariant();
            string script = step.CommandScript ?? string.Empty;
            if (action == "manual" || action == "open")
            {
                throw new InvalidOperationException("本步不允许发送绘图命令。");
            }
            // 交互步骤只启动命令，不猜选区、不代选对象、不代填保存路径。
            if (action == "click")
            {
                script = "_.-HATCH\n";
            }
            else if (action == "select")
            {
                script = "_.CHPROP\n";
            }
            else if (action == "save")
            {
                script = "_.SAVEAS\n";
            }
            else
            {
                // 只允许当前课程已经核实的属性脚本，外置题库不能执行任意命令。
                Dictionary<string, string> allowed = new Dictionary<string, string>
                {
                    { "layer-exists", "_.-LAYER\n_New\nHATCH\n\n" },
                    { "layer-red", "_.-LAYER\n_Color\n1\nHATCH\n\n" },
                    { "current-layer", "CLAYER\nHATCH\n" },
                    { "hpname", "HPNAME\nANSI31\n" },
                    { "hpscale", "HPSCALE\n1\n" },
                    { "hpang", "HPANG\n0\n" },
                    { "hplayer", "HPLAYER\n.\n" },
                    { "cecolor", "CECOLOR\n7\n" }
                };
                string expected;
                string key = step.CheckTypes != null && step.CheckTypes.Count == 1 ? step.CheckTypes[0] : string.Empty;
                if (!string.Equals(question.Id, "3.1", StringComparison.Ordinal)
                    || !allowed.TryGetValue(key, out expected)
                    || !string.Equals(script.Replace("\r\n", "\n"), expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("本步没有核实过的安全命令脚本，请选择指给我看或按操作说明完成。");
                }
            }
            Document document = RequireDocument();
            RequireWizardIdle(document);
            if (!IsPracticeDocument(document, question.Id))
            {
                throw new InvalidOperationException("当前图纸还不是本题的练习副本：请先点【我不会，帮我继续】，绑定好练习图后再点同一按钮。");
            }

            // SendStringToExecute 只排队接收一条命令行输入；多行输入不保证按预期处理，
            // 所以先把整组输入压成一行 AutoLISP，再排队送出。
            string payload = BuildOneShotInput(script);
            if (payload.Length == 0)
            {
                throw new InvalidOperationException("本步的命令序列为空，无法一键输入。");
            }
            document.SendStringToExecute(payload + "\n", true, false, false);

            CheckResult result = new CheckResult
            {
                Passed = true,
                Summary = "已将本步 " + CountInputs(script) + " 条输入排队发送；不等于已执行或通过"
            };
            result.Details.Add("排队内容：" + FormatInputChain(script));
            result.Details.Add("排队发送不保证所有输入一定执行；交互命令只启动，不代选对象、不代填输入。请以命令行和后续检查结果为准。");
            result.Details.Add("若命令行报“未知命令”或报错，按键盘 Esc 结束当前命令，再回到本步处理。");
            return result;
        }

        // 把 "命令\n输入\n输入" 压成一行：(command "命令" "输入" "输入" "")。
        // 末尾空行（连续两个以上换行）表示要补一个空回车结束命令；只有一个换行只是行尾，不补。
        internal static string BuildOneShotInput(string script)
        {
            string normalized = (script ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            int end = normalized.Length;
            int trailingNewLines = 0;
            while (end > 0 && normalized[end - 1] == '\n')
            {
                trailingNewLines++;
                end--;
            }

            string[] parts = normalized.Substring(0, end).Split('\n');
            List<string> tokens = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length > 0)
                {
                    tokens.Add(token);
                }
            }
            if (tokens.Count == 0)
            {
                return string.Empty;
            }
            if (tokens.Count == 1)
            {
                // 只有命令名、不需要参数（例如启动 -HATCH 等用户点选）：原样送一行即可。
                return tokens[0];
            }
            if (trailingNewLines >= 2)
            {
                tokens.Add(string.Empty);
            }

            string form = "(command";
            for (int i = 0; i < tokens.Count; i++)
            {
                form += " \"" + tokens[i].Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }
            return form + ")";
        }

        internal static int CountInputs(string script)
        {
            string[] parts = (script ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int count = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Trim().Length > 0)
                {
                    count++;
                }
            }
            return count;
        }

        internal static string FormatInputChain(string script)
        {
            string[] parts = (script ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string text = string.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                string token = parts[i].Trim();
                if (token.Length == 0)
                {
                    continue;
                }
                text += (text.Length == 0 ? string.Empty : " → ") + token;
            }
            return text + (CountInputs(script) > 1 && (script ?? string.Empty).EndsWith("\n\n", StringComparison.Ordinal)
                ? " → 空回车"
                : string.Empty);
        }

        private static string WizardText(string text)
        {
            return (text ?? string.Empty).Replace("【高亮相关对象】", "当前动作提示")
                .Replace("【显示本步箭头】", "当前动作提示")
                .Replace("【步骤说明】", "当前动作说明");
        }

        private static QuestionDefinition RequireWizardStep(string questionId, LearningStep step)
        {
            EnsureQuestionBank();
            QuestionDefinition question = _questionBank.Find(questionId);
            if (question == null || step == null || question.Steps == null
                || !question.Steps.Exists(delegate(LearningStep candidate) { return object.ReferenceEquals(candidate, step); }))
            {
                throw new InvalidOperationException("当前步骤不是该题在已加载题库中的步骤引用，请重新选择题目和步骤。");
            }
            return question;
        }

        private static bool IsPracticeDocument(Document document, string questionId)
        {
            string boundQuestion;
            return document != null && _practiceDocuments.TryGetValue(document, out boundQuestion)
                && string.Equals(boundQuestion, questionId, StringComparison.Ordinal);
        }

        private static void RequirePracticeDocument(Document document, string questionId)
        {
            if (!IsPracticeDocument(document, questionId))
            {
                throw new InvalidOperationException("当前图纸不是本次会话中助手为本题准备的练习副本；请切回对应练习图，或从本题 open 步骤准备新副本。");
            }
        }

        // 【唯一的“命令是否在跑”判断】
        // 只看 CommandInProgress（命令运行/等待输入时是命令名，空闲时是空串）。
        // 绝对不要再用 Editor.IsQuiescent：PaletteSet（面板）显示期间，即使 CAD 完全空闲，
        // 它也会返回 false。第七版就是因为这条判断，把“第一步、什么都还没做”误判成
        // “命令卡死”，于是所有按钮变灰，点了又提示
        // “当前没有正在进行的命令”——死循环就是这么来的。这个坑不要再踩回去。
        internal static bool IsCommandRunning(Document document)
        {
            if (document == null)
            {
                return false;
            }
            try
            {
                string name = document.CommandInProgress;
                if (name == null)
                {
                    return false;
                }
                name = name.Trim().TrimStart('_', '.');
                if (name.Length == 0)
                {
                    return false;
                }
                // 本插件自己的命令（以及 AutoCAD 的 NETLOAD）不算“卡住”，
                // 否则刚加载完、或点完面板命令的那一瞬间，面板会自己把自己锁死。
                if (name.StartsWith("CADLEARN", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("NETLOAD", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }
            catch (System.Exception)
            {
                // 取不到时保守按正在运行处理，避免误发命令。
                return true;
            }
        }

        private static void RequireWizardIdle(Document document)
        {
            if (IsCommandRunning(document))
            {
                throw new InvalidOperationException("CAD 现在停在一个命令中间，命令行最后一行在等你输入。"
                    + "请按键盘 Esc，等命令行回到“命令:”再操作。");
            }
        }

        private static string FindPracticeSource(string sourceFile, out string projectRoot)
        {
            projectRoot = null;
            if (string.IsNullOrWhiteSpace(sourceFile) || Path.IsPathRooted(sourceFile)
                || sourceFile.IndexOf(':') >= 0)
            {
                throw new InvalidOperationException("SourceFile 必须是素材目录内的相对路径。");
            }
            string root = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            // 仅探测插件目录及向上最多六级的固定候选路径，不枚举任何目录。
            for (int level = 0; level <= 6 && !string.IsNullOrEmpty(root); level++)
            {
                string assets = Path.GetFullPath(Path.Combine(root, "research", "cad_assets", "2003CADST"));
                string source = Path.GetFullPath(Path.Combine(assets, sourceFile));
                RequireProjectPath(assets, source);
                if (File.Exists(source))
                {
                    // root 是当前探测到的项目根候选；练习副本也放在这个根目录下。
                    projectRoot = root;
                    return source;
                }
                DirectoryInfo parent = Directory.GetParent(root);
                root = parent == null ? null : parent.FullName;
            }
            throw new FileNotFoundException("插件目录及向上六级的 research/cad_assets/2003CADST 中未找到 SourceFile；未扫描其他目录。", sourceFile);
        }

        private static void RequireProjectPath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("练习路径超出允许的项目目录。");
            }
            // 拒绝联接点和符号链接，防止规范化后仍转向项目外部的个人目录。
            string current = fullPath;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("练习路径不能经过联接点或符号链接。");
                }
                if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool IsOpenAction(LearningStep step)
        {
            return step != null && string.Equals((step.Action ?? string.Empty).Trim(), "open", StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearSelectionHintsWhenIdle()
        {
            Document current = Application.DocumentManager.MdiActiveDocument;
            if (IsCommandRunning(current))
            {
                _pendingVisualCleanup = true;
                return;
            }
            _checker.ClearHighlights();
            _pendingVisualCleanup = false;
        }

        private static bool _probePending;
        private static long _probeStartTick;

        private static void TryAutoDumpRibbon()
        {
            if (!_probePending)
            {
                return;
            }
            if (_probeStartTick == 0)
            {
                _probeStartTick = Stopwatch.GetTimestamp();
                return;
            }
            if (Stopwatch.GetTimestamp() - _probeStartTick < Stopwatch.Frequency * 3)
            {
                return;
            }
            _probePending = false;
            try
            {
                // 只写文件，不改图形、不弹窗；失败也只是没有 txt，不影响任何功能。
                RibbonProbe.WriteDump();
            }
            catch (System.Exception exception)
            {
                WriteMessage("\n[CAD学习助手] 功能区结构导出失败（不影响使用）：" + exception.Message + "\n");
            }
        }

        private static void ApplicationIdle(object sender, EventArgs e)
        {
            // 加载 3 秒后自动导出一次功能区结构（只写一个 txt，不弹窗）。
            // 目的：不用用户再敲 CADLEARNRIBBON 也能拿到菜单/功能区/工具条的准确位置。
            TryAutoDumpRibbon();
            if (_refreshingWizard || _palette == null || !_palette.Visible
                || _paletteControl == null || _paletteControl.IsDisposed || !_paletteControl.Visible
                || _paletteControl.InvokeRequired)
            {
                return;
            }
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (IsCommandRunning(document))
            {
                // 命令进行中：只刷新按钮和提示，绝不读数据库、绝不检查。
                long busyNow = Stopwatch.GetTimestamp();
                if (_lastBusyRefresh != 0 && busyNow - _lastBusyRefresh < Stopwatch.Frequency / 2)
                {
                    return;
                }
                _lastBusyRefresh = busyNow;
                try
                {
                    _paletteControl.RefreshBusyNotice();
                }
                catch (System.Exception)
                {
                    // 忙状态刷新失败不能逃逸 Idle 回调，也不能每半秒刷一次诊断。
                }
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (_lastWizardRefresh != 0 && now - _lastWizardRefresh < Stopwatch.Frequency / 2)
            {
                return;
            }
            _lastWizardRefresh = now;
            _refreshingWizard = true;
            try
            {
                _paletteControl.RefreshBusyNotice();
                if (_pendingVisualCleanup)
                {
                    ClearSelectionHintsWhenIdle();
                }
                _paletteControl.RefreshWizardState();
            }
            catch (System.Exception exception)
            {
                // 生命周期或显示清理失败不能逃逸 Idle 回调，也不能每半秒重复弹出诊断。
                _pendingVisualCleanup = false;
                WriteMessage("\n[CAD学习助手] 空闲刷新失败：" + exception.Message + "\n");
            }
            finally
            {
                _refreshingWizard = false;
            }
        }

        internal static void CheckFromPalette(string questionId)
        {
            RunSafely("检查当前图纸", delegate
            {
                EnsureChecker();
                Document document = RequireDocument();
                _checker.SetActiveQuestion(questionId);
                // 检查是题目级操作，与当前步骤无关；这里不再重置步骤，
                // 否则面板停在第 N 步时点“检查”，检查器的当前步骤会被悄悄改回第 1 步。
                QuestionDefinition question = _questionBank == null ? null : _questionBank.Find(questionId);
                CheckResult result;
                using (DocumentLock documentLock = document.LockDocument())
                {
                    result = _checker.CheckActiveQuestion(document, question);
                }
                ShowResult(result, document);
            });
        }

        // 单步向导使用的检查入口：只检查当前步骤，不把后续步骤的要求提前算成失败。
        internal static CheckResult CheckStepFromPalette(string questionId, LearningStep step, bool silent = false)
        {
            // 现有面板自动刷新仍调用双参数入口，由刷新上下文保证它也静默。
            silent = silent || _refreshingWizard;
            CheckResult result;
            try
            {
                EnsureChecker();
                QuestionDefinition question = RequireWizardStep(questionId, step);
                if (!question.WizardReady)
                {
                    throw new InvalidOperationException("本题尚未完成题图核实，当前只允许查看教学资料，不能自动验收。");
                }
                Document document = RequireDocument();
                RequireWizardIdle(document);
                RequirePracticeDocument(document, question.Id);

                bool practice = false;
                bool other = false;
                if (step.CheckTypes != null)
                {
                    foreach (string declaredType in step.CheckTypes)
                    {
                        if (string.Equals((declaredType ?? string.Empty).Trim(), "practice", StringComparison.OrdinalIgnoreCase))
                        {
                            practice = true;
                        }
                        else
                        {
                            other = true;
                        }
                    }
                }
                if (practice)
                {
                    // practice 只确认会话绑定，不进入 checker，也不忽略混合类型。
                    result = new CheckResult
                    {
                        Passed = !other,
                        Summary = other ? "步骤配置错误：practice 不能与其他检查类型混用"
                            : "本题练习副本已由助手准备并绑定，起点检查通过"
                    };
                    result.Details.Add(other ? "本步未获通过；请将 practice 独立为起点步骤，其余规则另行检查。"
                        : "仅确认当前文档与本题的会话绑定，不代表图形内容或整题已完成。");
                }
                else
                {
                    _checker.SetActiveQuestion(question.Id);
                    _checker.SetActiveStep(step);
                    using (DocumentLock documentLock = document.LockDocument())
                    {
                        result = _checker.CheckActiveStep(document, question, step);
                    }
                }
            }
            catch (System.Exception exception)
            {
                result = new CheckResult
                {
                    Passed = false,
                    Summary = "这一步暂时无法检查"
                };
                result.Details.Add(exception.GetType().Name + ": " + exception.Message);
                result.Details.Add("请确认本题练习副本处于活动状态，并按 Esc 结束当前 CAD 命令后重试。");
            }
            // 调用方已负责显示返回值，不再回写面板，避免重复更新或递归。
            if (!silent)
            {
                WriteMessage("\n[CAD学习助手] " + result.Summary + "\n");
                foreach (string detail in result.Details)
                {
                    WriteMessage("  - " + detail + "\n");
                }
            }
            return result;
        }

        internal static void HighlightFromPalette(string questionId, LearningStep step)
        {
            RunSafely("高亮相关对象", delegate
            {
                EnsureChecker();
                Document document = RequireDocument();
                _checker.SetActiveQuestion(questionId);
                _checker.SetActiveStep(step);
                _checker.ClearHighlights();
                CheckResult result;
                using (DocumentLock documentLock = document.LockDocument())
                {
                    result = _checker.HighlightActiveQuestion(document);
                }
                ShowResult(result, document);
            });
        }

        // 面板把它想说的话全部改写到命令行：界面只留“选题目 / 当前步骤 / 结果图 / 这步怎么做 / 两个按钮”。
        // 不弹窗、不改图、不触发任何命令，写失败也不抛。
        internal static void WritePanelStatus(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            WriteMessage("\n[学习助手] " + text + "\n");
        }

        internal static void ClearHighlightsFromPalette()
        {
            RunSafely("清除高亮", delegate
            {
                EnsureChecker();
                _checker.ClearHighlights();
                ShowResult(new CheckResult { Summary = "已清除助手记录的高亮" },
                    Application.DocumentManager.MdiActiveDocument);
            });
        }

        private static void DocumentBecameCurrent(object sender, DocumentCollectionEventArgs e)
        {
            if (_paletteControl != null && !_paletteControl.IsDisposed)
            {
                _paletteControl.SetDocument(e.Document);
            }
        }

        private static void DocumentToBeDeactivated(object sender, DocumentCollectionEventArgs e)
        {
            ClearDocumentHighlights(e.Document, false);
            if (_paletteControl != null && !_paletteControl.IsDisposed)
            {
                _paletteControl.SetDocument(null);
            }
        }

        private static void DocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            DetachCommandEvents(e.Document);
            if (e.Document != null)
            {
                _practiceDocuments.Remove(e.Document);
            }
            ClearDocumentHighlights(e.Document, true);
            if (_paletteControl != null && !_paletteControl.IsDisposed)
            {
                _paletteControl.ForgetDocument(e.Document);
            }
        }

        private static void ClearDocumentHighlights(Document document, bool closing)
        {
            if (_checker == null || !_checker.HasHighlightsIn(document))
            {
                return;
            }
            try
            {
                _checker.ClearHighlights();
            }
            catch (System.Exception exception)
            {
                WriteMessage("\n[CAD学习助手] 清理图纸高亮失败：" + exception.Message + "\n");
            }
            finally
            {
                if (closing)
                {
                    // 销毁通知已开始释放图纸（不是可取消的关闭请求）。
                    // 即使显示清理失败也释放记录，不再访问即将失效的 ObjectId。
                    _checker.ForgetHighlights(document);
                }
            }
        }

        private static Document RequireDocument(bool requireIdle = true)
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                throw new InvalidOperationException("没有活动图纸，请先打开练习图。");
            }
            // 避免用户在另一条命令输入中点击无模式按钮。
            if (requireIdle && IsCommandRunning(document))
            {
                throw new InvalidOperationException("请先按 Esc 结束当前 CAD 命令，再使用助手按钮。");
            }
            return document;
        }

        private static void EnsureChecker()
        {
            if (_checker == null)
            {
                _checker = new AutoCadChecker();
            }
        }

        private static void EnsureQuestionBank()
        {
            // 题库为空时按需重载，便于修好 JSON 后不重启 AutoCAD 直接重试。
            if (_questionBank == null || _questionBank.Questions.Count == 0)
            {
                _questionBank = QuestionBank.LoadFromPluginDirectory();
            }
        }

        private static string BuildIdList()
        {
            string ids = string.Empty;
            foreach (QuestionDefinition question in _questionBank.Questions)
            {
                if (ids.Length > 0)
                {
                    ids += " / ";
                }
                ids += question.Id;
            }
            return ids;
        }

        private static void RunSafely(string operation, Action action)
        {
            try
            {
                action();
            }
            catch (System.Exception exception)
            {
                CheckResult result = new CheckResult
                {
                    Summary = operation + "失败（未判定题目对错）"
                };
                result.Details.Add(exception.GetType().Name + ": " + exception.Message);
                result.Details.Add("请先结束正在执行的命令；若仍失败，请保留以上错误信息。");
                ShowResult(result, Application.DocumentManager.MdiActiveDocument);
            }
        }

        private static void ShowResult(CheckResult result, Document document)
        {
            if (_paletteControl != null && !_paletteControl.IsDisposed)
            {
                _paletteControl.UpdateCheckResult(result);
            }
            if (document != null)
            {
                try
                {
                    document.Editor.WriteMessage("\n[CAD学习助手] " + result.Summary + "\n");
                    foreach (string detail in result.Details)
                    {
                        document.Editor.WriteMessage("  - " + detail + "\n");
                    }
                }
                catch (System.Exception)
                {
                    System.Diagnostics.Trace.WriteLine(result.Summary);
                }
            }
        }

        private static void CreatePalette()
        {
            _palette = new PaletteSet("CAD学习助手")
            {
                MinimumSize = new System.Drawing.Size(360, 600),
                Size = new System.Drawing.Size(420, 820),
                KeepFocus = false
            };
            try
            {
            _paletteControl = new LearningPalette(_questionBank);
            _palette.Add("学习面板", _paletteControl);
            _paletteControl.SetDocument(Application.DocumentManager.MdiActiveDocument);
            }
            catch
            {
                _palette.Dispose();
                _palette = null;
                _paletteControl = null;
                throw;
            }
        }

        private static void WriteMessage(string message)
        {
            // 生命周期事件中 Editor 也可能已失效，诊断不能再引发异常。
            try
            {
                Document document = Application.DocumentManager.MdiActiveDocument;
                if (document != null)
                {
                    document.Editor.WriteMessage(message);
                }
            }
            catch (System.Exception)
            {
                System.Diagnostics.Trace.WriteLine(message);
            }
        }
    }
}
