using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
// IntegerCollection 位于 Autodesk.AutoCAD.Geometry（acdbmgd），不在 GraphicsInterface 命名空间。
using IntegerCollection = Autodesk.AutoCAD.Geometry.IntegerCollection;
using TransientManager = Autodesk.AutoCAD.GraphicsInterface.TransientManager;
using TransientDrawingMode = Autodesk.AutoCAD.GraphicsInterface.TransientDrawingMode;
using CadLearningAssistant.Infrastructure;

namespace CadLearningAssistant.Services
{
    public class AutoCadChecker
    {
        private string _activeQuestionId = "3.1";
        private const int MaxScannedObjects = 10000;
        private const int MaxHighlightedObjects = 200;
        private const int MaxGuideArrows = 3;
        private readonly List<ObjectId> _highlightedIds = new List<ObjectId>();
        // 临时提示图形（箭头线、点选标记圆）统一放这里：都只是屏幕上的临时图元，
        // 不进数据库、不改变图形，清除时逐条 EraseTransient。
        private readonly List<Entity> _guideEntities = new List<Entity>();
        private Document _highlightDocument;
        private LearningStep _activeStep;

        public string ActiveQuestionId
        {
            get { return _activeQuestionId; }
        }

        public int? ActiveStepOrder
        {
            get { return _activeStep == null ? (int?)null : _activeStep.Order; }
        }

        public LearningStep ActiveStep
        {
            get { return _activeStep; }
        }

        public void SetActiveQuestion(string questionId)
        {
            if (!string.Equals(_activeQuestionId, questionId, StringComparison.Ordinal))
            {
                _activeStep = null;
            }
            _activeQuestionId = questionId;
        }

        public void SetActiveStep(LearningStep step)
        {
            if (step != null)
            {
                ValidateHintTarget(step.HintTarget);
            }
            _activeStep = step;
        }

        public CheckResult CheckActiveQuestion(Document document)
        {
            if (document == null)
            {
                return Failure("没有活动图纸");
            }

            switch (_activeQuestionId)
            {
                case "3.1":
                    return CheckHatchQuestion(document);
                case "3.18":
                    return CheckBlockQuestion(document);
                case "6.1":
                    return CheckDimensionLayer(document);
                default:
                    return Failure("题目 " + _activeQuestionId + " 暂无自动检查规则，已保留手动检查入口");
            }
        }

        // 第三版入口：检查规则来自题库 JSON，新增题目无需为每道题增加 switch 分支。
        // 保留上面的无参数重载，兼容旧命令和外部调用方；没有题库定义时仍走旧版兜底。
        public CheckResult CheckActiveQuestion(Document document, QuestionDefinition question)
        {
            if (document == null)
            {
                return Failure("没有活动图纸");
            }
            if (question == null || question.Checks == null || question.Checks.Count == 0)
            {
                return CheckActiveQuestion(document);
            }
            return CheckQuestionRules(document, question);
        }

        // 用于“下一步”按钮：只检查当前步骤声明的规则，避免把整道题的后续要求提前算成失败。
        public CheckResult CheckActiveStep(Document document, QuestionDefinition question, LearningStep step)
        {
            if (document == null)
            {
                return Failure("没有活动图纸");
            }
            if (question == null || step == null)
            {
                return Failure("没有选中可检查的学习步骤");
            }
            if (step.CheckTypes == null || step.CheckTypes.Count == 0)
            {
                return Failure("本步未配置 CheckTypes，不能把 overview 或打开图纸当作自动通过；请配置专用状态类型或由人工确认。");
            }

            RuleState state = null;
            CheckResult result = new CheckResult();
            bool allPassed = true;
            int autoChecks = 0;
            int failedChecks = 0;
            int manualChecks = 0;
            foreach (string declaredType in step.CheckTypes)
            {
                string type = (declaredType ?? string.Empty).Trim().ToLowerInvariant();
                bool passed;
                string detail;
                if (type == "manual")
                {
                    manualChecks++;
                    detail = "人工检查（未自动判定）：请按步骤说明确认后继续。";
                    result.Details.Add(detail);
                    continue;
                }
                if (type == "practice")
                {
                    autoChecks++;
                    failedChecks++;
                    allPassed = false;
                    result.Details.Add("practice 类型由 Plugin 负责绑定练习副本；checker 不假装确认该步骤。");
                    continue;
                }

                autoChecks++;
                try
                {
                    if (IsStepStateType(type))
                    {
                        EvaluateStepState(type, document, question, out passed, out detail);
                    }
                    else if (IsQuestionRuleType(type))
                    {
                        bool matched = false;
                        passed = true;
                        detail = string.Empty;
                        if (question.Checks != null)
                        {
                            foreach (CheckRule rule in question.Checks)
                            {
                                if (rule == null || !string.Equals((rule.Type ?? string.Empty).Trim(), type, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                matched = true;
                                if (state == null)
                                {
                                    state = CollectRuleState(document);
                                }
                                bool rulePassed;
                                string ruleDetail;
                                EvaluateRule(rule, state, out rulePassed, out ruleDetail);
                                passed = passed && rulePassed;
                                result.Details.Add(type + "：" + ruleDetail);
                            }
                        }
                        passed = matched && passed;
                        detail = matched ? type + " 类型的全部已配置规则已检查。"
                            : "步骤类型“" + type + "”未在 question.Checks 配置规则，检查失败。";
                    }
                    else
                    {
                        passed = false;
                        detail = "未知的步骤检查类型“" + (declaredType ?? "<空>") + "”，检查失败。";
                    }
                }
                catch (System.Exception exception)
                {
                    // 单项失败不截断后续类型，避免多类型步骤漏检。
                    passed = false;
                    detail = type + " 检查失败：" + exception.GetType().Name + "：" + exception.Message;
                }
                result.Details.Add(detail);
                if (!passed)
                {
                    failedChecks++;
                    allPassed = false;
                }
            }

            if (state != null && state.ScanLimited)
            {
                allPassed = false;
                result.Details.Add("模型空间对象超过 " + MaxScannedObjects + " 个扫描上限，统计不完整，已保守判定失败。");
            }
            result.Passed = autoChecks > 0 && allPassed && manualChecks == 0;
            result.RequiresManual = manualChecks > 0;
            result.Summary = autoChecks == 0
                ? "第" + step.Order + "步需人工确认，未自动判定"
                : (result.Passed ? "第" + step.Order + "步状态检查通过"
                    : "第" + step.Order + "步未获自动通过：" + failedChecks + "/" + autoChecks + " 条自动检查未满足");
            if (manualChecks > 0)
            {
                result.Summary += "，另有 " + manualChecks + " 条需人工确认";
            }
            return result;
        }

        private bool IsStepStateType(string type)
        {
            return type == "layer-exists" || type == "layer-red" || type == "current-layer"
                || type == "hpname" || type == "hpscale" || type == "hpang" || type == "hplayer" || type == "cecolor"
                || type == "hatch-on-layer" || type == "saved";
        }

        private bool IsQuestionRuleType(string type)
        {
            return type == "layer" || type == "entity" || type == "pattern" || type == "color"
                || type == "scale" || type == "block" || type == "placement";
        }

        private void EvaluateStepState(string type, Document document, QuestionDefinition question,
            out bool passed, out string detail)
        {
            passed = false;
            if (type == "layer-exists" || type == "layer-red" || type == "current-layer")
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    if (type == "current-layer")
                    {
                        LayerTableRecord current = (LayerTableRecord)transaction.GetObject(document.Database.Clayer, OpenMode.ForRead);
                        passed = string.Equals(current.Name, "HATCH", StringComparison.OrdinalIgnoreCase);
                        detail = passed ? "通过：当前图层为 HATCH。" : "当前图层为“" + current.Name + "”，应为 HATCH。";
                    }
                    else
                    {
                        LayerTable layers = (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead);
                        passed = layers.Has("HATCH");
                        if (type == "layer-red" && passed)
                        {
                            LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(layers["HATCH"], OpenMode.ForRead);
                            passed = layer.Color.ColorIndex == 1;
                        }
                        detail = type == "layer-exists"
                            ? (passed ? "通过：HATCH 图层已存在。" : "缺少 HATCH 图层。")
                            : (passed ? "通过：HATCH 图层为索引色 1，与实体归属无关。" : "HATCH 图层不存在或颜色不是索引色 1。");
                    }
                    transaction.Commit();
                }
                return;
            }
            if (type == "hatch-on-layer")
            {
                int count = 0;
                int misplaced = 0;
                int scanned = 0;
                bool limited = false;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTable blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in modelSpace)
                    {
                        if (scanned >= MaxScannedObjects)
                        {
                            limited = true;
                            break;
                        }
                        scanned++;
                        Hatch hatch = transaction.GetObject(id, OpenMode.ForRead) as Hatch;
                        if (hatch != null)
                        {
                            count++;
                            if (!string.Equals(hatch.Layer, "HATCH", StringComparison.OrdinalIgnoreCase))
                            {
                                misplaced++;
                            }
                        }
                    }
                    transaction.Commit();
                }
                passed = !limited && count > 0 && misplaced == 0;
                detail = limited ? "hatch-on-layer：对象超过 " + MaxScannedObjects + " 个扫描上限，无法确认全部填充归属，保守失败。"
                    : (passed ? "通过：至少一个填充，且全部位于 HATCH 图层。"
                        : "填充归属未通过：模型空间填充 " + count + " 个，其中 " + misplaced + " 个不在 HATCH 图层。");
                return;
            }

