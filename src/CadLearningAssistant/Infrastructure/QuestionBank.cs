using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using CadLearningAssistant;

namespace CadLearningAssistant.Infrastructure
{
    public class QuestionBank
    {
        private readonly List<QuestionDefinition> _questions;
        private readonly string _sourcePath;
        private readonly string _loadError;

        private QuestionBank(List<QuestionDefinition> questions, string sourcePath, string loadError)
        {
            _questions = questions ?? new List<QuestionDefinition>();
            _sourcePath = sourcePath;
            _loadError = loadError;
        }

        public IList<QuestionDefinition> Questions
        {
            get { return _questions.AsReadOnly(); }
        }

        public string SourcePath
        {
            get { return _sourcePath; }
        }

        public string LoadError
        {
            get { return _loadError; }
        }

        public static QuestionBank LoadFromPluginDirectory()
        {
            string primaryPath = string.Empty;
            string fallbackPath = string.Empty;
            string path = string.Empty;
            try
            {
                string pluginDirectory = Path.GetDirectoryName(typeof(QuestionBank).Assembly.Location);
                primaryPath = Path.Combine(pluginDirectory ?? string.Empty, "questions.json");
                fallbackPath = Path.Combine(pluginDirectory ?? string.Empty, "content", "questions.json");
                path = primaryPath;

                FileStream stream;
                try
                {
                    stream = File.OpenRead(path);
                }
                catch (FileNotFoundException)
                {
                    path = fallbackPath;
                    stream = File.OpenRead(path);
                }
                catch (DirectoryNotFoundException)
                {
                    path = fallbackPath;
                    stream = File.OpenRead(path);
                }

                using (stream)
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(QuestionBankFile));
                    QuestionBankFile file = serializer.ReadObject(stream) as QuestionBankFile;
                    string validationError = Validate(file);
                    if (validationError != null)
                    {
                        return new QuestionBank(null, path, "题库校验失败，已拒载全库。来源：" + path + "。" + validationError);
                    }
                    return new QuestionBank(file.Questions, path, null);
                }
            }
            catch (Exception exception)
            {
                // 加载失败不阻止 AutoCAD 加载插件，保留路径、异常类型及详情供调用方排查。
                string error = "题库加载失败，已返回空题库。当前来源：" + path
                    + "。DLL 同目录路径：" + primaryPath
                    + "。content 后备路径：" + fallbackPath
                    + "。异常：" + exception.ToString();
                return new QuestionBank(null, path, error);
            }
        }

        private static string Validate(QuestionBankFile file)
        {
            if (file == null)
            {
                return "题库根对象不能为 null。";
            }
            if (!string.Equals(file.SchemaVersion, "1.0", StringComparison.Ordinal))
            {
                return "SchemaVersion 只能为 1.0，实际值：" + (file.SchemaVersion ?? "<null>") + "。";
            }
            if (file.Questions == null || file.Questions.Count == 0)
            {
                return "Questions 不能为空或空列表。";
            }

            Dictionary<string, int> questionIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int questionIndex = 0; questionIndex < file.Questions.Count; questionIndex++)
            {
                QuestionDefinition question = file.Questions[questionIndex];
                string location = "Questions[" + questionIndex + "]（第 " + (questionIndex + 1) + " 项）";
                if (question == null)
                {
                    return location + "不能为 null。";
                }
                if (string.IsNullOrWhiteSpace(question.Id))
                {
                    return location + ".Id 不能为空或仅含空白。";
                }
                location += "，题号“" + question.Id + "”";
                string normalizedId = NormalizeId(question.Id);
                int previousQuestionIndex;
                if (questionIds.TryGetValue(normalizedId, out previousQuestionIndex))
                {
                    return location + ".Id 在移除空白并忽略大小写后与 Questions[" + previousQuestionIndex + "].Id 重复。";
                }
                questionIds.Add(normalizedId, questionIndex);
                question.Id = normalizedId;
                if (string.IsNullOrWhiteSpace(question.Title))
                {
                    return location + ".Title 不能为空或仅含空白。";
                }
                if (question.Steps == null || question.Steps.Count == 0)
                {
                    return location + ".Steps 不能为空或空列表。";
                }

                Dictionary<int, int> stepOrders = new Dictionary<int, int>();
                for (int stepIndex = 0; stepIndex < question.Steps.Count; stepIndex++)
                {
                    LearningStep step = question.Steps[stepIndex];
                    string stepLocation = location + ".Steps[" + stepIndex + "]（第 " + (stepIndex + 1) + " 个步骤）";
                    if (step == null)
                    {
                        return stepLocation + "不能为 null。";
                    }
                    if (step.Order <= 0)
                    {
                        return stepLocation + ".Order 必须为正整数，实际值：" + step.Order + "。";
                    }
                    int previousStepIndex;
                    if (stepOrders.TryGetValue(step.Order, out previousStepIndex))
                    {
                        return stepLocation + ".Order（" + step.Order + "）与 Steps[" + previousStepIndex + "].Order 重复。";
                    }
                    stepOrders.Add(step.Order, stepIndex);
                    if (string.IsNullOrWhiteSpace(step.Title))
                    {
                        return stepLocation + ".Title 不能为空或仅含空白。";
                    }
                    if (string.IsNullOrWhiteSpace(step.Instruction))
                    {
                        return stepLocation + ".Instruction 不能为空或仅含空白。";
                    }
                    if (step.CheckTypes == null)
                    {
                        step.CheckTypes = new List<string>();
                    }
                    for (int checkTypeIndex = 0; checkTypeIndex < step.CheckTypes.Count; checkTypeIndex++)
                    {
                        if (string.IsNullOrWhiteSpace(step.CheckTypes[checkTypeIndex]))
                        {
                            return stepLocation + ".CheckTypes[" + checkTypeIndex + "] 不能为空或仅含空白。";
                        }
                    }
                    string[] menuParts;
                    if (!TryParseMenuPath(step.MenuPath, out menuParts))
                    {
                        return stepLocation + ".MenuPath 必须为空，或为以 > 分隔的菜单显示文字；各段不能为空，不能含控制字符。";
                    }
                    string automationError = ValidateAutomation(question, step, stepLocation);
                    if (automationError != null)
                    {
                        return automationError;
                    }
                    if (question.WizardReady && string.IsNullOrWhiteSpace(step.Action))
                    {
                        return stepLocation + ".Action 在 WizardReady=true 的题目中不能为空。";
                    }
                }

                if (question.Checks == null)
                {
                    question.Checks = new List<CheckRule>();
                }
                for (int checkIndex = 0; checkIndex < question.Checks.Count; checkIndex++)
                {
                    if (question.Checks[checkIndex] == null)
                    {
                        return location + ".Checks[" + checkIndex + "]（第 " + (checkIndex + 1) + " 个检查项）不能为 null。";
                    }
                }
                if (question.WizardReady)
                {
                    string wizardError = ValidateWizard(question, location);
                    if (wizardError != null)
                    {
                        return wizardError;
                    }
                }
                if (question.KnowledgePoints == null)
                {
                    question.KnowledgePoints = new List<string>();
                }
            }
            return null;
        }

        // 所有题目均检查菜单格式和自动化安全；只有显式就绪的题目启用完整向导契约。
        private static string ValidateWizard(QuestionDefinition question, string location)
        {
            HashSet<string> actions = new HashSet<string>(StringComparer.Ordinal)
            {
                "open", "command", "input", "click", "select", "save", "manual"
            };
            HashSet<string> checkTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "practice", "layer-exists", "layer-red", "current-layer", "hpname", "hpscale",
                "hpang", "hplayer", "cecolor", "hatch-on-layer", "saved", "layer", "entity",
                "pattern", "color", "scale", "block", "placement", "manual"
            };
            HashSet<string> ruleTypes = new HashSet<string>(StringComparer.Ordinal)
            {
                "layer", "entity", "pattern", "color", "scale", "block", "placement"
            };
            HashSet<string> configuredTypes = new HashSet<string>(StringComparer.Ordinal);
            for (int checkIndex = 0; checkIndex < question.Checks.Count; checkIndex++)
            {
                CheckRule rule = question.Checks[checkIndex];
                string type = (rule.Type ?? string.Empty).Trim().ToLowerInvariant();
                if (!ruleTypes.Contains(type) && type != "manual")
                {
                    return location + ".Checks[" + checkIndex + "].Type 不在整题规则白名单中；系统变量状态只能用于步骤 CheckTypes。";
                }
                configuredTypes.Add(type);
                // 不在加载时限定 Target；未知目标及 Expected 的实际语义由检查器保守处理。
            }

            if (string.IsNullOrWhiteSpace(question.SourceFile) || string.IsNullOrWhiteSpace(question.ExpectedOutput)
                || !string.Equals(Path.GetFileName(question.ExpectedOutput), question.ExpectedOutput, StringComparison.Ordinal)
                || question.ExpectedOutput.IndexOf(':') >= 0)
            {
                return location + "：向导必须配置 SourceFile 和仅含文件名的 ExpectedOutput。";
            }
            if (!string.Equals(question.Steps[0].Action, "open", StringComparison.Ordinal))
            {
                return location + ".Steps[0].Action 必须为 open，题目须从准备练习副本开始。";
            }
            for (int stepIndex = 0; stepIndex < question.Steps.Count; stepIndex++)
            {
                LearningStep step = question.Steps[stepIndex];
                string stepLocation = location + ".Steps[" + stepIndex + "]";
                if (step.Order != stepIndex + 1)
                {
                    return stepLocation + ".Order 必须按列表顺序从 1 连续递增，期望值：" + (stepIndex + 1) + "。";
                }
                if (!actions.Contains(step.Action))
                {
                    return stepLocation + ".Action 不在白名单 open/command/input/click/select/save/manual 中。";
                }
                if (step.Action != "open" && string.IsNullOrWhiteSpace(step.InputText))
                {
                    return stepLocation + ".InputText 不能为空；仅 open 步骤允许为空。";
                }
                if (string.IsNullOrWhiteSpace(step.ScreenHint))
                {
                    return stepLocation + ".ScreenHint 不能为空或仅含空白。";
                }
                if (string.IsNullOrWhiteSpace(step.Hint))
                {
                    return stepLocation + ".Hint 不能为空或仅含空白。";
                }
                if (step.CheckTypes.Count == 0)
                {
                    return stepLocation + ".CheckTypes 至少需要一个检查类型，无规则不能 AutoAdvance。";
                }
                if (step.Action == "manual" || step.CheckTypes.Contains("manual"))
                {
                    return stepLocation + "：就绪向导暂不支持人工确认推进，请将本题 WizardReady 设为 false，不能让用户卡在人工步骤。";
                }
                for (int checkTypeIndex = 0; checkTypeIndex < step.CheckTypes.Count; checkTypeIndex++)
                {
                    string type = step.CheckTypes[checkTypeIndex].Trim().ToLowerInvariant();
                    string typeLocation = stepLocation + ".CheckTypes[" + checkTypeIndex + "]";
                    if (!checkTypes.Contains(type))
                    {
                        return typeLocation + " 不在向导检查类型白名单中。";
                    }
                    if (type == "practice" && (step.CheckTypes.Count != 1 || step.Action != "open"))
                    {
                        return typeLocation + "：practice 只能独立声明且必须对应 open 动作。";
                    }
                    if (type == "manual")
                    {
                        return stepLocation + "：人工确认步骤暂不可标记为就绪向导，请设 WizardReady=false。";
                    }
                    if (ruleTypes.Contains(type) && !configuredTypes.Contains(type))
                    {
                        return typeLocation + "：选规则类型“" + type + "”必须在 question.Checks 中有同类型规则，不能无规则自动前进。";
                    }
                }
            }
            return null;
        }

        // 与运行时定位共用格式解析；格式合法不代表菜单路径已经在当前工作区核实。
        internal static bool TryParseMenuPath(string menuPath, out string[] parts)
        {
            parts = new string[0];
            if (string.IsNullOrEmpty(menuPath))
            {
                return true;
            }
            foreach (char character in menuPath)
            {
                if (char.IsControl(character))
                {
                    return false;
                }
            }
            string[] segments = menuPath.Split(new char[] { '>' }, StringSplitOptions.None);
            for (int index = 0; index < segments.Length; index++)
            {
                segments[index] = segments[index].Trim();
                if (segments[index].Length == 0)
                {
                    return false;
                }
            }
            parts = segments;
            return true;
        }

        private static string ValidateAutomation(QuestionDefinition question, LearningStep step, string location)
        {
            if (step.AutoPickPoint)
            {
                return location + ".AutoPickPoint 不能为 true；填充区域须由用户核实并手工点选，禁止自动猜测内部点。";
            }
            string script = step.CommandScript;
            if (string.IsNullOrEmpty(script))
            {
                return null;
            }
            // 与 Plugin.RunStepScript 保持逐字一致，只统一 CRLF，不放宽参数或追加输入。
            script = script.Replace("\r\n", "\n");
            string expected;
            if (step.Action == "click" || step.Action == "select" || step.Action == "save")
            {
                expected = step.Action == "click" ? "_.-HATCH\n"
                    : step.Action == "select" ? "_.CHPROP\n" : "_.SAVEAS\n";
                return string.Equals(script, expected, StringComparison.Ordinal) ? null
                    : location + ".CommandScript 只能启动本动作的固定命令，不能包含点坐标、对象选择、保存路径或后续应答。";
            }
            Dictionary<string, string> allowed = new Dictionary<string, string>(StringComparer.Ordinal)
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
            string key = step.CheckTypes != null && step.CheckTypes.Count == 1 ? step.CheckTypes[0] : string.Empty;
            if ((step.Action != "command" && step.Action != "input")
                || !string.Equals(question.Id, "3.1", StringComparison.Ordinal)
                || !allowed.TryGetValue(key, out expected)
                || !string.Equals(script, expected, StringComparison.Ordinal))
            {
                return location + ".CommandScript 不符合 Plugin.RunStepScript 已核实的题号、动作、单一检查类型和脚本白名单。";
            }
            return null;
        }

        private static string NormalizeId(string id)
        {
            if (id == null)
            {
                return null;
            }
            StringBuilder normalized = new StringBuilder(id.Length);
            foreach (char character in id)
            {
                if (!char.IsWhiteSpace(character))
                {
                    normalized.Append(character);
                }
            }
            return normalized.ToString();
        }

        public QuestionDefinition Find(string id)
        {
            string normalizedId = NormalizeId(id);
            foreach (QuestionDefinition question in _questions)
            {
                if (string.Equals(NormalizeId(question.Id), normalizedId, StringComparison.OrdinalIgnoreCase))
                {
                    return question;
                }
            }
            return null;
        }
    }
}
