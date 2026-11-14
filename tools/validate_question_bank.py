# -*- coding: utf-8 -*-
"""按 C# 加载器 Infrastructure/QuestionBank.cs 的 Validate/ValidateWizard 复刻校验题库。

为什么要有这个脚本：QuestionBank 的规则是“任一处不合规 → 整库拒载”，
一条字段写错，面板就只剩“题库加载失败”。C# 侧没法在沙箱里编译时，
至少先用 Python 把同一套规则跑一遍。

用法：
    python tools/validate_question_bank.py src/CadLearningAssistant/content/questions.json
"""
import io
import json
import sys

# 与 QuestionBank.ValidateWizard 的集合逐字一致，改这里必须同步改 C#。
ACTIONS = {"open", "command", "input", "click", "select", "save", "manual"}
CHECK_TYPES = {
    "practice", "layer-exists", "layer-red", "current-layer", "hpname", "hpscale",
    "hpang", "hplayer", "cecolor", "hatch-on-layer", "saved", "layer", "entity",
    "pattern", "color", "scale", "block", "placement", "manual",
}
RULE_TYPES = {"layer", "entity", "pattern", "color", "scale", "block", "placement"}

# 与 ValidateAutomation / Plugin.RunStepScript 的白名单一致。
SCRIPT_BY_ACTION = {"click": "_.-HATCH\n", "select": "_.CHPROP\n", "save": "_.SAVEAS\n"}
SCRIPT_BY_CHECK = {
    "layer-exists": "_.-LAYER\n_New\nHATCH\n\n",
    "layer-red": "_.-LAYER\n_Color\n1\nHATCH\n\n",
    "current-layer": "CLAYER\nHATCH\n",
    "hpname": "HPNAME\nANSI31\n",
    "hpscale": "HPSCALE\n1\n",
    "hpang": "HPANG\n0\n",
    "hplayer": "HPLAYER\n.\n",
    "cecolor": "CECOLOR\n7\n",
}


def squash(value):
    return "".join(ch for ch in (value or "") if not ch.isspace())


def check_menu_path(value):
    if not value:
        return True
    if any(ord(ch) < 32 for ch in value):
        return False
    return all(part.strip() for part in value.split(">"))


def validate_automation(question, step, where):
    if step.get("AutoPickPoint"):
        return where + ".AutoPickPoint 不能为 true"
    script = (step.get("CommandScript") or "").replace("\r\n", "\n")
    if not script:
        return None
    action = step.get("Action") or ""
    if action in SCRIPT_BY_ACTION:
        if script == SCRIPT_BY_ACTION[action]:
            return None
        return where + ".CommandScript 只能启动本动作的固定命令"
    types = step.get("CheckTypes") or []
    key = types[0] if len(types) == 1 else ""
    if action not in ("command", "input") or question.get("Id") != "3.1":
        return where + ".CommandScript 只允许出现在已核实的 3.1 题"
    if SCRIPT_BY_CHECK.get(key) != script:
        return where + ".CommandScript 不在单一检查类型的脚本白名单中"
    return None


def validate_wizard(question, where):
    configured = set()
    for index, rule in enumerate(question.get("Checks") or []):
        rule_type = (rule.get("Type") or "").strip().lower()
        if rule_type not in RULE_TYPES and rule_type != "manual":
            return "%s.Checks[%d].Type 不在整题规则白名单中" % (where, index)
        configured.add(rule_type)

    source = question.get("SourceFile") or ""
    output = question.get("ExpectedOutput") or ""
    if (not source.strip() or not output.strip()
            or "/" in output or "\\" in output or ":" in output):
        return where + "：向导必须配置 SourceFile 和仅含文件名的 ExpectedOutput"

    steps = question["Steps"]
    if (steps[0].get("Action") or "") != "open":
        return where + ".Steps[0].Action 必须为 open"

    for index, step in enumerate(steps):
        loc = "%s.Steps[%d]" % (where, index)
        if step.get("Order") != index + 1:
            return loc + ".Order 必须按列表顺序从 1 连续递增"
        action = step.get("Action") or ""
        if action not in ACTIONS:
            return loc + ".Action 不在白名单中"
        if action != "open" and not (step.get("InputText") or "").strip():
            return loc + ".InputText 不能为空（仅 open 允许为空）"
        if not (step.get("ScreenHint") or "").strip():
            return loc + ".ScreenHint 不能为空"
        if not (step.get("Hint") or "").strip():
            return loc + ".Hint 不能为空"
        types = step.get("CheckTypes") or []
        if not types:
            return loc + ".CheckTypes 至少需要一个检查类型"
        if action == "manual" or "manual" in types:
            return loc + "：就绪向导不支持人工确认步骤"
        for type_index, raw in enumerate(types):
            value = (raw or "").strip().lower()
            type_loc = "%s.CheckTypes[%d]" % (loc, type_index)
            if value not in CHECK_TYPES:
                return type_loc + " 不在检查类型白名单中"
            if value == "practice" and (len(types) != 1 or action != "open"):
                return type_loc + "：practice 只能独立声明且对应 open"
            if value in RULE_TYPES and value not in configured:
                return type_loc + "：必须在 question.Checks 中有同类型规则"
    return None


