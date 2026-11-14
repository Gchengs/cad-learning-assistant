using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using CadLearningAssistant.Infrastructure;

namespace CadLearningAssistant.UI
{
    public class LearningPalette : UserControl
    {
        private sealed class QuestionProgress
        {
            public int CurrentStepIndex;
            public bool HasPassedFinalSaveCheck;
        }

        private sealed class DocumentReferenceComparer : IEqualityComparer<Document>
        {
            public bool Equals(Document left, Document right)
            {
                return object.ReferenceEquals(left, right);
            }

            public int GetHashCode(Document document)
            {
                return RuntimeHelpers.GetHashCode(document);
            }
        }

        private readonly QuestionBank _questionBank;
        private readonly ComboBox _questionSelector;
        private readonly Label _titleText;
        private readonly Label _progressLabel;
        private readonly TextBox _inputText;
        // 最终结果示意图：每题一张 PNG（content\images\<题号>.png）。
        // 面板上原来这块是空白，用户不知道“做完了应该是什么样”，图放这里正好补上。
        private readonly PictureBox _previewPicture;
        private readonly Label _previewCaption;
        private readonly Dictionary<string, Image> _imageCache;
        private readonly Button _primaryButton;
        private readonly Button _checkButton;
        private bool _commandQueued;
        private bool _inputDeliveryUncertain;
        private bool _autoCheckSuspended;
        private bool _relevantCommandStarted;
        private DateTime _commandQueuedAtUtc;
        private bool _dispatchingPrimary;
        private readonly Dictionary<Document, Dictionary<string, QuestionProgress>> _documentProgress;
        private readonly Font _largeButtonFont;
        private readonly Font _normalFont;
        private QuestionDefinition _activeQuestion;
        private Document _currentDocument;
        private int _currentStepIndex;
        private int _selectedQuestionIndex = -1;
        private bool _suppressSelectionEvents;
        private bool _checking;
        private bool _stepStarted;
        private bool _autoCheckPending;
        private int _documentVersion;
        private int _wizardContextVersion;
        private volatile bool _disposeRequested;
        private bool _startingStep;
        private bool _finalReview;
        private bool _busyNoticeShown;
        private string _lastStatus;

        private static readonly Color PrimaryColor = Color.FromArgb(30, 111, 184);
        private static readonly Color SecondaryColor = Color.FromArgb(238, 244, 249);

        public LearningPalette(QuestionBank questionBank)
        {
            _questionBank = questionBank;
            _documentProgress = new Dictionary<Document, Dictionary<string, QuestionProgress>>(
                new DocumentReferenceComparer());
            _largeButtonFont = new Font("Microsoft YaHei", 10F, FontStyle.Bold);
            _normalFont = new Font("Microsoft YaHei", 9F, FontStyle.Regular);
            _imageCache = new Dictionary<string, Image>();

            Dock = DockStyle.Fill;
            MinimumSize = new Size(360, 600);
            AutoScroll = false;
            BackColor = Color.White;
            Padding = new Padding(10);
            VisibleChanged += PaletteVisibleChanged;

            _questionSelector = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = _normalFont,
                IntegralHeight = false,
                DropDownHeight = 300,
                // 题目标题普遍比面板宽，下拉列表跟着面板宽会把后半截全截掉，
                // 26 道题只看到一样的开头。下拉列表单独放宽。
                DropDownWidth = 460,
                Margin = new Padding(0, 0, 0, 6)
            };
            _questionSelector.SelectedIndexChanged += QuestionChanged;

            // 步骤标题用 Label 而不是只读 TextBox：去掉输入框边框，视觉上就是“一行标题”，
            // 也不再出现滚动条这种一看就像“又要我填东西”的控件。
            _titleText = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(31, 78, 121),
                Font = new Font("Microsoft YaHei", 13F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 0)
            };
            _inputText = CreateDisplayTextBox(74, 10F, FontStyle.Bold, Color.FromArgb(122, 61, 0));