            // GetSystemVariable 读取活动文档，拒绝拿另一张图的变量为传入文档作证。
            if (!object.ReferenceEquals(document, Application.DocumentManager.MdiActiveDocument))
            {
                detail = type + "：传入图纸不是活动文档，不能可靠读取其系统变量。";
                return;
            }
            if (type == "hpname")
            {
                string name = Convert.ToString(Application.GetSystemVariable("HPNAME"));
                string normalized = (name ?? string.Empty).Trim().TrimStart('_');
                passed = string.Equals(normalized, "ANSI31", StringComparison.OrdinalIgnoreCase);
                detail = passed ? "通过：HPNAME 为 ANSI31（忽略前缀 _）。" : "HPNAME 应为 ANSI31，当前为“" + name + "”。";
                return;
            }
            if (type == "hpscale")
            {
                object value = Application.GetSystemVariable("HPSCALE");
                double scale = value == null ? double.NaN : Convert.ToDouble(value);
                const double tolerance = 1e-9;
                passed = IsFinite(scale) && Math.Abs(scale - 1.0) <= tolerance;
                detail = passed ? "通过：HPSCALE 为 1（容差 1e-9）。" : "HPSCALE 应为 1（容差 1e-9），当前为 " + scale + "。";
                return;
            }
            if (type == "hpang")
            {
                double angle = Convert.ToDouble(Application.GetSystemVariable("HPANG"));
                passed = IsFinite(angle) && Math.Abs(angle) <= 1e-9;
                detail = passed ? "通过：HPANG 为 0。" : "HPANG 应为 0，当前为 " + angle + "。";
                return;
            }
            if (type == "hplayer")
            {
                string layer = Convert.ToString(Application.GetSystemVariable("HPLAYER"));
                passed = string.Equals(layer, ".", StringComparison.Ordinal);
                detail = passed ? "通过：HPLAYER 为 .，新填充使用当前层。" : "HPLAYER 应为英文句点 .，当前为“" + layer + "”。";
                return;
            }
            if (type == "cecolor")
            {
                string color = Application.GetSystemVariable("CECOLOR") as string;
                passed = string.Equals(color, "7", StringComparison.Ordinal);
                detail = passed ? "通过：CECOLOR 字符串为 7。" : "CECOLOR 应为字符串 7，当前为“" + color + "”。";
                return;
            }
            if (type == "saved")
            {
                if (string.IsNullOrWhiteSpace(question.ExpectedOutput))
                {
                    detail = "saved：未配置 question.ExpectedOutput，无法确认保存文件名。";
                    return;
                }
                string path = document.Database.Filename;
                string expected = question.ExpectedOutput.Trim();
                string actual = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
                bool exists = !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && File.Exists(path);
                object value = Application.GetSystemVariable("DBMOD");
                int dbmod = value == null ? -1 : Convert.ToInt32(value);
                passed = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) && exists && dbmod == 0;
                detail = passed ? "通过：文件名符合 ExpectedOutput、文件存在且 DBMOD=0；未验证几何。"
                    : "保存状态未通过：文件名“" + actual + "”，期望“" + expected + "”，文件存在=" + exists + "，DBMOD=" + dbmod + "；未验证几何。";
                return;
            }
            detail = "不支持的步骤状态类型：" + type + "。";
        }

        private sealed class RuleState
        {
            public bool HatchLayerExists;
            public bool HatchLayerIsRed;
            public bool DimLayerExists;
            public int HatchCount;
            public int DimensionCount;
            public int HatchOnOtherLayerCount;
            public int NonAnsi31Count;
            public int NonWhiteHatchCount;
            public int NonPositiveScaleCount;
            public bool TuDefinitionExists;
            public int TuReferenceCount;
            public bool ScanLimited;
        }

        private RuleState CollectRuleState(Document document)
        {
            RuleState state = new RuleState();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                LayerTable layers = (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead);
                state.HatchLayerExists = layers.Has("HATCH");
                if (state.HatchLayerExists)
                {
                    LayerTableRecord hatchLayer = (LayerTableRecord)transaction.GetObject(layers["HATCH"], OpenMode.ForRead);
                    state.HatchLayerIsRed = hatchLayer.Color.ColorIndex == 1;
                }
                state.DimLayerExists = layers.Has("DIM");

                BlockTable blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                state.TuDefinitionExists = blocks.Has("tu");
                ObjectId tuDefinitionId = state.TuDefinitionExists ? blocks["tu"] : ObjectId.Null;
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(
                    blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                int scanned = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (scanned >= MaxScannedObjects)
                    {
                        state.ScanLimited = true;
                        break;
                    }
                    scanned++;
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity == null)
                    {
                        continue;
                    }
                    Hatch hatch = entity as Hatch;
                    if (hatch != null)
                    {
                        state.HatchCount++;
                        if (!string.Equals(hatch.Layer, "HATCH", StringComparison.OrdinalIgnoreCase))
                        {
                            state.HatchOnOtherLayerCount++;
                        }
                        if (!string.Equals(hatch.PatternName, "ANSI31", StringComparison.OrdinalIgnoreCase))
                        {
                            state.NonAnsi31Count++;
                        }
                        if (hatch.Color.ColorIndex != 7)
                        {
                            state.NonWhiteHatchCount++;
                        }
                        if (hatch.PatternScale <= 0)
                        {
                            state.NonPositiveScaleCount++;
                        }
                    }
                    if (entity is Dimension)
                    {
                        state.DimensionCount++;
                    }
                    BlockReference reference = entity as BlockReference;
                    if (reference != null && tuDefinitionId != ObjectId.Null
                        && reference.BlockTableRecord == tuDefinitionId)
                    {
                        state.TuReferenceCount++;
                    }
                }
                transaction.Commit();
            }
            return state;
        }

        private CheckResult CheckQuestionRules(Document document, QuestionDefinition question)
        {
            RuleState state = CollectRuleState(document);
            if (state.ScanLimited)
            {
                return Failure("模型空间对象超过 " + MaxScannedObjects + " 个扫描上限，整题统计不完整，已保守判定失败。");
            }
            CheckResult result = new CheckResult();
            bool allPassed = true;
            int autoRules = 0;
            int failedRules = 0;
            int manualRules = 0;
            List<CheckRule> rules = new List<CheckRule>(question.Checks);
            if (string.Equals(question.Id, "3.1", StringComparison.OrdinalIgnoreCase))
            {
                bool hasPlacement = false;
                foreach (CheckRule rule in rules)
                {
                    if (rule != null && string.Equals((rule.Type ?? string.Empty).Trim(), "placement", StringComparison.OrdinalIgnoreCase)
                        && string.Equals((rule.Target ?? string.Empty).Trim(), "HATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPlacement = true;
                    }
                }
                if (!hasPlacement)
                {
                    // 仅在整题入口保留旧版 3.1 的归属要求；不污染步骤规则或题库对象。
                    rules.Add(new CheckRule { Type = "placement", Target = "HATCH", Expected = "HATCH" });
                }
            }
            foreach (CheckRule rule in rules)
            {
                bool passed;
                string detail;
                EvaluateRule(rule, state, out passed, out detail);
                result.Details.Add(detail);
                if (rule != null && string.Equals((rule.Type ?? string.Empty).Trim(), "manual", StringComparison.OrdinalIgnoreCase))
                {
                    // manual 不计入自动判定集合：它既不是通过，也不是“规则不满足”。
                    manualRules++;
                    continue;
                }
                autoRules++;
                if (!passed)
                {
                    failedRules++;
                    allPassed = false;
                }
            }

            result.Passed = autoRules > 0 && allPassed;
            result.RequiresManual = manualRules > 0;
            if (autoRules == 0)
            {
                result.Summary = question.Id + " 本题为人工检查：题库没有可自动判定的规则";
            }
            else if (result.Passed)
            {
                result.Summary = question.Id + " 规则检查通过（不代表题图全部要求已自动核实）";
                if (manualRules > 0)
                {
                    result.Summary = result.Summary + "；另有 " + manualRules + " 条人工检查项";
                }
            }
            else
            {
                result.Summary = question.Id + " 规则检查未通过：" + failedRules + "/" + autoRules + " 条规则未满足";
                if (manualRules > 0)
                {
                    result.Summary = result.Summary + "，另有 " + manualRules + " 条需人工确认";
                }
            }
            if (question.Id == "3.1" && result.Passed)
            {
                result.Summary = "3.1 基础属性检查通过（不代表整题完成）";
                result.Details.Add("ANSI31 是当前教学约定；区域、边界、疏密和保存名仍需人工确认。");
            }
            return result;
        }

        private void EvaluateRule(CheckRule rule, RuleState state, out bool passed, out string detail)
        {
            passed = false;
            if (rule == null)
            {
                detail = "检查项为空，规则检查失败。";
                return;
            }
            string type = (rule.Type ?? string.Empty).Trim().ToLowerInvariant();
            string target = (rule.Target ?? string.Empty).Trim();
            string expected = (rule.Expected ?? string.Empty).Trim();
            string message = string.IsNullOrWhiteSpace(rule.Message) ? "未通过题库检查规则。" : rule.Message;

            if (type == "manual")
            {
                // manual 表示“未自动判定”，passed 保持 false，避免被当成通过。
                detail = "人工检查（未自动判定）：" + message;
                return;
            }

            if (state.ScanLimited)
            {
                detail = type + "：模型空间超过 " + MaxScannedObjects + " 个对象，统计不完整，不能自动通过。";
                return;
            }

            if (type == "layer")
            {
                if (!string.Equals(target, "HATCH", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(target, "DIM", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "不支持的图层检查目标：" + target + "（当前引擎仅统计 HATCH、DIM 两个图层）。";
                    return;
                }
                bool exists = string.Equals(target, "HATCH", StringComparison.OrdinalIgnoreCase)
                    ? state.HatchLayerExists
                    : state.DimLayerExists;
                if (!exists)
                {
                    detail = message;
                    return;
                }
                if (string.Equals(expected, "exists", StringComparison.OrdinalIgnoreCase))
                {
                    passed = true;
                    detail = "通过：" + target + " 图层已存在。";
                    return;
                }
                if (expected.StartsWith("colorIndex=", StringComparison.OrdinalIgnoreCase))
                {
                    int colorIndex;
                    if (!TryParseColorIndex(expected, out colorIndex))
                    {
                        detail = "图层颜色规则格式错误：" + expected + "（应为 colorIndex=数字）。";
                        return;
                    }
                    if (string.Equals(target, "DIM", StringComparison.OrdinalIgnoreCase))
                    {
                        // 引擎未统计 DIM 图层颜色，不凭猜测判定。
                        detail = "暂不支持 DIM 图层的颜色规则；题库可改用 layer/DIM/exists，或先扩展引擎统计。";
                        return;
                    }
                    if (colorIndex != 1)
                    {
                        detail = "HATCH 图层颜色规则目前只支持 colorIndex=1；收到：" + expected + "。";
                        return;
                    }
                    passed = state.HatchLayerIsRed;
                    detail = passed
                        ? "通过：HATCH 图层存在，颜色为红色（索引色 1），未检查实体归属。"
                        : "HATCH 图层已存在，但颜色不是红色（索引色 1）。";
                    return;
                }
                detail = "不支持的图层期望值：" + expected + "（当前支持 exists、colorIndex=数字）。";
                return;
            }

            if (type == "placement")
            {
                if (!string.Equals(target, "HATCH", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(expected, "HATCH", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "归属规则目前仅支持 placement / HATCH / HATCH，收到：" + target + " / " + expected + "。";
                    return;
                }
                passed = state.HatchCount > 0 && state.HatchOnOtherLayerCount == 0;
                detail = passed
                    ? "通过：全部 " + state.HatchCount + " 个填充对象位于 HATCH 图层。"
                    : (state.HatchCount == 0 ? "填充归属检查失败：没有填充对象。"
                        : state.HatchOnOtherLayerCount + " 个填充对象不在 HATCH 图层。");
                return;
            }

            if (type == "entity")
            {
                int count;
                if (string.Equals(target, "HATCH", StringComparison.OrdinalIgnoreCase))
                {
                    count = state.HatchCount;
                }
                else if (string.Equals(target, "DIMENSION", StringComparison.OrdinalIgnoreCase))
                {
                    count = state.DimensionCount;
                }
                else
                {
                    detail = "不支持的实体检查目标：" + target + "（当前仅统计 HATCH、DIMENSION）。";
                    return;
                }
                int minimum;
                if (!TryParseMinimumCount(expected, out minimum))
                {
                    detail = "实体数量规则格式错误：" + expected + "（应为 count>=数字）。";
                    return;
                }
                passed = count >= minimum;
                detail = passed
                    ? "通过：找到 " + count + " 个 " + target + " 对象（要求至少 " + minimum + " 个）。"
                    : message + "（当前 " + count + " 个，要求至少 " + minimum + " 个）";
                return;
            }

            if (type == "pattern")
            {
                if (!string.Equals(target, "HATCH", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "不支持的图案检查目标：" + target + "（当前仅支持 HATCH）。";
                    return;
                }
                // 统计层按 ANSI31 计算非匹配数量；期望值换成别的图案会得到错误结论，故明确拒绝。
                if (!string.Equals(expected, "ANSI31", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "图案规则目前只支持期望值 ANSI31（引擎按 ANSI31 统计非匹配数量）；要检查 "
                        + expected + " 需先扩展 RuleState。";
                    return;
                }
                if (state.HatchCount == 0)
                {
                    detail = message + "（当前没有填充对象）";
                    return;
                }
                passed = state.NonAnsi31Count == 0;
                detail = passed
                    ? "通过：全部 " + state.HatchCount + " 个填充对象都是 ANSI31 图案。"
                    : state.NonAnsi31Count + " 个填充对象不是 ANSI31 图案。";
                return;
            }

            if (type == "color")
            {
                if (!string.Equals(target, "HATCH", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "不支持的显式颜色检查目标：" + target + "（当前仅支持 HATCH）。";
                    return;
                }
                int colorIndex;
                if (!TryParseColorIndex(expected, out colorIndex))
                {
                    detail = "显式颜色规则格式错误：" + expected + "（应为 colorIndex=数字）。";
                    return;
                }
                if (colorIndex != 7)
                {
                    detail = "显式颜色规则目前只支持 colorIndex=7（引擎按索引色 7 统计）；要检查索引色 "
                        + colorIndex + " 需先扩展 RuleState。";
                    return;
                }
                if (state.HatchCount == 0)
                {
                    detail = message + "（当前没有填充对象）";
                    return;
                }
                passed = state.NonWhiteHatchCount == 0;
                detail = passed
                    ? "通过：全部 " + state.HatchCount + " 个填充对象都是显式白色（索引色 7）。"
                    : state.NonWhiteHatchCount + " 个填充对象不是显式白色（索引色 7）。";
                return;
            }

            if (type == "scale")
            {
                if (!string.Equals(target, "HATCH", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "不支持的填充比例检查目标：" + target + "（当前仅支持 HATCH）。";
                    return;
                }
                if (!string.Equals(expected, "positive", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "填充比例规则目前只支持期望值 positive，收到：" + expected + "。";
                    return;
                }
                if (state.HatchCount == 0)
                {
                    detail = message + "（当前没有填充对象）";
                    return;
                }
                passed = state.NonPositiveScaleCount == 0;
                detail = passed
                    ? "通过：全部 " + state.HatchCount + " 个填充对象的比例都是有效正数。"
                    : state.NonPositiveScaleCount + " 个填充对象的比例无效（不是正数）。";
                return;
            }

            if (type == "block")
            {
                if (!string.Equals(target, "tu", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "不支持的图块检查目标：" + target + "（引擎目前只统计 tu 图块）。";
                    return;
                }
                if (!string.Equals(expected, "definition+reference", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "图块规则目前只支持期望值 definition+reference，收到：" + expected + "。";
                    return;
                }
                passed = state.TuDefinitionExists && state.TuReferenceCount > 0;
                if (passed)
                {
                    detail = "通过：存在 tu 图块定义，且有 " + state.TuReferenceCount + " 个参照。";
                }
                else if (!state.TuDefinitionExists)
                {
                    detail = "未找到名为 tu 的块定义。";
                }
                else
                {
                    detail = "已有 tu 块定义，但模型空间没有插入参照。";
                }
                return;
            }

            detail = "不支持的题库规则类型：" + (rule.Type ?? "<空>") + "。";
        }

        private bool TryParseMinimumCount(string expected, out int minimum)
        {
            minimum = 0;
            const string prefix = "count>=";
            if (string.IsNullOrEmpty(expected) || !expected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return int.TryParse(expected.Substring(prefix.Length).Trim(), out minimum) && minimum >= 0;
        }

        private bool TryParseColorIndex(string expected, out int colorIndex)
        {
            colorIndex = 0;
            const string prefix = "colorIndex=";
            return !string.IsNullOrEmpty(expected)
                && expected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(expected.Substring(prefix.Length).Trim(), out colorIndex);
        }

        private CheckResult CheckHatchQuestion(Document document)
        {
            CheckResult result = new CheckResult();
            bool hasHatchLayer = false;
            bool hatchLayerIsRed = false;
            bool hasHatch = false;
            bool allHatchesMatch = true;
            int hatchCount = 0;
            int nonAnsi31Count = 0;
            int nonWhiteCount = 0;
            int nonPositiveScaleCount = 0;
            int hatchOnOtherLayerCount = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                LayerTable layers = (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead);
                hasHatchLayer = layers.Has("HATCH");
                if (!hasHatchLayer)
                {
                    result.Details.Add("缺少 HATCH 图层；请创建该图层并设置为红色。");
                }
                else
                {
                    LayerTableRecord layer = (LayerTableRecord)transaction.GetObject(layers["HATCH"], OpenMode.ForRead);
                    hatchLayerIsRed = layer.Color.ColorIndex == 1;
                    result.Details.Add(hatchLayerIsRed
                        ? "HATCH 图层存在，颜色为红色。"
                        : "HATCH 图层已存在，但颜色不是红色（索引色 1）。");
                }

                BlockTable blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                int scanned = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (scanned >= MaxScannedObjects)
                    {
                        return Failure("模型空间对象超过 " + MaxScannedObjects + " 个扫描上限，填充整题统计不完整，已保守判定失败。");
                    }
                    scanned++;
                    Hatch hatch = transaction.GetObject(id, OpenMode.ForRead) as Hatch;
                    if (hatch == null)
                    {
                        continue;
                    }

                    hasHatch = true;
                    hatchCount++;
                    if (!string.Equals(hatch.Layer, "HATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        hatchOnOtherLayerCount++;
                    }
                    if (!string.Equals(hatch.PatternName, "ANSI31", StringComparison.OrdinalIgnoreCase))
                    {
                        nonAnsi31Count++;
                    }
                    if (hatch.Color.ColorIndex != 7)
                    {
                        nonWhiteCount++;
                    }
                    if (hatch.PatternScale <= 0)
                    {
                        nonPositiveScaleCount++;
                    }
                }
                transaction.Commit();
            }

            if (!hasHatch)
            {
                result.Details.Add("尚未找到填充对象。");
                allHatchesMatch = false;
            }
            else
            {
                result.Details.Add("已找到 " + hatchCount + " 个填充对象。");
                if (hatchOnOtherLayerCount > 0)
                {
                    result.Details.Add(hatchOnOtherLayerCount + " 个填充对象不在 HATCH 图层。");
                    allHatchesMatch = false;
                }
                if (nonAnsi31Count > 0)
                {
                    result.Details.Add(nonAnsi31Count + " 个填充对象不是 ANSI31 图案。");
                    allHatchesMatch = false;
                }
                if (nonWhiteCount > 0)
                {
                    result.Details.Add(nonWhiteCount + " 个填充对象不是显式白色（索引色 7）。");
                    allHatchesMatch = false;
                }
                if (nonPositiveScaleCount > 0)
                {
                    result.Details.Add(nonPositiveScaleCount + " 个填充对象的比例无效。");
                    allHatchesMatch = false;
                }
                if (allHatchesMatch)
                {
                    result.Details.Add("填充所在图层、图案和对象颜色均符合当前规则；比例暂按可视范围人工确认。");
                }
            }

            result.Passed = hasHatchLayer && hatchLayerIsRed && hasHatch && allHatchesMatch;
            result.Details.Add("ANSI31 是当前教学约定，不代表已核实的官方唯一答案；区域、边界、疏密和保存名尚未自动检查。");
            result.Summary = result.Passed ? "3.1 基础属性检查通过（不代表整题完成）" : "3.1 基础属性尚未符合当前教学规则";
            return result;
        }

        // 调用方先 ClearHighlights，再锁定 document；此方法只读取模型空间。
        // HintTarget=overview 时还会缩放到整张起始图（起点定位），仅改视图不写库。
        public CheckResult HighlightActiveQuestion(Document document)
        {
            RequireClearedHighlights();
            if (document == null)
            {
                return Failure("没有活动图纸");
            }
            if (_activeStep != null)
            {
                ValidateHintTarget(_activeStep.HintTarget);
                if (IsTextOnlyTarget(_activeStep.HintTarget))
                {
                    return StepInstructions(_activeStep);
                }
            }

            CheckResult result = new CheckResult();
            // overview = 起点定位：高亮整张起始图的几何对象，并把视图缩放到全图。
            bool isOverview = _activeStep != null && IsOverviewTarget(_activeStep.HintTarget);
            int highlighted = 0;
            int scanned = 0;
            int skipped = 0;
            bool limited = false;
            bool hasExtents = false;
            double minX = 0.0;
            double minY = 0.0;
            double maxX = 0.0;
            double maxY = 0.0;
            _highlightDocument = document;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (scanned >= MaxScannedObjects || (!isOverview && _highlightedIds.Count >= MaxHighlightedObjects))
                    {
                        limited = true;
                        break;
                    }
                    scanned++;
                    try
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased)
                        {
                            continue;
                        }
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                        if (!ShouldHighlight(entity, transaction))
                        {
                            continue;
                        }

                        // 全图范围独立于 200 个高亮上限；额外对象继续贡献范围，不再注册高亮。
                        if (_highlightedIds.Count < MaxHighlightedObjects)
                        {
                            _highlightedIds.Add(id);
                            entity.Highlight();
                            highlighted++;
                        }
                        if (isOverview)
                        {
                            Extents3d extents;
                            if (TryGetPlanarExtents(entity, out extents))
                            {
                                if (!hasExtents)
                                {
                                    minX = extents.MinPoint.X;
                                    minY = extents.MinPoint.Y;
                                    maxX = extents.MaxPoint.X;
                                    maxY = extents.MaxPoint.Y;
                                    hasExtents = true;
                                }
                                else
                                {
                                    if (extents.MinPoint.X < minX)
                                    {
                                        minX = extents.MinPoint.X;
                                    }
                                    if (extents.MinPoint.Y < minY)
                                    {
                                        minY = extents.MinPoint.Y;
                                    }
                                    if (extents.MaxPoint.X > maxX)
                                    {
                                        maxX = extents.MaxPoint.X;
                                    }
                                    if (extents.MaxPoint.Y > maxY)
                                    {
                                        maxY = extents.MaxPoint.Y;
                                    }
                                }
                            }
                        }
                    }
                    catch (System.Exception)
                    {
                        skipped++;
                    }
                }
                transaction.Commit();
            }

            string zoomError = null;
            if (isOverview && highlighted > 0 && hasExtents)
            {
                zoomError = limited || skipped > 0 ? "对象范围未完整读取，未用部分范围冒充全图"
                    : TryZoomToPlanarExtents(document, minX, minY, maxX, maxY);
            }

            ReleaseDocumentIfEmpty();
            result.Passed = highlighted > 0;
            if (isOverview)
            {
                result.Summary = result.Passed
                    ? "起点定位：已高亮起始图 " + highlighted + " 个几何对象"
                        + (hasExtents && zoomError == null ? "，视图已缩放到全图" : "")
                    : "起点定位：当前图纸没有几何对象，可能还没打开起始图";
                if (result.Passed)
                {
                    result.Details.Add("这是第 1 步“打开起始图”的定位提示：整张练习图的线条、圆弧、圆和多段线被临时高亮，方便你确认要改的是哪张图、图形在什么位置。");
                    result.Details.Add("这一步不需要画任何东西。看完图形后点【下一步】继续。");
                }
            }
            else
            {
                result.Summary = result.Passed ? "已高亮当前题目/步骤相关对象（不代表检查通过）" : "没有找到可成功高亮的匹配对象";
            }
            result.Details.Add("已高亮 " + highlighted + " 个对象；只改变显示，不修改 DWG，也不参与评分。");
            if (zoomError != null)
            {
                result.Details.Add("视图未能自动缩放到全图（" + zoomError + "）；可手动输入 ZOOM 后选 E 查看起点范围。");
            }
            if (highlighted == 0)
            {
                result.Details.Add(isOverview
                    ? "起始图里没有可定位的线条、圆弧、圆或多段线；请先确认打开的是本题起始图。"
                    : "图上还没有这类对象：先照【步骤说明】里的编号把图形做出来，再点本按钮。");
            }
            if (limited || skipped > 0)
            {
                result.Details.Add("本次最多扫描 " + MaxScannedObjects + " 个对象、记录 " + MaxHighlightedObjects
                    + " 个高亮；" + (limited ? "已达到限制；" : "") + "跳过异常对象 " + skipped + " 个，失败高亮记录保留待清理。");
            }
            return result;
        }

        // 调用方负责清理旧提示并持有 document 的锁；不自动缩放、不写入数据库。
        // 例外：HintTarget=overview（起点定位）会额外把视图缩放到整张起始图，只改视图，不改实体。
        public CheckResult ShowStepGuide(Document document, LearningStep step)
        {
            RequireClearedHighlights();
            SetActiveStep(step);
            if (document == null)
            {
                return Failure("没有活动图纸");
            }
            if (step == null)
            {
                return Failure("请先选择学习步骤；未生成箭头。");
            }
            string target = step.HintTarget;
            if (IsTextOnlyTarget(target))
            {
                return StepInstructions(step);
            }
            if (IsOverviewTarget(target))
            {
                // 起点步骤的目标是整张图，没有单个需要指向的对象；画箭头会让人误以为要改那几个对象。
                CheckResult overviewResult = new CheckResult
                {
                    Passed = false,
                    Summary = "起点定位不用箭头，请点【高亮相关对象】"
                };
                overviewResult.Details.Add("第 1 步“打开起始图”要确认的是整张图，没有单个需要指向的对象，所以不生成箭头。");
                overviewResult.Details.Add("点【高亮相关对象】会高亮整张起始图，并把视图缩放到全图，方便你看清起点。");
                if (step != null && !string.IsNullOrWhiteSpace(step.Hint))
                {
                    overviewResult.Details.Add("步骤提示：" + step.Hint);
                }
                return overviewResult;
            }
            if (!document.Database.TileMode)
            {
                return Failure("箭头仅支持二维模型空间；当前 TileMode=false，即使进入布局视口也不生成箭头。");
            }

            Vector3d right;
            Vector3d up;
            double length;
            using (ViewTableRecord view = document.Editor.GetCurrentView())
            {
                Vector3d direction = view.ViewDirection;
                if (!IsFinite(direction.X) || !IsFinite(direction.Y) || !IsFinite(direction.Z)
                    || direction.Length < 1e-12 || !IsFinite(view.ViewTwist)
                    || view.PerspectiveEnabled || Math.Abs(direction.GetNormal().Z) < 1.0 - 1e-10)
                {
                    return Failure("箭头仅支持非透视的二维平面视图；请切回模型空间平面视图后重试。");
                }
                length = Math.Min(view.Height, view.Width) * 0.12;
                if (!IsFinite(view.Height) || !IsFinite(view.Width) || !IsFinite(length) || length <= 1e-9)
                {
                    return Failure("当前视图尺寸无效，未生成箭头。");
                }
                Vector3d normal = direction.GetNormal();
                Matrix3d planeToWorld = Matrix3d.PlaneToWorld(normal);
                Matrix3d twist = Matrix3d.Rotation(-view.ViewTwist, normal, Point3d.Origin);
                right = Vector3d.XAxis.TransformBy(planeToWorld).TransformBy(twist).GetNormal();
                up = Vector3d.YAxis.TransformBy(planeToWorld).TransformBy(twist).GetNormal();
            }

            List<Point3d> centers = new List<Point3d>(MaxGuideArrows);
            int scanned = 0;
            int badExtents = 0;
            int skipped = 0;
            bool limited = false;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (scanned >= MaxScannedObjects || centers.Count >= MaxGuideArrows)
                    {
                        limited = true;
                        break;
                    }
                    scanned++;
                    Entity entity;
                    try
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased)
                        {
                            continue;
                        }
                        entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                        if (!MatchesTarget(entity, transaction, target))
                        {
                            continue;
                        }
                    }
                    catch (System.Exception)
                    {
                        skipped++;
                        continue;
                    }
                    Point3d center;
                    if (!TryGetGuideCenter(entity, out center))
                    {
                        badExtents++;
                        continue;
                    }
                    centers.Add(center);
                }
                transaction.Commit();
            }

            CheckResult result = new CheckResult();
            int arrows = 0;
            string drawError = null;
            if (centers.Count > 0)
            {
                _highlightDocument = document;
                foreach (Point3d center in centers)
                {
                    if (!TryAddGuideArrow(center, right, up, length, out drawError))
                    {
                        break;
                    }
                    arrows++;
                }
                ReleaseDocumentIfEmpty();
            }
            result.Passed = arrows > 0;
            result.Summary = result.Passed ? "已显示 " + arrows + " 个步骤定位箭头（不代表评分通过）" : "未生成可用的步骤箭头";
            result.Details.Add("黄色箭头指向的是【图上的对象位置】，不是面板上的按钮；它只是提示，不会改动图形。");
            result.Details.Add("箭头不会自动缩放视图：看不到就先输 Z 回车、再输 E 回车缩放到全图，然后重新点一次按钮。");
            result.Details.Add("最多扫描 " + MaxScannedObjects + " 个对象、保留 " + MaxGuideArrows
                + " 个中心（每箭头 3 条黄色线）；本次扫描 " + scanned + " 个，坏/非二维 Extents 跳过 "
                + badExtents + " 个，读取异常跳过 " + skipped + " 个。" + (limited ? "已按数量上限截断，并非全部目标。" : ""));
            if (centers.Count == 0)
            {
                result.Details.Add("图上还没有可指向的对象：先照【步骤说明】里的编号把图形做出来，再点本按钮。");
            }
            if (drawError != null)
            {
                result.Details.Add("临时箭头注册失败：" + drawError + "；无法擦除的临时线仍保留记录，请先清除再重试。");
            }
            return result;
        }

        public bool HasHighlightsIn(Document document)
        {
            return document != null && object.ReferenceEquals(_highlightDocument, document);
        }

        // 仅供不可取消的图纸销毁通知调用；不同于普通清理，最终必须释放全部引用。
        public void ForgetHighlights(Document document)
        {
            if (!HasHighlightsIn(document))
            {
                return;
            }
            try
            {
                foreach (Entity transient in _guideEntities)
                {
                    try
                    {
                        if (!TransientManager.CurrentTransientManager.EraseTransient(transient, new IntegerCollection()))
                        {
                            System.Diagnostics.Trace.WriteLine("销毁图纸时临时图形擦除返回 false，仍将释放引用。");
                        }
                    }
                    catch (System.Exception exception)
                    {
                        System.Diagnostics.Trace.WriteLine("销毁图纸时临时图形擦除失败：" + exception.Message);
                    }
                    finally
                    {
                        try
                        {
                            transient.Dispose();
                        }
                        catch (System.Exception exception)
                        {
                            System.Diagnostics.Trace.WriteLine("销毁图纸时临时图形释放失败：" + exception.Message);
                        }
                    }
                }
            }
            finally
            {
                _guideEntities.Clear();
                _highlightedIds.Clear();
                _highlightDocument = null;
            }
        }

        public void ClearHighlights()
        {
            Document document = _highlightDocument;
            if (document == null)
            {
                return;
            }
            if (_highlightedIds.Count == 0 && _guideEntities.Count == 0)
            {
                _highlightDocument = null;
                return;
            }

            List<ObjectId> remaining = new List<ObjectId>();
            string firstError = null;
            // 必须锁定原所属图纸；锁定/事务异常时不丢弃尚待清理的记录。
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                for (int index = _guideEntities.Count - 1; index >= 0; index--)
                {
                    Entity transient = _guideEntities[index];
                    try
                    {
                        if (!TransientManager.CurrentTransientManager.EraseTransient(transient, new IntegerCollection()))
                        {
                            if (firstError == null)
                            {
                                firstError = "EraseTransient 返回 false，保留临时图形以便重试。";
                            }
                            continue;
                        }
                    }
                    catch (System.Exception exception)
                    {
                        if (firstError == null)
                        {
                            firstError = exception.GetType().Name + ": " + exception.Message;
                        }
                        // 仍可能注册于图形系统，禁止 Dispose。
                        continue;
                    }
                    _guideEntities.RemoveAt(index);
                    try
                    {
                        transient.Dispose();
                    }
                    catch (System.Exception exception)
                    {
                        if (firstError == null)
                        {
                            firstError = "已擦除临时图形，但释放失败：" + exception.Message;
                        }
                    }
                }
                foreach (ObjectId id in _highlightedIds)
                {
                    try
                    {
                        if (id.IsNull || !id.IsValid || id.IsErased)
                        {
                            continue;
                        }
                        if (!object.Equals(id.Database, document.Database))
                        {
                            throw new InvalidOperationException("检测到不属于原图纸的对象，已拒绝跨图纸访问。");
                        }
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity != null)
                        {
                            entity.Unhighlight();
                        }
                    }
                    catch (System.Exception exception)
                    {
                        remaining.Add(id);
                        if (firstError == null)
                        {
                            firstError = exception.GetType().Name + ": " + exception.Message;
                        }
                    }
                }
                transaction.Commit();
            }
            _highlightedIds.Clear();
            _highlightedIds.AddRange(remaining);
            ReleaseDocumentIfEmpty();
            if (firstError != null)
            {
                throw new InvalidOperationException("显示清理未全部成功；保留 " + _guideEntities.Count
                    + " 条临时线、" + _highlightedIds.Count + " 个高亮记录：" + firstError);
            }
        }

        private void RequireClearedHighlights()
        {
            if (_highlightDocument != null || _guideEntities.Count > 0 || _highlightedIds.Count > 0)
            {
                throw new InvalidOperationException("请先清除上一轮高亮/箭头，再重新显示提示。");
            }
        }

        private void ReleaseDocumentIfEmpty()
        {
            if (_guideEntities.Count == 0 && _highlightedIds.Count == 0)
            {
                _highlightDocument = null;
            }
        }

        private bool ShouldHighlight(Entity entity, Transaction transaction)
        {
            if (entity == null)
            {
                return false;
            }

            // 第二版优先采用当前步骤的显式定位目标；未配置目标时保留第一版题型级高亮。
            if (_activeStep != null)
            {
                string target = _activeStep.HintTarget;
                ValidateHintTarget(target);
                if (!IsTextOnlyTarget(target))
                {
                    return MatchesTarget(entity, transaction, target);
                }
                return false;
            }

            if (_activeQuestionId == "3.1")
            {
                return entity is Hatch;
            }
            if (_activeQuestionId == "3.18")
            {
                BlockReference reference = entity as BlockReference;
                if (reference == null)
                {
                    return false;
                }
                BlockTableRecord definition = transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                return definition != null && string.Equals(definition.Name, "tu", StringComparison.OrdinalIgnoreCase);
            }
            if (_activeQuestionId == "6.1")
            {
                return entity is Dimension || string.Equals(entity.Layer, "DIM", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private void ValidateHintTarget(string target)
        {
            if (IsTextOnlyTarget(target))
            {
                return;
            }
            string normalized = target.Trim().ToLowerInvariant();
            if (normalized != "overview" && normalized != "geometry" && normalized != "hatch"
                && normalized != "block:tu" && normalized != "dimension")
            {
                throw new InvalidOperationException("不支持的步骤提示目标“" + target + "”。可用值：overview、geometry、hatch、block:tu、dimension、none。");
            }
        }

        private bool IsTextOnlyTarget(string target)
        {
            return string.IsNullOrWhiteSpace(target)
                || string.Equals(target.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }

        // overview 专指“起点定位”：整张起始图的范围，不是某个待修改对象。
        private static bool IsOverviewTarget(string target)
        {
            return !string.IsNullOrWhiteSpace(target)
                && string.Equals(target.Trim(), "overview", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetPlanarExtents(Entity entity, out Extents3d extents)
        {
            extents = new Extents3d();
            try
            {
                if (entity == null)
                {
                    return false;
                }
                Extents3d raw = entity.GeometricExtents;
                Point3d min = raw.MinPoint;
                Point3d max = raw.MaxPoint;
                if (!IsFinite(min.X) || !IsFinite(min.Y) || !IsFinite(min.Z)
                    || !IsFinite(max.X) || !IsFinite(max.Y) || !IsFinite(max.Z))
                {
                    return false;
                }
                extents = raw;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        // 仅用于起点定位：把视图缩放到给定二维范围。失败时返回原因，不抛异常、不改数据库。
        private string TryZoomToPlanarExtents(Document document, double minX, double minY, double maxX, double maxY)
        {
            try
            {
                double width = maxX - minX;
                double height = maxY - minY;
                double centerX = (minX + maxX) / 2.0;
                double centerY = (minY + maxY) / 2.0;
                if (!IsFinite(width) || !IsFinite(height) || !IsFinite(centerX) || !IsFinite(centerY))
                {
                    return "范围数值无效";
                }
                // 退化图形（点、极小图形）给一个最小可见范围，避免缩放到无穷远。
                if (width < 1.0)
                {
                    width = 1.0;
                }
                if (height < 1.0)
                {
                    height = 1.0;
                }

                var editor = document.Editor;
                using (ViewTableRecord view = editor.GetCurrentView())
                {
                    double ratio = view.Height > 1e-9 ? view.Width / view.Height : 1.0;
                    if (!IsFinite(ratio) || ratio < 1e-6)
                    {
                        ratio = 1.0;
                    }
                    double viewWidth = width * 1.15;
                    double viewHeight = height * 1.15;
                    // 按当前视口纵横比补齐，否则图形会被拉伸。
                    if (viewWidth / viewHeight > ratio)
                    {
                        viewHeight = viewWidth / ratio;
                    }
                    else
                    {
                        viewWidth = viewHeight * ratio;
                    }

                    // Target 为 WCS 中心，CenterPoint 为以该 Target 为原点的视图平面坐标。
                    view.CenterPoint = Point2d.Origin;
                    view.Target = new Point3d(centerX, centerY, 0.0);
                    view.ViewDirection = Vector3d.ZAxis;
                    view.ViewTwist = 0.0;
                    view.PerspectiveEnabled = false;
                    view.Width = viewWidth;
                    view.Height = viewHeight;
                    editor.SetCurrentView(view);
                }
                editor.UpdateScreen();
                return null;
            }
            catch (System.Exception ex)
            {
                return ex.Message;
            }
        }

        private CheckResult StepInstructions(LearningStep step)
        {
            CheckResult result = new CheckResult
            {
                Passed = false,
                Summary = "本步以文字提示为主，未生成箭头"
            };
            result.Details.Add("当前步骤没有可靠的图形定位目标，不虚构箭头位置。");
            if (step != null && !string.IsNullOrWhiteSpace(step.Hint))
            {
                result.Details.Add("步骤提示：" + step.Hint);
            }
            return result;
        }

        private bool MatchesTarget(Entity entity, Transaction transaction, string target)
        {
            if (entity == null || IsTextOnlyTarget(target))
            {
                return false;
            }
            string normalized = target.Trim().ToLowerInvariant();
            if (normalized == "hatch")
            {
                return entity is Hatch;
            }
            if (normalized == "dimension")
            {
                return entity is Dimension;
            }
            if (normalized == "overview" || normalized == "geometry")
            {
                return entity is Line || entity is Arc || entity is Circle
                    || entity is Polyline || entity is Polyline2d || entity is Polyline3d;
            }
            if (normalized == "block:tu")
            {
                BlockReference reference = entity as BlockReference;
                if (reference == null)
                {
                    return false;
                }
                BlockTableRecord definition = transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                return definition != null && string.Equals(definition.Name, "tu", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private bool TryGetGuideCenter(Entity entity, out Point3d center)
        {
            center = Point3d.Origin;
            try
            {
                Extents3d extents = entity.GeometricExtents;
                Point3d min = extents.MinPoint;
                Point3d max = extents.MaxPoint;
                double x = (min.X + max.X) / 2.0;
                double y = (min.Y + max.Y) / 2.0;
                double z = (min.Z + max.Z) / 2.0;
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                {
                    return false;
                }
                center = new Point3d(x, y, z);
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private bool TryAddGuideArrow(Point3d center, Vector3d right, Vector3d up, double length, out string error)
        {
            error = null;
            List<Line> registered = new List<Line>();
            try
            {
                Vector3d normalizedRight = right.GetNormal();
                Vector3d normalizedUp = up.GetNormal();
                Point3d basePoint = center - normalizedUp * length;
                Point3d leftWing = center - normalizedUp * (length * 0.35) - normalizedRight * (length * 0.35);
                Point3d rightWing = center - normalizedUp * (length * 0.35) + normalizedRight * (length * 0.35);
                Point3d[] starts = { basePoint, leftWing, rightWing };
                Point3d[] ends = { center, center, center };
                TransientManager manager = TransientManager.CurrentTransientManager;
                for (int index = 0; index < starts.Length; index++)
                {
                    Line line = new Line(starts[index], ends[index]);
                    line.ColorIndex = 2;
                    if (!manager.AddTransient(line, TransientDrawingMode.DirectShortTerm, 128, new IntegerCollection()))
                    {
                        line.Dispose();
                        throw new InvalidOperationException("AddTransient 返回 false。");
                    }
                    _guideEntities.Add(line);
                    registered.Add(line);
                }
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                for (int index = registered.Count - 1; index >= 0; index--)
                {
                    Line line = registered[index];
                    bool erased = false;
                    try
                    {
                        erased = TransientManager.CurrentTransientManager.EraseTransient(line, new IntegerCollection());
                    }
                    catch (System.Exception eraseException)
                    {
                        error += "；清理临时线失败：" + eraseException.Message;
                    }
                    if (erased)
                    {
                        _guideEntities.Remove(line);
                        line.Dispose();
                    }
                }
                return false;
            }
        }

        // 先清掉上一轮的箭头/高亮，再画新的点选箭头。调用方不需要自己处理清除顺序。
        public CheckResult ClearAndShowClickPoint(Document document, Point3d point, double arrowLength)
        {
            ClearHighlights();
            return ShowClickPoint(document, point, arrowLength);
        }

        // 在图上用红箭头 + 圆圈指出“就点这里”。
        // 只是屏幕临时图形：不进数据库、不改图、不影响任何检查规则。
        public CheckResult ShowClickPoint(Document document, Point3d point, double arrowLength)
        {
            RequireClearedHighlights();
            if (document == null)
            {
                throw new InvalidOperationException("当前没有活动图纸，无法在图上指出位置。");
            }
            if (!IsFinite(point.X) || !IsFinite(point.Y) || !IsFinite(point.Z))
            {
                throw new InvalidOperationException("要指出的点坐标无效。");
            }
            double length = arrowLength;
            if (!IsFinite(length) || length <= 0)
            {
                length = 1.0;
            }

            _highlightDocument = document;
            List<Entity> registered = new List<Entity>();
            try
            {
                Point3d tip = new Point3d(point.X, point.Y, point.Z);
                Point3d tail = new Point3d(point.X, point.Y + length, point.Z);
                Point3d leftWing = new Point3d(point.X - length * 0.32, point.Y + length * 0.62, point.Z);
                Point3d rightWing = new Point3d(point.X + length * 0.32, point.Y + length * 0.62, point.Z);
                AddPointerLine(tip, tail, registered);
                AddPointerLine(tip, leftWing, registered);
                AddPointerLine(tip, rightWing, registered);

                Circle ring = new Circle(tip, Vector3d.ZAxis, length * 0.12);
                ring.ColorIndex = 1;
                RegisterTransient(ring, registered);
            }
            catch (System.Exception exception)
            {
                string rollbackError = RollbackTransients(registered);
                ReleaseDocumentIfEmpty();
                throw new InvalidOperationException("在图上画点选箭头失败：" + exception.GetType().Name
                    + ": " + exception.Message + (rollbackError == null ? string.Empty : "；回滚时" + rollbackError));
            }

            CheckResult result = new CheckResult
            {
                Passed = false,
                Summary = "已在图上用红色箭头指出要点的位置（只是提示，不改图）"
            };
            result.Details.Add("箭头尖端那个小圆圈，就是要把鼠标点下去的位置；不要点在线上。");
            result.Details.Add("箭头是屏幕临时图形，缩放/平移视图后可能消失；消失就再点一次按钮重新画。");
            result.Details.Add("如果箭头指的位置不是你要填充的那块区域，说明这张图的闭合边界和助手推算的不一致：按 Esc 取消后自己重新点。");
            return result;
        }

        private void AddPointerLine(Point3d start, Point3d end, List<Entity> registered)
        {
            Line line = new Line(start, end);
            line.ColorIndex = 1;
            RegisterTransient(line, registered);
        }

        private void RegisterTransient(Entity entity, List<Entity> registered)
        {
            if (!TransientManager.CurrentTransientManager.AddTransient(entity, TransientDrawingMode.DirectShortTerm, 128, new IntegerCollection()))
            {
                entity.Dispose();
                throw new InvalidOperationException("AddTransient 返回 false。");
            }
            _guideEntities.Add(entity);
            registered.Add(entity);
        }

        private string RollbackTransients(List<Entity> registered)
        {
            string error = null;
            for (int index = registered.Count - 1; index >= 0; index--)
            {
                Entity entity = registered[index];
                bool erased = false;
                try
                {
                    erased = TransientManager.CurrentTransientManager.EraseTransient(entity, new IntegerCollection());
                }
                catch (System.Exception exception)
                {
                    error = "清理临时图形失败：" + exception.Message;
                }
                if (erased)
                {
                    _guideEntities.Remove(entity);
                    try
                    {
                        entity.Dispose();
                    }
                    catch (System.Exception)
                    {
                        // 已从图形系统移除，释放失败不再追究。
                    }
                }
            }
            return error;
        }

        private bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private CheckResult CheckBlockQuestion(Document document)
        {
            CheckResult result = new CheckResult();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                bool hasDefinition = blocks.Has("tu");
                ObjectId definitionId = hasDefinition ? blocks["tu"] : ObjectId.Null;
                int references = 0;
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                int scanned = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (scanned >= MaxScannedObjects)
                    {
                        return Failure("模型空间对象超过 " + MaxScannedObjects + " 个扫描上限，图块整题统计不完整，已保守判定失败。");
                    }
                    scanned++;
                    BlockReference reference = transaction.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (reference != null && definitionId != ObjectId.Null && reference.BlockTableRecord == definitionId)
                    {
                        references++;
                    }
                }
                transaction.Commit();
                result.Passed = hasDefinition && references > 0;
                result.Details.Add(hasDefinition ? "已找到 tu 块定义。" : "未找到名为 tu 的块定义。");
                result.Details.Add(references > 0 ? "已找到 tu 的块参照。" : "未找到 tu 的块参照，请按题图插入。");
            }
            result.Summary = result.Passed ? "3.18 初步检查通过" : "3.18 还未完成";
            return result;
        }

        private CheckResult CheckDimensionLayer(Document document)
        {
            CheckResult result = new CheckResult();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                LayerTable layers = (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead);
                bool hasDimensionLayer = layers.Has("DIM");
                int dimensions = 0;
                BlockTable blocks = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                int scanned = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (scanned >= MaxScannedObjects)
                    {
                        return Failure("模型空间对象超过 " + MaxScannedObjects + " 个扫描上限，标注整题统计不完整，已保守判定失败。");
                    }
                    scanned++;
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity is Dimension)
                    {
                        dimensions++;
                    }
                }
                transaction.Commit();
                result.Passed = hasDimensionLayer && dimensions > 0;
                result.Details.Add(hasDimensionLayer ? "DIM 图层存在。" : "缺少 DIM 图层。");
                result.Details.Add(dimensions > 0 ? "已找到 " + dimensions + " 个尺寸对象。" : "未找到尺寸对象，请先创建标注。");
            }
            result.Summary = result.Passed ? "6.1 初步检查通过" : "6.1 还未完成";
            return result;
        }

        private CheckResult Failure(string message)
        {
            CheckResult result = new CheckResult { Passed = false, Summary = message };
            result.Details.Add(message);
            return result;
        }
    }
}