def validate(payload):
    errors = []
    if payload is None:
        return ["题库根对象为 null"]
    if payload.get("SchemaVersion") != "1.0":
        errors.append("SchemaVersion 必须为 1.0，实际：%r" % payload.get("SchemaVersion"))
        return errors
    questions = payload.get("Questions") or []
    if not questions:
        return ["Questions 不能为空"]

    seen = {}
    for q_index, question in enumerate(questions):
        where = "Questions[%d]" % q_index
        if not isinstance(question, dict):
            errors.append(where + " 不是对象")
            continue
        qid = question.get("Id")
        if not qid or not qid.strip():
            errors.append(where + ".Id 不能为空")
            continue
        where += "（%s）" % qid
        key = squash(qid).lower()
        if key in seen:
            errors.append("%s.Id 与 Questions[%d] 重复" % (where, seen[key]))
        seen[key] = q_index
        if not (question.get("Title") or "").strip():
            errors.append(where + ".Title 不能为空")
        steps = question.get("Steps") or []
        if not steps:
            errors.append(where + ".Steps 不能为空")
            continue

        orders = {}
        for s_index, step in enumerate(steps):
            loc = "%s.Steps[%d]" % (where, s_index)
            order = step.get("Order")
            if not isinstance(order, int) or order <= 0:
                errors.append(loc + ".Order 必须为正整数")
            elif order in orders:
                errors.append(loc + ".Order 与 Steps[%d] 重复" % orders[order])
            else:
                orders[order] = s_index
            if not (step.get("Title") or "").strip():
                errors.append(loc + ".Title 不能为空")
            if not (step.get("Instruction") or "").strip():
                errors.append(loc + ".Instruction 不能为空")
            for c_index, value in enumerate(step.get("CheckTypes") or []):
                if not (value or "").strip():
                    errors.append("%s.CheckTypes[%d] 为空白" % (loc, c_index))
            if not check_menu_path(step.get("MenuPath")):
                errors.append(loc + ".MenuPath 格式不合规：%r" % step.get("MenuPath"))
            error = validate_automation(question, step, loc)
            if error:
                errors.append(error)
            if question.get("WizardReady") and not (step.get("Action") or "").strip():
                errors.append(loc + ".Action 在 WizardReady 题中不能为空")
            if question.get("WizardReady") and step.get("Order") != s_index + 1:
                pass  # 由 validate_wizard 统一报，避免重复

        for c_index, rule in enumerate(question.get("Checks") or []):
            if not isinstance(rule, dict):
                errors.append("%s.Checks[%d] 不是对象" % (where, c_index))

        if question.get("WizardReady"):
            error = validate_wizard(question, where)
            if error:
                errors.append(error)
    return errors


def main():
    if len(sys.argv) < 2:
        print("usage: validate_question_bank.py <questions.json>")
        return 2
    path = sys.argv[1]
    with io.open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)

    errors = validate(payload)
    questions = payload.get("Questions") or []
    steps = sum(len(q.get("Steps") or []) for q in questions)
    ready = [q.get("Id") for q in questions if q.get("WizardReady")]
    print("questions=%d steps=%d wizardReady=%s" % (len(questions), steps, ready))
    if errors:
        print("FAIL %d 项：" % len(errors))
        for error in errors[:40]:
            print("  - " + error)
        return 1
    print("OK：题库符合 QuestionBank.cs 的加载规则（加载器会接收整库）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