            _previewCaption = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(89, 89, 89),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                Margin = new Padding(0, 0, 0, 2),
                Text = "最终结果示意"
            };
            _previewPicture = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                MinimumSize = new Size(0, 120),
                Margin = new Padding(0, 0, 0, 6),
                Cursor = Cursors.Hand
            };
            // 面板里图只有三百多像素宽，细节看不清；点一下给一张大的。
            _previewPicture.Click += PreviewPictureClicked;

            _progressLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(89, 89, 89),
                Font = _normalFont,
                Margin = new Padding(0, 2, 0, 6)
            };

            _primaryButton = CreateButton("我不会，帮我继续", PrimaryColor, Color.White, 50);
            _primaryButton.Font = _largeButtonFont;
            _primaryButton.Click += PrimaryActionClicked;
            _checkButton = CreateButton("检查", SecondaryColor, Color.FromArgb(31, 78, 121), 36);
            _checkButton.Click += CheckClicked;

            // 四块：选题目、当前步骤、最终结果图、这步怎么做。
            // 原来说明区下面一大片空白，现在给了示意图；图直接回答“做完是什么样”，
            // 下面三行只说“点什么、敲什么、记什么”。
            // 辅助方式下拉、当前提示、现在怎么做、命令参考、更多面板都已删除：
            // 两种辅助方式实际都走“代输命令”，留着只会让人以为是两件事；
            // 结果与诊断改为写进 CAD 命令行（Ctrl+9 可见），不再占面板空间。
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.White,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // 图区固定 210px 高：之前放在 Percent 行里，遇到“做完后自查”这种长文字会被压到只剩几十像素，
            // 图小到看不出是什么。固定高 + 下面文字行占剩余空间，两边都不会被挤没。
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            layout.Controls.Add(_questionSelector, 0, 0);
            layout.Controls.Add(CreateStepHeader(_titleText, _progressLabel), 0, 1);
            layout.Controls.Add(CreatePreviewSection(_previewCaption, _previewPicture), 0, 2);
            layout.Controls.Add(CreateTextSection("这步怎么做", _inputText), 0, 3);

            Panel contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                // 布局改成 Fill 后不再需要外层滚动：长题面由细节框自己滚。
                AutoScroll = false,
                Margin = new Padding(0)
            };
            contentPanel.Controls.Add(layout);
            TableLayoutPanel actions = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
            actions.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            actions.Controls.Add(CreateButtonRow(_primaryButton), 0, 0);
            actions.Controls.Add(CreateButtonRow(_checkButton), 0, 1);
            Controls.Add(contentPanel);
            Controls.Add(actions);
            LoadQuestions();
            RefreshButtonState();
        }

        // 第二行：步骤标题 + 进度/绑定状态。两行都用固定高度，
        // 否则 TextBox/Label 在 AutoSize 行里会被压成一行高，文字看不全。
        private static Control CreateStepHeader(Control title, Control progress)
        {
            TableLayoutPanel header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 56F));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(progress, 0, 1);
            return header;
        }

        private bool DeferIfRequired(Action action)
        {
            if (_disposeRequested || IsDisposed || Disposing || !IsHandleCreated)
            {
                return true;
            }
            if (!InvokeRequired)
            {
                return false;
            }
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (!_disposeRequested && !IsDisposed && !Disposing && IsHandleCreated)
                    {
                        action();
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // 排队时句柄可能已销毁；ObjectDisposedException 也属于此异常类型。
            }
            return true;
        }

        private void InvalidateWizardCheck()
        {
            _autoCheckPending = false;
            _commandQueued = false;
            Plugin.ClearMenuPointer();
            System.Threading.Interlocked.Increment(ref _wizardContextVersion);
        }

        private void ResetCurrentStepInteraction()
        {
            _stepStarted = false;
            _autoCheckSuspended = false;
            _relevantCommandStarted = false;
            _busyNoticeShown = false;
            _commandQueued = false;
            _commandQueuedAtUtc = DateTime.MinValue;
            _inputDeliveryUncertain = false;
            _finalReview = false;
        }

        public void SetDocument(Document document)
        {
            int version = System.Threading.Interlocked.Increment(ref _documentVersion);
            ApplyDocument(document, version);
        }

        private void ApplyDocument(Document document, int version)
        {
            if (DeferIfRequired(delegate { ApplyDocument(document, version); })
                || version != System.Threading.Interlocked.CompareExchange(ref _documentVersion, 0, 0))
            {
                return;
            }
            if (document != null && !object.ReferenceEquals(document, CadApplication.DocumentManager.MdiActiveDocument))
            {
                return;
            }

            if (object.ReferenceEquals(_currentDocument, document))
            {
                RefreshView();
                return;
            }

            InvalidateWizardCheck();
            _currentDocument = document;
            ResetCurrentStepInteraction();
            _checking = false;
            ClearStatus();
            RestoreCurrentQuestionProgress();
            RefreshView();
            NotifyPluginOfCurrentSelection();
        }

        public void ForgetDocument(Document document)
        {
            if (DeferIfRequired(delegate { ForgetDocument(document); }))
            {
                return;
            }
            if (document == null)
            {
                return;
            }

            _documentProgress.Remove(document);
            if (object.ReferenceEquals(_currentDocument, document))
            {
                InvalidateWizardCheck();
                _currentDocument = null;
                ResetCurrentStepInteraction();
                _checking = false;
                ClearStatus();
                RefreshView();
            }
        }

        private bool IsRelevantCommand(string commandName)
        {
            LearningStep step = GetCurrentStep();
            string name = SafeText(commandName).Trim().TrimStart('_', '.').ToUpperInvariant();
            if (step == null || step.CheckTypes == null || name.Length == 0)
            {
                return false;
            }
            foreach (string checkType in step.CheckTypes)
            {
                string type = SafeText(checkType).Trim().ToLowerInvariant();
                if ((type == "layer-exists" || type == "layer-red" || type == "current-layer")
                    && (name == "LAYER" || name == "-LAYER" || name == "CLAYER")) return true;
                if ((type == "hpname" || type == "hpscale" || type == "hpang" || type == "hplayer" || type == "cecolor")
                    && (name == type.ToUpperInvariant() || name == "SETVAR")) return true;
                if ((type == "hatch-on-layer" || type == "pattern" || type == "color" || type == "placement")
                    && (name == "HATCH" || name == "-HATCH" || name == "HATCHEDIT" || name == "-HATCHEDIT"
                        || name == "CHPROP" || name == "CHANGE" || name == "PROPERTIES")) return true;
                if (type == "saved" && (name == "SAVE" || name == "QSAVE" || name == "SAVEAS")) return true;
            }
            return false;
        }

        public void NotifyCommandStarted(Document document, string commandName)
        {
            if (DeferIfRequired(delegate { NotifyCommandStarted(document, commandName); })) return;
            if (!_autoCheckSuspended && _stepStarted && IsCurrentDocumentActive()
                && object.ReferenceEquals(document, _currentDocument) && IsRelevantCommand(commandName))
            {
                _commandQueued = false;
                _inputDeliveryUncertain = false;
                _relevantCommandStarted = true;
                RefreshButtonState();
            }
        }

        public void QueueWizardCheck(Document document, string commandName)
        {
            int version = System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0);
            ApplyQueuedWizardCheck(document, commandName, version);
        }

        private void ApplyQueuedWizardCheck(Document document, string commandName, int version)
        {
            if (DeferIfRequired(delegate { ApplyQueuedWizardCheck(document, commandName, version); }))
            {
                return;
            }
            if (version == System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0)
                && !_autoCheckSuspended && _stepStarted && object.ReferenceEquals(document, _currentDocument)
                && IsCurrentDocumentActive() && IsRelevantCommand(commandName))
            {
                _commandQueued = false;
                _inputDeliveryUncertain = false;
                _relevantCommandStarted = false;
                _autoCheckPending = true;
            }
        }

        public void RefreshWizardState()
        {
            if (DeferIfRequired(RefreshWizardState))
            {
                return;
            }
            if (!Visible || !Plugin.IsLearningPaletteVisible)
            {
                return;
            }
            // 未观察到相关命令开始/结束时，超时只解锁求助，不把“发送成功”当成完成。
            if (_commandQueued && _commandQueuedAtUtc != DateTime.MinValue
                && DateTime.UtcNow - _commandQueuedAtUtc >= TimeSpan.FromSeconds(3) && !IsCommandBusy())
            {
                _commandQueued = false;
                _autoCheckPending = false;
                _autoCheckSuspended = true;
                _inputDeliveryUncertain = true;
                SetStatus("尚未确认本步命令执行完毕。没有自动推进；可再点同一按钮检查实际状态，不会重发未确认的输入。" );
            }
            RefreshButtonState();
            if (!_autoCheckPending || _checking || _startingStep || _dispatchingPrimary || _commandQueued
                || _autoCheckSuspended || !IsWizardReady() || !_stepStarted || !IsCurrentDocumentActive())
            {
                return;
            }
            LearningStep step = GetCurrentStep();
            if (step == null || IsManualAction(step) || IsCommandBusy())
            {
                return;
            }

            CheckResult result = CheckForPrimary(step, step.AutoAdvance, false);
            if (result == null || !result.Passed || result.RequiresManual)
            {
                ShowAutomaticRetryHelp(step, result);
            }
            else if (!step.AutoAdvance)
            {
                SetStatus(BuildResultText(result) + Environment.NewLine
                    + "本步检查通过。再点【我不会，帮我继续】进入下一步；未自动跳步。" );
            }
        }

        private void ShowAutomaticRetryHelp(LearningStep step, CheckResult result)
        {
            if (step == null)
            {
                return;
            }
            string message = result == null ? "自动检查没有返回结果。" : BuildResultText(result);
            SetStatus(message + Environment.NewLine + "自动检查未通过；下一次点【我不会，帮我继续】可重试，不会自动重复发送命令。"
                + Environment.NewLine + BuildCoachText(step));
        }

        private static TextBox CreateDisplayTextBox(int minimumHeight, float fontSize, FontStyle style, Color foreColor)
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, minimumHeight),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei", fontSize, style),
                ForeColor = foreColor,
                TabStop = false,
                Margin = new Padding(0, 0, 0, 5),
                Padding = new Padding(5)
            };
        }

        private static Button CreateButton(string text, Color backColor, Color foreColor, int height)
        {
            return new Button
            {
                Dock = DockStyle.Fill,
                MinimumSize = new Size(0, height),
                Height = height,
                Text = text,
                BackColor = backColor,
                ForeColor = foreColor,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular),
                // 必须同时保持 Standard + UseVisualStyleBackColor=false：
                // FlatStyle.System 会把绘制交给系统主题，BackColor/ForeColor 会被直接忽略，
                // 防止唯一主按钮的强调色被主题覆盖为灰色。
                UseVisualStyleBackColor = false,
                FlatStyle = FlatStyle.Standard,
                Margin = new Padding(2)
            };
        }

        // 说明区：一行小标题 + 一个填满剩余高度的只读文本框。
        // 不再按文字长度量高度（上一版那样）：量出来的高度会把图区挤到几十像素，
        // 而且文字换行后行数会抖。现在框固定占剩下的空间，长文字自己在框里滚。
        private static Control CreateTextSection(string caption, Control control)
        {
            TableLayoutPanel section = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            section.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Label label = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Text = caption,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(89, 89, 89),
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                Margin = new Padding(0)
            };
            section.Controls.Add(label, 0, 0);
            section.Controls.Add(control, 0, 1);
            return section;
        }

        // 图区：上面一行小字说明这是什么图，下面把剩余高度全给图（Zoom 保持比例）。
        // 用 Percent 行而不是固定高度，面板拉高时图自己变大，不会留下一片空白。
        private static Control CreatePreviewSection(Label caption, PictureBox picture)
        {
            TableLayoutPanel section = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
            section.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            section.Controls.Add(caption, 0, 0);
            section.Controls.Add(picture, 0, 1);
            return section;
        }

        private static Control CreateButtonRow(params Control[] controls)
        {
            TableLayoutPanel row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = controls.Length,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            float width = 100F / controls.Length;
            for (int index = 0; index < controls.Length; index++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
                row.Controls.Add(controls[index], index, 0);
            }
            return row;
        }

        private void LoadQuestions()
        {
            _suppressSelectionEvents = true;
            try
            {
                foreach (QuestionDefinition question in _questionBank.Questions)
                {
                    // 26 道题里只有 3.1 能自动验收；下拉里不加标记，用户就永远不知道
                    // “为什么这题只给我看文字、不帮我检查”。标记只写在能验收的那一题上。
                    string suffix = question.WizardReady ? "　·　可自动验收" : string.Empty;
                    _questionSelector.Items.Add(SafeText(question.Id) + "  " + SafeText(question.Title) + suffix);
                }
                if (_questionSelector.Items.Count > 0)
                {
                    _questionSelector.SelectedIndex = 0;
                }
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            if (_questionSelector.Items.Count == 0)
            {
                _titleText.Text = "题库加载失败";
                _progressLabel.Text = SafeText(_questionBank.LoadError ?? ("题库为空。来源：" + _questionBank.SourcePath));
                SetButtonsEnabled(false);
                WriteStatus("题库加载失败：" + SafeText(_questionBank.LoadError ?? _questionBank.SourcePath));
                return;
            }

            QuestionChanged(this, EventArgs.Empty);
        }

        private void QuestionChanged(object sender, EventArgs e)
        {
            if (_suppressSelectionEvents || _questionSelector.SelectedIndex < 0)
            {
                return;
            }

            if (_activeQuestion != null && !CanChangeWizardSelection())
            {
                _suppressSelectionEvents = true;
                try
                {
                    _questionSelector.SelectedIndex = _selectedQuestionIndex;
                }
                finally
                {
                    _suppressSelectionEvents = false;
                }
                return;
            }

            try
            {
                InvalidateWizardCheck();
                _selectedQuestionIndex = _questionSelector.SelectedIndex;
                _activeQuestion = _questionBank.Questions[_selectedQuestionIndex];
                ResetCurrentStepInteraction();
                _checking = false;
                ClearStatus();
                RestoreCurrentQuestionProgress();
                RefreshView();
                NotifyPluginOfCurrentSelection();
            }
            catch (Exception exception)
            {
                ShowDiagnostic("切换题目失败：" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private void PaletteVisibleChanged(object sender, EventArgs e)
        {
            if (!Visible)
            {
                Plugin.ClearMenuPointer();
            }
        }

        private void PrimaryActionClicked(object sender, EventArgs e)
        {
            if (_checking || _startingStep || _dispatchingPrimary)
            {
                return;
            }
            _dispatchingPrimary = true;
            _primaryButton.Enabled = false;
            _lastStatus = null; // 每点一次都要有回音：同样的结论也要再打一遍，不能看起来像没反应。
            try
            {
                LearningStep step = GetCurrentStep();
                if (IsCommandBusy())
                {
                    ShowBusyHelp(step);
                    return;
                }
                if (_commandQueued)
                {
                    SetStatus("本步输入正在等待 CAD 接收，不会重复发送。请稍候；仍未执行时将解除等待，保留当前步骤。" );
                    return;
                }
                _autoCheckSuspended = false;
                if (_activeQuestion == null || step == null)
                {
                    SetStatus("当前没有可用步骤。");
                    return;
                }
                if (!IsWizardReady())
                {
                    // 未核实题：点“我不会”只解释这一步怎么做，绝不代替用户动手，也不推进步骤。
                    // 以前这里直接跳到下一步，等于把“我不会”变成“跳过”，是错的。
                    ShowStepGuidance(step);
                    return;
                }
                if (_finalReview)
                {
                    CheckResult finalResult = CheckForPrimary(step, false, false);
                    if (finalResult != null && finalResult.Passed && !finalResult.RequiresManual)
                    {
                        // 整题检查拥有最终显示权，不能再用保存检查结果覆盖它。
                        Plugin.CheckFromPalette(_activeQuestion.Id);
                    }
                    else
                    {
                        _finalReview = false;
                        ProvideRetryHelp(step, finalResult, true);
                    }
                    return;
                }
                if (!_stepStarted)
                {
                    StartOrHelpCurrentStep(false);
                    return;
                }
                CheckResult result = CheckForPrimary(step, true, false);
                if (result == null || !result.Passed || result.RequiresManual)
                {
                    ProvideRetryHelp(step, result, false);
                }
            }
            finally
            {
                _dispatchingPrimary = false;
                RefreshButtonState();
            }
        }

        private void ShowBusyHelp(LearningStep step)
        {
            SetStatus("CAD 当前命令仍在进行；不会取消或再次输入命令。请按命令行当前提示完成，结束后再点本按钮检查。"
                + Environment.NewLine + BuildCoachText(step));
        }

        // 未核实题的唯一求助路径：说清“这一步点哪里、敲什么”，经典菜单能定位就指出来。
        // 不发送命令、不猜点、不推进步骤；推进只能由用户点【我做好了，下一步】自己确认。
        private void ShowStepGuidance(LearningStep step)
        {
            if (step == null)
            {
                SetStatus("当前没有可用步骤。");
                return;
            }
            string panel = SafeText(step.PanelPath).Trim();
            string command = SafeText(step.Command).Trim();
            string message = "第 " + step.Order + " 步：" + SafeText(step.Title)
                + "（题库指引，未在本机核实；助手不代输命令、不验收）";
            if (panel.Length > 0)
            {
                message += Environment.NewLine + "面板：" + panel;
            }
            else if (command.Length > 0)
            {
                message += Environment.NewLine + "本步没有面板入口，直接敲“这步怎么做”里的命令。";
            }
            else
            {
                message += Environment.NewLine + "本步不用点也不用敲，先读“这步怎么做”里的题目要求。";
            }
            // 指到了要显示指到哪；指不到更要显示为什么——上一版只有成功时才写消息，
            // 失败原因被吞掉，用户只会看到“点了没反应”。
            if (!string.IsNullOrWhiteSpace(step.MenuPath))
            {
                string pointerMessage;
                Plugin.TryShowMenuPointer(step, out pointerMessage);
                if (!string.IsNullOrEmpty(pointerMessage))
                {
                    message += Environment.NewLine + pointerMessage;
                }
            }
            // 按钮文字随步骤变（读题/自查是【下一步】，动手步骤是【我做好了，下一步】），
            // 这里必须跟着说同一个名字，否则用户会去找一个不存在的按钮。
            if (IsLastStep())
            {
                message += Environment.NewLine + "这已经是最后一步：对照“操作细节”里的自查逐条确认即可。";
            }
            else
            {
                message += Environment.NewLine + "按“操作细节”做完后，点【" + SafeText(_checkButton.Text) + "】。";
            }
            SetStatus(message);
        }

        // 未核实题没有验收规则，所以这里只记录“用户说做完了”，绝不宣称已通过。
        // 命令进行中不允许翻页，避免在交互中途把步骤换掉。
        private void AdvanceManualStep(LearningStep step)
        {
            if (step == null || !CanChangeWizardSelection())
            {
                return;
            }
            if (IsLastStep())
            {
                SetStatus("已经是最后一步。本题未在本机核实，助手不能替你宣告做完；"
                    + "请对照“做完后自查”逐条确认，再自行保存。");
                return;
            }
            InvalidateWizardCheck();
            ResetCurrentStepInteraction();
            _currentStepIndex = ClampStepIndex(_currentStepIndex + 1);
            SaveCurrentStepIndex();
            RefreshView();
            NotifyPluginOfCurrentSelection();
            SetStatus("进到第 " + (_currentStepIndex + 1) + " 步。本题未自动验收：上一步是否真的做对，要你对照图形确认。");
        }

        private void ProvideRetryHelp(LearningStep step, CheckResult result)
        {
            ProvideRetryHelp(step, result, false);
        }

        private void ProvideRetryHelp(LearningStep step, CheckResult result, bool allowFinalReview)
        {
            if (step == null || !object.ReferenceEquals(step, GetCurrentStep()) || !IsCurrentDocumentActive())
            {
                return;
            }
            string message = result == null ? "检查器没有返回结果。" : BuildResultText(result);
            if (_inputDeliveryUncertain)
            {
                SetStatus(message + Environment.NewLine
                    + "先前输入尚未确认执行或中断，本次只检查和解释，不重复发送。请查看命令行；需要放弃时手工按 Esc。"
                    + Environment.NewLine + BuildCoachText(step));
                return;
            }
            if (!allowFinalReview && IsManualAction(step))
            {
                // 手工步骤不代发命令，也不代点：只解释，等你自己做完。
                SetStatus(message + Environment.NewLine + "当前步骤未通过；请按提示处理后，再点本按钮重试。"
                    + Environment.NewLine + BuildCoachText(step));
                return;
            }
            RunStepScriptForCurrentStep(step, message);
        }

        private void RunStepScriptForCurrentStep(LearningStep step, string prefix)
        {
            if (_activeQuestion == null || step == null || IsManualAction(step))
            {
                SetStatus(SafeText(prefix) + Environment.NewLine + "本步需要手工操作，不能安全代选；完成后再点本按钮重试。"
                    + Environment.NewLine + BuildCoachText(step));
                return;
            }
            try
            {
                // 先建立等待状态，避免同步命令事件早于 SendStringToExecute 返回而被覆盖。
                _stepStarted = true;
                _autoCheckSuspended = false;
                _relevantCommandStarted = false;
                _commandQueued = true;
                _commandQueuedAtUtc = DateTime.UtcNow;
                CheckResult result = Plugin.RunStepScript(_activeQuestion.Id, step);
                if (result == null || !result.Passed)
                {
                    _commandQueued = false;
                    _autoCheckPending = false;
                    SetStatus(SafeText(prefix) + Environment.NewLine + BuildResultText(result)
                        + Environment.NewLine + "命令未确认发送；处理当前步骤后，再点本按钮重试。"
                        + Environment.NewLine + BuildCoachText(step));
                    return;
                }
                SetStatus(SafeText(prefix) + Environment.NewLine + BuildResultText(result)
                    + Environment.NewLine + "命令已发送；命令结束后会自动检查一次，若仍失败请再次点本按钮重试。"
                    + Environment.NewLine + BuildCoachText(step));
            }
            catch (Exception exception)
            {
                _commandQueued = false;
                _autoCheckPending = false;
                _autoCheckSuspended = true;
                ShowDiagnostic(SafeText(prefix) + Environment.NewLine + "发送命令失败："
                    + exception.GetType().Name + ": " + exception.Message
                    + Environment.NewLine + "请处理当前步骤后，再点本按钮重试。"
                    + Environment.NewLine + BuildCoachText(step));
            }
        }

        private CheckResult CheckForPrimary(LearningStep step, bool allowAdvance, bool advanceManual)
        {
            if (_checking || _startingStep || step == null || _activeQuestion == null || !IsWizardReady()
                || !IsCurrentDocumentActive() || !IsDocumentIdle(_currentDocument))
            {
                return null;
            }
            _autoCheckPending = false;
            _checking = true;
            InvalidateWizardCheck();
            Document checkedDocument = _currentDocument;
            int contextVersion = System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0);
            try
            {
                CheckResult result = Plugin.CheckStepFromPalette(_activeQuestion.Id, step, true);
                if (contextVersion != System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0)
                    || !IsCheckContextCurrent(checkedDocument, step))
                {
                    SetStatus("检查期间图纸或步骤已改变，未推进；请确认当前练习图后再点本按钮。" );
                    return null;
                }
                if (result != null && result.Passed && !result.RequiresManual)
                {
                    RememberFinalSaveCheck(step, result);
                    if (allowAdvance && (advanceManual || !IsManualAction(step)))
                    {
                        AdvanceOneStep();
                    }
                }
                if (result != null && (!result.Passed || result.RequiresManual || !allowAdvance))
                {
                    ShowCheckResult(result);
                }
                return result;
            }
            catch (Exception exception)
            {
                ShowDiagnostic("检查这一步出错：" + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
            finally
            {
                _checking = false;
                RefreshButtonState();
            }
        }

        private void StartOrHelpCurrentStep(bool unused)
        {
            LearningStep step = GetCurrentStep();
            if (!CanUseWizard(step))
            {
                SetStatus(CannotStartText(step));
                return;
            }
            QuestionDefinition question = _activeQuestion;
            Document startingDocument = _currentDocument;
            int startingIndex = _currentStepIndex;
            _startingStep = true;
            InvalidateWizardCheck();
            ResetCurrentStepInteraction();
            try
            {
                Plugin.StartWizardAction(question.Id, step);
                Document document = CadApplication.DocumentManager.MdiActiveDocument;
                if (document == null || !object.ReferenceEquals(_activeQuestion, question)
                    || (!IsOpenAction(step) && !object.ReferenceEquals(document, startingDocument)))
                {
                    throw new InvalidOperationException("启动后活动图纸或题目已改变，本步未启动。");
                }
                if (!object.ReferenceEquals(_currentDocument, document))
                {
                    SetDocument(document);
                }
                InvalidateWizardCheck();
                _currentStepIndex = ClampStepIndex(startingIndex);
                SaveCurrentStepIndex();
                _stepStarted = true;
                RefreshView();
                if (!IsCheckContextCurrent(document, step))
                {
                    throw new InvalidOperationException("启动后绑定图纸或当前步骤不一致，本步未启动。");
                }
                SetStatus("已准备当前步骤。现在检查一次实际状态；未通过时按所选方式提供当前帮助。"
                    + Environment.NewLine + BuildCoachText(step));
                _startingStep = false;
                CheckResult result = CheckForPrimary(step, true, false);
                if (result == null || !result.Passed || result.RequiresManual)
                {
                    ProvideRetryHelp(step, result, false);
                }
            }
            catch (Exception exception)
            {
                ResetCurrentStepInteraction();
                InvalidateWizardCheck();
                ShowDiagnostic("启动这一步出错：" + exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                _startingStep = false;
                RefreshButtonState();
            }
        }

        private void NextClicked(object sender, EventArgs e)
        {
            if (!CanChangeWizardSelection())
            {
                return;
            }
            LearningStep step = GetCurrentStep();
            if (!IsWizardReady() && step != null && !IsLastStep())
            {
                InvalidateWizardCheck();
                ResetCurrentStepInteraction();
                _currentStepIndex = ClampStepIndex(_currentStepIndex + 1);
                SaveCurrentStepIndex();
                RefreshView();
                NotifyPluginOfCurrentSelection();
                SetStatus("仅查看下一条（未核实），没有执行检查，也不代表本步或上一条已完成。");
                return;
            }
            CheckResult result = CheckForPrimary(step, true, true);
            if (result == null || !result.Passed || result.RequiresManual)
            {
                SetStatus("手动检查未通过或未返回结果；手工步骤不会自动推进。完成当前动作后可再次检查。"
                    + Environment.NewLine + BuildCoachText(step));
            }
        }

        // 唯一的检查按钮：平时检查当前步骤；走到最后一步且已通过时，做整题检查。
        private void CheckClicked(object sender, EventArgs e)
        {
            _lastStatus = null;
            LearningStep step = GetCurrentStep();
            if (!IsWizardReady())
            {
                // 未核实题的检查按钮换成“我做好了，下一步”：只翻页，不做验收。
                AdvanceManualStep(step);
                return;
            }
            if (_finalReview)
            {
                if (_activeQuestion == null || !IsWizardReady() || !IsCurrentDocumentActive())
                {
                    WriteStatus("本题未核实，或绑定图纸不是活动图纸，不能进行整题检查。");
                    return;
                }
                try
                {
                    _finalReview = false;
                    Plugin.CheckFromPalette(_activeQuestion.Id);
                    return;
                }
                catch (Exception exception)
                {
                    ShowDiagnostic("整题检查出错：" + exception.GetType().Name + ": " + exception.Message);
                    return;
                }
            }
            CheckResult result = CheckForPrimary(step, false, false);
            if (result == null || !result.Passed || result.RequiresManual)
            {
                SetStatus((result == null ? "检查未执行。" : BuildResultText(result))
                    + Environment.NewLine + BuildCoachText(step));
            }
            else
            {
                WriteStatus("本步检查通过：" + BuildResultText(result));
            }
        }

        private void AdvanceOneStep()
        {
            InvalidateWizardCheck();
            ResetCurrentStepInteraction();
            if (IsLastStep())
            {
                _finalReview = true;
                SetStatus("本步检查通过；这是最后一步。下一次点本按钮会重新检查保存状态，通过后才发起整题检查；整题结果不代表区域位置全部正确。" );
                RefreshButtonState();
                return;
            }

            _currentStepIndex = ClampStepIndex(_currentStepIndex + 1);
            SaveCurrentStepIndex();
            RefreshView();
            NotifyPluginOfCurrentSelection();
            SetStatus("上一动作检查通过。当前步骤尚未启动；点本按钮会在本次点击内准备并检查当前步骤，最多推进一步。"
                + Environment.NewLine + BuildCoachText(GetCurrentStep()));
        }

        private void RefreshView()
        {
            RenderCurrentStep();
            RefreshProgress();
            RefreshButtonState();
        }

        private void RenderCurrentStep()
        {
            LearningStep step = GetCurrentStep();
            if (_activeQuestion == null || step == null)
            {
                _titleText.Text = string.Empty;
                _inputText.Clear();
                _previewPicture.Image = null;
                return;
            }

            _titleText.Text = step.Order + ". " + SafeText(step.Title);
            _inputText.Text = BuildInputText(step);
            SelectBeginning(_inputText);
            ShowResultImage(_activeQuestion);
        }

        // 每道题一张结果图，放在 DLL 同目录 images\<题号>.png。
        // 图是静态文件而不是代码画的：换图不用重新编译，画错也不会把插件搞崩。
        private void ShowResultImage(QuestionDefinition question)
        {
            Image image = question == null ? null : LoadResultImage(question);
            _previewPicture.Image = image;
            if (image == null)
            {
                _previewCaption.Text = "本题没有结果示意图";
                _previewCaption.ForeColor = Color.FromArgb(150, 70, 60);
                return;
            }
            _previewCaption.ForeColor = Color.FromArgb(89, 89, 89);
            // 配套盘子里有原图（SourceFile 非空）的是真图渲染，其余是按题面尺寸画的示意。
            bool fromDwg = SafeText(question.SourceFile).Trim().Length > 0;
            _previewCaption.Text = fromDwg
                ? "最终结果（配套 DWG 渲染）· 点图放大"
                : "最终结果示意（按题面尺寸画）· 点图放大";
        }

        private Image LoadResultImage(QuestionDefinition question)
        {
            string id = SafeText(question.Id).Trim();
            if (id.Length == 0)
            {
                return null;
            }
            Image cached;
            if (_imageCache.TryGetValue(id, out cached))
            {
                return cached;
            }
            Image image = null;
            try
            {
                string directory = Path.GetDirectoryName(typeof(LearningPalette).Assembly.Location);
                string path = Path.Combine(directory ?? string.Empty, "images", id + ".png");
                if (File.Exists(path))
                {
                    // Image.FromStream 要求流在图片存活期间一直打开；这里复制成独立 Bitmap，
                    // 之后可以安全关流，也不会因为文件被覆盖而画出花屏。
                    using (FileStream stream = File.OpenRead(path))
                    using (Image loaded = Image.FromStream(stream))
                    {
                        image = new Bitmap(loaded);
                    }
                }
            }
            catch (IOException)
            {
                image = null;
            }
            catch (UnauthorizedAccessException)
            {
                image = null;
            }
            catch (ArgumentException)
            {
                // 文件不是有效图片（下载/复制损坏）。
                image = null;
            }
            _imageCache[id] = image;
            return image;
        }

        // 面板里图只有三百多像素，孔位、尺寸看不清；点一下给一张大图，Esc 关。
        // 窗口按屏幕可用区域取 88%，不再写死 1024×760：小屏会超出屏幕，大屏又浪费。
        private void PreviewPictureClicked(object sender, EventArgs e)
        {
            Image image = _previewPicture.Image;
            if (image == null)
            {
                return;
            }
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(1400, (int)(area.Width * 0.88));
            int height = Math.Min(980, (int)(area.Height * 0.88));
            using (Form form = new Form())
            {
                string title = _activeQuestion == null
                    ? SafeText(_previewCaption.Text)
                    : SafeText(_activeQuestion.Id) + "　" + SafeText(_activeQuestion.Title);
                form.Text = title + "　——　Esc 关闭";
                form.StartPosition = FormStartPosition.CenterScreen;
                form.Width = width;
                form.Height = height;
                form.MinimumSize = new Size(480, 360);
                form.KeyPreview = true;
                form.BackColor = Color.White;
                PictureBox box = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.White,
                    Image = image,
                    Cursor = Cursors.Hand
                };
                // 大图里再点一下也关，不用去找右上角的叉
                box.Click += delegate { form.Close(); };
                Label hint = new Label
                {
                    Dock = DockStyle.Bottom,
                    Height = 26,
                    Text = "点图或按 Esc 关闭　·　滚轮缩放留给 CAD 本体的缩放命令",
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(110, 110, 110),
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Regular)
                };
                form.Controls.Add(box);
                form.Controls.Add(hint);
                form.KeyDown += delegate(object keySender, KeyEventArgs keyEvent)
                {
                    if (keyEvent.KeyCode == Keys.Escape)
                    {
                        form.Close();
                    }
                };
                form.ShowDialog(this);
            }
        }

        private void RememberFinalSaveCheck(LearningStep step, CheckResult result)
        {
            if (!IsLastStep() || step == null || result == null || !result.Passed || result.RequiresManual
                || !string.Equals(SafeText(step.Action).Trim(), "save", StringComparison.OrdinalIgnoreCase)
                || step.CheckTypes == null || !step.CheckTypes.Exists(delegate(string type)
                {
                    return string.Equals(SafeText(type).Trim(), "saved", StringComparison.OrdinalIgnoreCase);
                }))
            {
                return;
            }
            QuestionProgress progress = GetQuestionProgress(true);
            if (progress != null)
            {
                progress.HasPassedFinalSaveCheck = true;
                RefreshProgress();
            }
        }

        private void RefreshProgress()
        {
            if (_activeQuestion == null)
            {
                _progressLabel.Text = "暂无题目";
                return;
            }

            int total = _activeQuestion.Steps == null ? 0 : _activeQuestion.Steps.Count;
            int number = total == 0 ? 0 : _currentStepIndex + 1;
            string documentText = IsCurrentDocumentActive() ? "已绑定当前图纸" : "未绑定当前图纸";
            QuestionProgress progress = GetQuestionProgress(false);
            // 一行能看完：行号 + 状态；会话历史只加一个短标记，完整说明写到命令行。
            // 未核实题不显示绑定状态：它本来就不做图纸校验，写“未绑定”只会让人以为出错。
            if (!IsWizardReady())
            {
                _progressLabel.Text = ShortUnit() + "第 " + number + "/" + total + " 步　·　题库指引（未验收）";
                return;
            }
            _progressLabel.Text = ShortUnit() + "第 " + number + "/" + total + " 步　·　" + documentText
                + (progress != null && progress.HasPassedFinalSaveCheck ? "　·　曾通过保存检查" : string.Empty);
        }

        // 题库按单元组织（“第一单元 文件操作”），下拉框里只显示题号，
        // 进度行补一个单元名，26 道题时才不会分不清当前在哪一章。
        private string ShortUnit()
        {
            string unit = SafeText(_activeQuestion == null ? null : _activeQuestion.Unit).Trim();
            if (unit.Length == 0)
            {
                return string.Empty;
            }
            int space = unit.IndexOf(' ');
            if (space <= 0)
            {
                return unit + "　·　";
            }
            return unit.Substring(0, space) + "　·　";
        }

        private void RefreshButtonState()
        {
            if (_disposeRequested || IsDisposed || Disposing)
            {
                return;
            }
            LearningStep step = GetCurrentStep();
            bool busy = IsCommandBusy();
            bool ready = IsWizardReady();
            bool available = !_startingStep && !_checking && !_dispatchingPrimary;
            bool canUsePrimary = step != null && available
                && (!ready || IsCurrentDocumentActive() || IsOpenAction(step));
            if (busy)
            {
                canUsePrimary = step != null && available;
            }
            _primaryButton.Enabled = canUsePrimary;

            bool lastStep = IsLastStep();
            bool canUseCheck = available && !busy && !_commandQueued
                && step != null && (ready ? IsCurrentDocumentActive() : !lastStep);
            _questionSelector.Enabled = available && !busy;
            _checkButton.Enabled = canUseCheck;
            // 未核实题的第二个按钮只负责“翻页”：读题、自查这类步骤写“下一步”更准确，
            // 需要动手的步骤才写“我做好了”，免得把“做完”当成通过验收。
            // 最后一步没有“下一步”可走，按钮直接变灰并改名，不能让用户点了没反应。
            if (!ready)
            {
                _checkButton.Text = lastStep
                    ? "已到最后一步"
                    : (step != null && IsManualAction(step) ? "下一步" : "我做好了，下一步");
            }
            else
            {
                _checkButton.Text = _finalReview ? "检查整题" : "检查";
            }
        }

        // 命令进行中由 Plugin 的空闲回调调用：只摆提示、刷按钮，绝不读数据库。
        public void RefreshBusyNotice()
        {
            if (DeferIfRequired(RefreshBusyNotice))
            {
                return;
            }
            if (_disposeRequested || IsDisposed || Disposing)
            {
                return;
            }
            bool busy = IsCommandBusy();
            RefreshButtonState();
            if (busy)
            {
                if (!_busyNoticeShown)
                {
                    _busyNoticeShown = true;
                    SetStatus("CAD 正在执行命令。不会取消或重复输入；卡住时仍可点同一按钮查看当前帮助。"
                        + Environment.NewLine + BuildCoachText(GetCurrentStep()));
                }
            }
            else if (_busyNoticeShown)
            {
                _busyNoticeShown = false;
                // 忙转闲本身不是成功事件；只有相关命令正常结束才能排队检查。
                RefreshButtonState();
            }
        }

        private static bool IsCommandBusy()
        {
            // 统一由 Plugin.IsCommandRunning 判定，理由见那里的注释（不能用 IsQuiescent）。
            try
            {
                return Plugin.IsCommandRunning(CadApplication.DocumentManager.MdiActiveDocument);
            }
            catch (Exception)
            {
                // 状态不可读时只允许求助，不允许误发或误查。
                return true;
            }
        }

        private void RestoreCurrentQuestionProgress()
        {
            _currentStepIndex = 0;
            if (_currentDocument == null || _activeQuestion == null)
            {
                return;
            }

            QuestionProgress progress = GetQuestionProgress(true);
            _currentStepIndex = ClampStepIndex(progress.CurrentStepIndex);
            progress.CurrentStepIndex = _currentStepIndex;
        }

        private void NotifyPluginOfCurrentSelection()
        {
            if (_activeQuestion == null)
            {
                return;
            }

            try
            {
                Plugin.SelectQuestion(_activeQuestion.Id);
                LearningStep step = GetCurrentStep();
                if (step != null)
                {
                    Plugin.SelectStep(_activeQuestion.Id, step);
                }
            }
            catch (Exception exception)
            {
                ShowDiagnostic("同步当前步骤出错：" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private QuestionProgress GetQuestionProgress(bool create)
        {
            if (_activeQuestion == null || _currentDocument == null)
            {
                return null;
            }

            Dictionary<string, QuestionProgress> questionProgress;
            if (!_documentProgress.TryGetValue(_currentDocument, out questionProgress))
            {
                if (!create)
                {
                    return null;
                }
                questionProgress = new Dictionary<string, QuestionProgress>(StringComparer.OrdinalIgnoreCase);
                _documentProgress.Add(_currentDocument, questionProgress);
            }

            string questionId = SafeText(_activeQuestion.Id);
            QuestionProgress progress;
            if (!questionProgress.TryGetValue(questionId, out progress) && create)
            {
                progress = new QuestionProgress();
                questionProgress.Add(questionId, progress);
            }
            return progress;
        }

        private void SaveCurrentStepIndex()
        {
            QuestionProgress progress = GetQuestionProgress(true);
            if (progress != null)
            {
                progress.CurrentStepIndex = _currentStepIndex;
            }
        }

        private LearningStep GetCurrentStep()
        {
            if (_activeQuestion == null || _activeQuestion.Steps == null || _activeQuestion.Steps.Count == 0)
            {
                return null;
            }
            return _activeQuestion.Steps[ClampStepIndex(_currentStepIndex)];
        }

        private int ClampStepIndex(int index)
        {
            if (_activeQuestion == null || _activeQuestion.Steps == null || _activeQuestion.Steps.Count == 0)
            {
                return 0;
            }
            if (index < 0)
            {
                return 0;
            }
            if (index >= _activeQuestion.Steps.Count)
            {
                return _activeQuestion.Steps.Count - 1;
            }
            return index;
        }

        private bool IsLastStep()
        {
            return _activeQuestion != null && _activeQuestion.Steps != null
                && _currentStepIndex >= _activeQuestion.Steps.Count - 1;
        }

        private bool IsWizardReady()
        {
            return _activeQuestion != null && _activeQuestion.WizardReady;
        }

        private bool IsOpenAction(LearningStep step)
        {
            return step != null && string.Equals(SafeText(step.Action).Trim(), "open", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsManualAction(LearningStep step)
        {
            return step != null && string.Equals(SafeText(step.Action).Trim(), "manual", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCurrentDocumentActive()
        {
            return _currentDocument != null
                && object.ReferenceEquals(_currentDocument, CadApplication.DocumentManager.MdiActiveDocument);
        }

        private bool IsCheckContextCurrent(Document document, LearningStep step)
        {
            return object.ReferenceEquals(_currentDocument, document) && IsCurrentDocumentActive()
                && object.ReferenceEquals(GetCurrentStep(), step);
        }

        private static bool IsDocumentIdle(Document document)
        {
            return document != null && !Plugin.IsCommandRunning(document);
        }

        private bool CanChangeWizardSelection()
        {
            if (_disposeRequested || IsDisposed || Disposing || _startingStep || _checking || _dispatchingPrimary || _commandQueued)
            {
                return false;
            }
            Document document = CadApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return true;
            }
            // 与按钮、与插件共用同一个口径（IsCommandRunning 内部已放行 CADLEARN*/NETLOAD*），
            // 别再这里另写一套，否则又会出现“按钮能点、点了才报错”。
            if (!IsCommandBusy())
            {
                return true;
            }
            SetStatus("CAD 命令尚未结束，不能回看或切换题目、步骤。请完成当前操作，或按 Esc 两次后重试。");
            return false;
        }

        private bool CanUseWizard(LearningStep step)
        {
            return !_startingStep && !_checking && _activeQuestion != null && step != null && IsWizardReady()
                && (IsCurrentDocumentActive() || IsOpenAction(step));
        }

        private string CannotStartText(LearningStep step)
        {
            if (!IsWizardReady())
            {
                return "本题待核实，只能预览当前动作，不能启动或检查。";
            }
            if (!IsCurrentDocumentActive() && !IsOpenAction(step))
            {
                return "绑定图纸不是当前活动图纸。本步不是 open，请先切回并绑定对应练习图纸。";
            }
            return "当前步骤还不能启动。";
        }

        private static string FormatCommandText(string value)
        {
            string text = SafeText(value).Trim();
            return text.Length == 0 ? "本步没有已核实的命令文本，请对照当前动作查看。" : text;
        }

        // “这步怎么做”只放三行，一眼能扫完：
        //   ① 点　该点哪个面板/菜单
        //   ② 敲　该在命令行敲什么（回车写成 ↵）
        //   ③ 记　最容易错的那一句（只取第一句，长文进命令行）
        // 题库原文很长，全塞进面板就变成了“读论文”；完整内容点【我不会，帮我继续】时
        // 会写进 CAD 命令行，需要细看时按 Ctrl+9。
        private static string BuildInputText(LearningStep step)
        {
            if (step == null)
            {
                return string.Empty;
            }
            string panel = SafeText(step.PanelPath).Trim();
            string command = SafeText(step.Command).Trim();
            string tip = SafeText(step.Tip).Trim();
            if (panel.Length == 0 && command.Length == 0 && tip.Length == 0)
            {
                return BuildScriptText(step);
            }
            List<string> lines = new List<string>();
            if (panel.Length > 0)
            {
                lines.Add("① 点　" + ShortSegment(panel, 38));
            }
            if (command.Length > 0)
            {
                lines.Add((panel.Length > 0 ? "② 敲　" : "① 敲　") + CompactCommand(command));
            }
            if (tip.Length > 0)
            {
                // 只有一段题面（题目要求、做完后自查）时不加编号，整段照原样显示。
                bool onlyTip = panel.Length == 0 && command.Length == 0;
                lines.Add(onlyTip ? tip : (panel.Length > 0 ? "③ 记　" : "② 记　") + FirstSentence(tip, 42));
            }
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        // 面板路径里的括注（“Ribbon 无独立按钮”“或双击多线”）对“点哪里”没有帮助，
        // 去掉后一条路径通常能压到 20 字以内。
        private static string ShortSegment(string value, int max)
        {
            string text = SafeText(value).Trim();
            text = System.Text.RegularExpressions.Regex.Replace(text, "（[^（）]*）", string.Empty);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\([^()]*\\)", string.Empty);
            // 题库原文常写“应用程序菜单→ 图形实用工具”，箭头前后空格不统一，统一成 “ → ”。
            text = text.Replace("→", " → ");
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ");
            text = text.Trim(' ', '·', '，', '。', '；', '/');
            if (text.Length > max)
            {
                text = text.Substring(0, max) + "…";
            }
            return text;
        }

        // 命令行里“回车”两个字占两格，换成一字符的 ↵ 后一句话就能看完；
        // 命令本身不截断——少一个参数就会画错。
        private static string CompactCommand(string value)
        {
            string text = SafeText(value).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            text = text.Replace(" 回车", "↵").Replace("回车", "↵");
            text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
            return text;
        }

        private static string FirstSentence(string value, int max)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(SafeText(value), "\\s+", " ").Trim();
            // 题库的要点常以“① 定基准：…”开头；面板前面已经有“③ 记”，再套一层编号会看乱。
            text = System.Text.RegularExpressions.Regex.Replace(text, "^[①②③④⑤⑥⑦⑧⑨⑩⑪⑫]+[\\s、.．]*", string.Empty);
            int stop = text.IndexOfAny(new char[] { '。', '；', ';' });
            if (stop > 8)
            {
                text = text.Substring(0, stop);
            }
            if (text.Length > max)
            {
                text = text.Substring(0, max) + "…";
            }
            return text;
        }

        // 老题（3.1 等）没有 PanelPath/Tip，只有人的动作描述 + 自动脚本：
        // 这种情况下把“按钮会自动输入什么”写在动作下面，仍然保留。
        private static string BuildScriptText(LearningStep step)
        {
            string body = FormatCommandText(step.Command);
            string script = SafeText(step.CommandScript);
            if (script.Trim().Length == 0)
            {
                return body;
            }

            string[] parts = script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
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
                return body;
            }
            if (script.EndsWith("\n\n", StringComparison.Ordinal))
            {
                tokens.Add("回车");
            }
            return body + Environment.NewLine
                + "点【我不会，帮我继续】：助手自动依次输入 " + string.Join(" / ", tokens.ToArray()) + "。";
        }

        private static string FormatInputText(string value)
        {
            string text = SafeText(value).Trim();
            return text.Length == 0 ? "本步不要求键入文字。" : text;
        }

        private static string BuildScreenHint(LearningStep step)
        {
            if (step == null)
            {
                return string.Empty;
            }
            string text = SafeText(step.ScreenHint).Trim();
            if (string.Equals(SafeText(step.Action).Trim(), "click", StringComparison.OrdinalIgnoreCase))
            {
                return "封闭区域的内部点击点尚未核实；没有精确定位箭头，请对照题图确认，不要把对象位置当成点击点。";
            }
            // 题库题的面板路径已经在“操作细节”里显示过，这里没有补充就不重复，
            // 让 BuildCoachText 整行省掉，免得命令行每次刷一屏“没有定位信息”。
            return text;
        }

        // 命令行里的补充说明只写面板上没有的东西（屏幕位置、注意事项）。
        // 空项整行跳过：题库题这两项本来就是空的，硬拼“注意：”只会变成噪音。
        private static string BuildCoachText(LearningStep step)
        {
            if (step == null)
            {
                return "当前没有可用步骤。";
            }
            List<string> lines = new List<string>();
            string screen = BuildScreenHint(step);
            if (screen.Length > 0)
            {
                lines.Add("屏幕位置：" + screen);
            }
            string hint = SafeText(step.Hint).Trim();
            if (hint.Length > 0)
            {
                lines.Add("注意：" + hint);
            }
            // 面板上的“③ 记”只留了第一句，完整要点写到这里，需要细看时按 Ctrl+9。
            string fullTip = SafeText(step.Tip).Trim();
            if (fullTip.Length > 60)
            {
                lines.Add("完整要点：" + fullTip);
            }
            lines.Add("命令行被关掉时按 Ctrl+9 打开；不确定状态时可再点【我不会，帮我继续】。");
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private void ShowCheckResult(CheckResult result)
        {
            if (result == null)
            {
                ShowDiagnostic("检查器没有返回结果。");
                return;
            }
            SetStatus(BuildResultText(result));
        }

        private static string BuildResultText(CheckResult result)
        {
            if (result == null)
            {
                return "检查器没有返回结果。";
            }
            string details = result.Details == null ? string.Empty : string.Join(Environment.NewLine, result.Details.ToArray());
            string summary = SafeText(result.Summary);
            return summary.Length == 0 ? details : summary + (details.Length == 0 ? string.Empty : Environment.NewLine + details);
        }

        // 面板不再有“现在怎么做”文本框：结果、诊断、解释一律写到 CAD 命令行，
        // 这样不占版面，也不会出现“点了按钮却没有反馈”。同一句话不重复刷。
        private void SetStatus(string text)
        {
            WriteStatus(text);
        }

        private void WriteStatus(string text)
        {
            string value = SafeText(text).Trim();
            if (value.Length == 0 || string.Equals(value, _lastStatus, StringComparison.Ordinal))
            {
                return;
            }
            _lastStatus = value;
            Plugin.WritePanelStatus(value);
        }

        private void ClearStatus()
        {
            _lastStatus = null;
        }

        private void ShowDiagnostic(string text)
        {
            WriteStatus("诊断：" + SafeText(text));
        }

        private static void SelectBeginning(TextBox textBox)
        {
            textBox.SelectionStart = 0;
            textBox.SelectionLength = 0;
        }

        private void SetButtonsEnabled(bool enabled)
        {
            _primaryButton.Enabled = enabled;
            _checkButton.Enabled = false;
            _questionSelector.Enabled = enabled;
        }

        public void SelectById(string questionId)
        {
            if (DeferIfRequired(delegate { SelectById(questionId); }) || !CanChangeWizardSelection())
            {
                return;
            }
            for (int index = 0; index < _questionBank.Questions.Count; index++)
            {
                if (!string.Equals(_questionBank.Questions[index].Id, questionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (_questionSelector.SelectedIndex == index)
                {
                    QuestionChanged(this, EventArgs.Empty);
                }
                else
                {
                    _questionSelector.SelectedIndex = index;
                }
                return;
            }
        }

        public void SelectStepByOrder(int order)
        {
            if (DeferIfRequired(delegate { SelectStepByOrder(order); }) || !CanChangeWizardSelection())
            {
                return;
            }
            if (_activeQuestion == null || _activeQuestion.Steps == null)
            {
                return;
            }
            for (int index = 0; index < _activeQuestion.Steps.Count; index++)
            {
                if (_activeQuestion.Steps[index].Order != order)
                {
                    continue;
                }
                InvalidateWizardCheck();
                _currentStepIndex = index;
                SaveCurrentStepIndex();
                ResetCurrentStepInteraction();
                ClearStatus();
                RefreshView();
                NotifyPluginOfCurrentSelection();
                return;
            }
        }

        public void UpdateCheckResult(CheckResult result)
        {
            int version = System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0);
            ApplyCheckResult(result, version);
        }

        private void ApplyCheckResult(CheckResult result, int version)
        {
            if (DeferIfRequired(delegate { ApplyCheckResult(result, version); })
                || version != System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0)
                || result == null)
            {
                return;
            }
            ShowCheckResult(result);
        }

        public void NotifyCommandInterrupted(Document document, string commandName, string reason)
        {
            int version = System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0);
            ApplyCommandInterrupted(document, commandName, reason, version);
        }

        private void ApplyCommandInterrupted(Document document, string commandName, string reason, int version)
        {
            if (DeferIfRequired(delegate { ApplyCommandInterrupted(document, commandName, reason, version); }))
            {
                return;
            }
            if (!object.ReferenceEquals(document, _currentDocument) || _disposeRequested
                || version != System.Threading.Interlocked.CompareExchange(ref _wizardContextVersion, 0, 0)
                || (!_commandQueued && !_relevantCommandStarted && !IsRelevantCommand(commandName)))
            {
                return;
            }
            InvalidateWizardCheck();
            _inputDeliveryUncertain = false;
            _autoCheckSuspended = true;
            _relevantCommandStarted = false;
            _busyNoticeShown = false;
            _stepStarted = true;
            SetStatus("命令已中断或失败：" + SafeText(reason)
                + Environment.NewLine + "已保留当前步骤和已启动状态；不会自动推进。处理后再次点【我不会，帮我继续】可重试。"
                + Environment.NewLine + BuildCoachText(GetCurrentStep()));
            RefreshButtonState();
        }

        protected override void Dispose(bool disposing)
        {
            Plugin.ClearMenuPointer();
            _disposeRequested = true;
            System.Threading.Interlocked.Increment(ref _documentVersion);
            InvalidateWizardCheck();
            if (disposing)
            {
                _documentProgress.Clear();
                if (_largeButtonFont != null)
                {
                    _largeButtonFont.Dispose();
                }
                if (_normalFont != null)
                {
                    _normalFont.Dispose();
                }
                foreach (Image image in _imageCache.Values)
                {
                    if (image != null)
                    {
                        image.Dispose();
                    }
                }
                _imageCache.Clear();
            }
            base.Dispose(disposing);
        }

        private static string SafeText(string value)
        {
            return value ?? string.Empty;
        }

        private sealed class CadWindowHandle : IWin32Window
        {
            private readonly IntPtr _handle;

            public CadWindowHandle(IntPtr handle)
            {
                _handle = handle;
            }

            public IntPtr Handle
            {
                get { return _handle; }
            }
        }

    }
}
