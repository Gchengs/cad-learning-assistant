using System.Collections.Generic;
using System.Runtime.Serialization;

namespace CadLearningAssistant
{
    // 显式使用 DataContract/DataMember 固定题库 JSON 的契约及字段名。
    // DataContractJsonSerializer 也支持符合条件的未标注 POCO，并非必须标注才能序列化。
    [DataContract]
    public class QuestionBankFile
    {
        [DataMember(Name = "SchemaVersion")]
        public string SchemaVersion { get; set; }

        [DataMember(Name = "Questions")]
        public List<QuestionDefinition> Questions { get; set; }
    }

    [DataContract]
    public class QuestionDefinition
    {
        [DataMember(Name = "Id")]
        public string Id { get; set; }

        [DataMember(Name = "Unit")]
        public string Unit { get; set; }

        [DataMember(Name = "Title")]
        public string Title { get; set; }

        [DataMember(Name = "Difficulty")]
        public string Difficulty { get; set; }

        [DataMember(Name = "SourceFile")]
        public string SourceFile { get; set; }

        [DataMember(Name = "ExpectedOutput")]
        public string ExpectedOutput { get; set; }

        [DataMember(Name = "Summary")]
        public string Summary { get; set; }

        // 表示已核实所声明的基础操作链路和检查契约，允许教学试用；不等于完整题图已核实。
        // 未就绪题目仍可查看预览资料，但不能自动带做；区域、边界等未核实项须在提示中明确。
        [DataMember(Name = "WizardReady")]
        public bool WizardReady { get; set; }

        [DataMember(Name = "KnowledgePoints")]
        public List<string> KnowledgePoints { get; set; }

        [DataMember(Name = "Steps")]
        public List<LearningStep> Steps { get; set; }

        [DataMember(Name = "Checks")]
        public List<CheckRule> Checks { get; set; }
    }

    [DataContract]
    public class LearningStep
    {
        [DataMember(Name = "Order")]
        public int Order { get; set; }

        [DataMember(Name = "Title")]
        public string Title { get; set; }

        [DataMember(Name = "Instruction")]
        public string Instruction { get; set; }

        [DataMember(Name = "Command")]
        public string Command { get; set; }

        [DataMember(Name = "Hint")]
        public string Hint { get; set; }

        // 可选的教学定位目标：overview、geometry、hatch、block:tu、dimension、none。
        // 仅用于显示提示，不参与评分，也不代表题目的官方答案。
        [DataMember(Name = "HintTarget")]
        public string HintTarget { get; set; }

        // 向导动作类型：open、command、input、click、select、save、manual。
        [DataMember(Name = "Action")]
        public string Action { get; set; }

        // 完整短命令及必要的手工操作提示，不跨步骤停留在命令中间状态；不代替用户操作。
        // WizardReady 题中仅 open 允许为空；open 由唯一主按钮准备独立练习副本。
        [DataMember(Name = "InputText")]
        public string InputText { get; set; }

        // WizardReady 题必填：浮层仅指命令行大致区域，不代表精确 UI 按钮或图形内部点。
        // 点击区域未核实须明确提醒，不能把包围盒中心当作可靠内部点。
        [DataMember(Name = "ScreenHint")]
        public string ScreenHint { get; set; }

        // 题库原文的界面路径，例如「常用 → 绘图 → 矩形」。零基础用户先要知道点哪里，
        // 所以这一项与命令行分开存，不混进 Command。空表示本步没有面板入口。
        [DataMember(Name = "PanelPath")]
        public string PanelPath { get; set; }

        // 题库原文的参数与要点（填什么值、容易错在哪）。空表示没有补充说明。
        // 与 Instruction 分开：Instruction 是给校验器看的契约，Tip 是给人看的题面细节。
        [DataMember(Name = "Tip")]
        public string Tip { get; set; }

        // 可选：真实菜单显示文字路径，使用 > 分隔；只定位实际可见且唯一匹配的菜单项。
        [DataMember(Name = "MenuPath")]
        public string MenuPath { get; set; }

        // 可选：可一键送进 AutoCAD 命令行的纯命令序列，用 \n 分隔，末尾留空行结束命令。
        // 属性步骤必须匹配安全白名单；交互步骤最多只启动核实过的命令，
        // 后续点选、属性和保存路径由用户操作，不能跨提示自动应答。
        [DataMember(Name = "CommandScript")]
        public string CommandScript { get; set; }

        // 兼容旧题库字段；当前必须为 false。几何内部点不能证明是题图要求的区域，
        // 加载器拒绝 true，运行流程不猜点、不代点。
        [DataMember(Name = "AutoPickPoint")]
        public bool AutoPickPoint { get; set; }

        // 仅在完整命令结束且检查通过后自动前进；manual 动作、manual 类型或无规则均不可自动前进。
        // 默认值为 false，避免未配置的步骤被误跳过。
        [DataMember(Name = "AutoAdvance")]
        public bool AutoAdvance { get; set; }

        // WizardReady 题至少声明一种类型；practice 只能独立用于 open，由插件核验练习副本。
        // 状态类型：layer-exists/layer-red/current-layer/hpname/hpscale/hpang/hplayer/cecolor/hatch-on-layer/saved。
        // 选规则类型：layer/entity/pattern/color/scale/block/placement，必须在题目 Checks 中有同类型规则。
        // manual 仅人工确认；空列表不代表通过。未知 Target 的语义由检查器处理，加载器不硬编码目标。
        [DataMember(Name = "CheckTypes")]
        public List<string> CheckTypes { get; set; }
    }

    [DataContract]
    public class CheckRule
    {
        [DataMember(Name = "Type")]
        public string Type { get; set; }

        [DataMember(Name = "Target")]
        public string Target { get; set; }

        [DataMember(Name = "Expected")]
        public string Expected { get; set; }

        [DataMember(Name = "Tolerance")]
        public double Tolerance { get; set; }

        [DataMember(Name = "Message")]
        public string Message { get; set; }
    }

    public class CheckResult
    {
        public bool Passed { get; set; }
        public bool RequiresManual { get; set; }
        public string Summary { get; set; }
        public List<string> Details { get; private set; }

        public CheckResult()
        {
            Details = new List<string>();
        }
    }
}
