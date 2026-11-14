# -*- coding: utf-8 -*-
"""把《CAD 中级习题册·分单元考点覆盖题》HTML 转成插件题库 JSON。

只用标准库。用法：
    python tools/extract_question_bank.py <html> <out.json> [--keep 现有JSON]

生成规则（与内容真实性有关，改之前先读）：
- WizardReady 一律 false：题库只提供“该点哪里、该敲什么”，本机没有核实过
  图形区域、尺寸和最终结果，所以不启用自动验收，也不发送任何命令。
- 面板路径、命令行、参数要点原样保留；除把行内回车符改写成“回车”、
  去掉 HTML 标签外，不改写题面文字。
- MenuPath 只在题面明确写了“菜单 X → Y”时才填，且只填经典菜单栏；
  “应用程序菜单”不是经典菜单栏，跳过。
"""
import html
import io
import json
import os
import re
import sys

CJK = re.compile(r"[\u4e00-\u9fff]")
NOISE = ("快速访问工具栏", "快速访问", "应用程序菜单", "命令行为主", "状态栏", "工具栏", "菜单")
TAIL_NOISE = ("选项卡", "对话框", "面板", "按钮")


def strip_tags(value):
    value = re.sub(r"<br\s*/?>", "\n", value)
    value = re.sub(r"<[^>]+>", "", value)
    return html.unescape(value)


def collapse(value):
    lines = []
    for line in value.replace("\u00a0", " ").split("\n"):
        line = re.sub(r"[ \t]+", " ", line).strip()
        if line:
            lines.append(line)
    return "\n".join(lines)


def to_text(value):
    return collapse(strip_tags(value or ""))


def extract_span(block, marker):
    """取出 marker 开头、span 平衡的那段内层 HTML。"""
    start = block.find(marker)
    if start < 0:
        return ""
    body_start = start + len(marker)
    depth = 1
    index = body_start
    while index < len(block):
        next_open = block.find("<span", index)
        next_close = block.find("</span>", index)
        if next_close < 0:
            return ""
        if 0 <= next_open < next_close:
            depth += 1
            index = next_open + 5
            continue
        depth -= 1
        if depth == 0:
            return block[body_start:next_close]
        index = next_close + 7
    return ""


def parse_steps(block):
    table_start = block.find('<table class="st">')
    if table_start < 0:
        return []
    table_end = block.find("</table>", table_start)
    table = block[table_start:table_end]
    steps = []
    for row in re.findall(r"<tr>(.*?)</tr>", table, re.S):
        cells = re.findall(r"<td>(.*?)</td>", row, re.S)
        if len(cells) < 3:
            continue
        order_text = to_text(cells[0])
        if not re.match(r"^\d+$", order_text):
            continue
        panel = to_text(extract_span(cells[1], '<span class="pn">'))
        command = collapse(strip_tags(
            extract_span(cells[1], '<span class="cm">').replace(
                '<span class="ent">\u21b5</span>', " \u21b5 ")))
        tip = to_text(cells[2])
        steps.append({
            "Order": int(order_text),
            "PanelRaw": panel,
            "CommandRaw": command,
            "Tip": tip,
        })
    return steps


def split_units(text):
    marks = [(m.start(), m.group(1), m.group(2)) for m in
             re.finditer(r'<h2 id="u(\d+)">([^<]+)</h2>', text)]
    units = []
    for index, (pos, number, name) in enumerate(marks):
        end = marks[index + 1][0] if index + 1 < len(marks) else len(text)
        units.append((number, to_text(name), text[pos:end]))
    return units


def table_rows(table_html):
    rows = []
    for row in re.findall(r"<tr>(.*?)</tr>", table_html, re.S):
        cells = [to_text(cell) for cell in re.findall(r"<t[dh][^>]*>(.*?)</t[dh]>", row, re.S)]
        cells = [cell for cell in cells if cell]
        if cells:
            rows.append(cells)
    return rows


def extract_spec(body):
    """题目区：段落 / 有序列表 / 表格按原顺序转成可读文本，保留全部数值。"""
    parts = []
    item_index = 0
    token = re.compile(
        r"<table[^>]*>(?P<table>.*?)</table>|<p[^>]*>(?P<p>.*?)</p>|<li[^>]*>(?P<li>.*?)</li>",
        re.S)
    for match in token.finditer(body):
        if match.group("table") is not None:
            item_index = 0
            rows = table_rows(match.group("table"))
            if not rows:
                continue
            header = rows[0]
            has_header = all(len(row) == len(header) for row in rows) and len(rows) > 1
            body_rows = rows[1:] if has_header else rows
            for row in body_rows:
                if len(row) == 2:
                    parts.append(row[0] + "\uff1a" + row[1])
                else:
                    parts.append(row[0] + "\uff1a" + " / ".join(row[1:]))
            continue
        text = to_text(match.group("p") or match.group("li"))
        if not text:
            continue
        if match.group("li") is not None:
            item_index += 1
            parts.append("(%d) " % item_index + text)
        else:
            item_index = 0
            parts.append(text)
    return "\n".join(parts)


def clean_segment(value):
    value = re.sub(r"\u300c([^\u300d]*)\u300d", r"\1", value)
    value = re.sub(r"（[^（）]*）", "", value)
    value = re.sub(r"\([^)]*\)", "", value)
    for noise in NOISE:
        value = value.replace(noise, "")
    value = value.strip(" \u3000\u00b7\u3001\uff0c\u3002\uff1b;:-\uff09)")
    for noise in TAIL_NOISE:
        if value.endswith(noise) and len(value) > len(noise) + 1:
            value = value[:-len(noise)]
    return value


# 这些片段是“接着上一步做”的指代，单独看没有意义，不能当步骤标题；
# “或/再/并”开头的是并列的备选操作，也不是本步的主动作。
TITLE_SKIP = ("\u540c\u4e0a", "\u540c\u524d", "\u6216", "\u518d", "\u5e76", "\u4e5f")
# 面板路径里的目录级词，落到它们身上等于没写标题（“绘图”不是动作，“圆”才是）。
TITLE_GENERIC = ("\u5e38\u7528", "\u7ed8\u56fe", "\u4fee\u6539", "\u6ce8\u91ca", "\u63d2\u5165", "\u683c\u5f0f",
                 "\u89c6\u56fe", "\u5de5\u5177", "\u56fe\u5c42", "\u7279\u6027", "\u6807\u6ce8", "\u5f15\u7ebf",
                 "\u6587\u5b57", "\u5b9e\u4f53\u7f16\u8f91", "\u5efa\u6a21", "\u5b9e\u7528\u5de5\u5177")


def step_title(panel, command, order):
    # 必须先去括号再切分：括号里常写“Ribbon 无直接入口；或双击多线”，
    # 直接按“；”切会切出“或双击多线）”这种半截标题。menu_path 同理。
    text = re.sub(r"（[^（）]*）", "", panel or "")
    text = re.sub(r"\([^)]*\)", "", text)
    for segment in reversed(re.split(r"[\u2192\uff1b;/]", text)):
        candidate = clean_segment(segment)
        if not candidate or candidate.startswith(TITLE_SKIP) or candidate in TITLE_GENERIC:
            continue
        # “相切、相切、半径”这种是下拉里的选项列表，不是动作名，退回上一级取工具名。
        if "\u3001" in candidate and len(candidate) > 4:
            continue
        if 1 <= len(candidate) <= 10 and CJK.search(candidate):
            return candidate
    token = re.match(r"^[-_.A-Z][A-Z0-9_.-]{1,15}", (command or "").strip())
    if token:
        return token.group(0)
    option = re.match(r"^[A-Z]\s", (command or "").strip())
    if option:
        return "\u7eed\u5199\u4e0a\u4e00\u547d\u4ee4"
    return "\u7b2c %d \u6b65" % order


def resolve_panel(panel, previous):
    """把「同上 / 同上对话框 →…」补成完整路径。

    零基础用户看不出“同上”是哪一步，面板上只显示“同上对话框 →「应用」”
    等于没写。这里把上一步的面板路径接上去，只做字符串拼接，不改题面。
    """
    text = (panel or "").strip()
    if not text or not previous:
        return text
    if not (text.startswith("\u540c\u4e0a") or text.startswith("\u540c\u524d")):
        return text
    rest = text[2:]
    rest = re.sub(r"^[\s\u3000]*(对话框|面板|窗口|的)", "", rest)
    rest = re.sub(r"^[\s\u3000\u2192]+", "", rest).strip()
    base = re.sub(r"（[^（）]*）", "", previous).strip()
    base = base.strip("\u3002\uff1b; ")
    if not rest:
        return base
    return base + " \u2192 " + rest


# 菜单路径里混进对话框按钮就指不到：Win32 菜单树里没有“应用/确定”这些项，
# 右键会报“菜单里没找到”，比不指更糟。只留真正的菜单项。
DIALOG_BUTTONS = ("\u5e94\u7528", "\u786e\u5b9a", "\u53d6\u6d88", "\u5173\u95ed", "\u6d4f\u89c8",
                  "\u65b0\u5efa", "\u5220\u9664", "\u63d2\u5165", "\u9009\u9879", "\u8bbe\u7f6e")


def menu_path(panel):
    # 括号里通常是“Ribbon 无独立按钮”或“或 菜单 …”这类补充说明；
    # 先整体去掉，避免括号内的分号把路径截断成半截。
    text = re.sub(r"（[^（）]*）", "", panel or "")
    text = re.sub(r"（.*$", "", text)
    match = re.search(r"(?<!\u5e94\u7528\u7a0b\u5e8f)\u83dc\u5355\s*([^\uff1b;]+)", text)
    if not match:
        return ""
    raw = match.group(1)
    if "\u2192" not in raw:
        return ""
    segments = []
    for segment in raw.split("\u2192"):
        text_part = clean_segment(segment)
        if not text_part or len(text_part) > 10 or "Ribbon" in text_part:
            break
        if text_part in DIALOG_BUTTONS:
            break
        segments.append(text_part)
        if len(segments) == 3:
            break
    if len(segments) < 2:
        return ""
    return ">".join(segments)


def action_for(command):
    head = re.match(r"^[-_.]*([A-Za-z]+)", (command or "").strip())
    if not head:
        return "manual"
    name = head.group(1).upper()
    if name in ("SAVEAS", "QSAVE", "SAVE"):
        return "save"
    return "command"


def build_question(raw):
    unit = re.sub(r"\s+", " ", raw["UnitName"]).strip()
    meta = raw["Meta"]
    output = ""
    match = re.search(r"产出\s*([^\s\u00b7\uff0c]+)", meta)
    if match:
        output = match.group(1)
    if not output:
        match = re.search(r"([A-Za-z0-9_-]+\.(?:dwt|dwg|DWT|DWG))", raw["Title"])
        if match:
            output = match.group(1)

    points = []
    for chip in raw["Chips"]:
        text = re.sub(r"^\d+-\d+\s*", "", chip).strip()
        if text:
            points.append(text)

    steps = []
    order = 0
    spec = raw.get("Spec") or ""
    if spec.strip():
        order = 1
        # 这些是自拟题，没有起始 DWG；不提醒新建，用户很可能在旧图上直接动手。
        spec = "\u52a8\u624b\u524d\u5148\u65b0\u5efa\u4e00\u5f20\u7a7a\u767d\u56fe\uff08Ctrl+N\uff0c\u6216\u5feb\u901f\u8bbf\u95ee\u3010\u65b0\u5efa\u3011\uff09\uff0c\u52a9\u624b\u4e0d\u66ff\u4f60\u5efa\u6587\u4ef6\u3002\n" + spec
        steps.append({
            "Order": order,
        "Title": "\u9898\u76ee\u8981\u6c42",
        "Instruction": "\u5148\u8bfb\u4e00\u904d\u9898\u76ee\u8981\u6c42\uff0c\u518d\u6309\u4e0b\u4e00\u6b65\u5f00\u59cb\u3002",
        "Command": "",
        "InputText": "\uff08\u672c\u6b65\u4e0d\u9700\u8981\u8f93\u5165\u547d\u4ee4\uff0c\u5148\u770b\u9898\u76ee\u8981\u6c42\uff09",
        "PanelPath": "",
        "Tip": spec,
        "ScreenHint": "",
        "Hint": "",
        "HintTarget": "none",
        "Action": "manual",
        "AutoAdvance": False,
        "CheckTypes": ["manual"],
        "MenuPath": "",
        "CommandScript": "",
    })

    previous_panel = ""
    for step in raw["Steps"]:
        order += 1
        command = step["CommandRaw"].replace("\u21b5", "\u56de\u8f66")
        command = collapse(command)
        panel = resolve_panel(step["PanelRaw"], previous_panel)
        previous_panel = panel or previous_panel
        title = step_title(panel, command, step["Order"])
        action = action_for(command)
        instruction = step["Tip"] or (panel or command)
        steps.append({
            "Order": order,
            "Title": title,
            "Instruction": instruction,
            "Command": command,
            "InputText": command or "\uff08\u672c\u6b65\u4ee5\u754c\u9762\u64cd\u4f5c\u4e3a\u4e3b\uff0c\u6309\u9762\u677f\u8def\u5f84\u64cd\u4f5c\uff09",
            "PanelPath": panel,
            "Tip": step["Tip"].replace("\u21b5", "\u56de\u8f66"),
            # 面板第三行已经把面板路径和要点显示出来了，这两项再各写一遍只会在
            # 命令行里刷屏，所以留空；BuildCoachText 会跳过空项。
            "ScreenHint": "",
            "Hint": "",
            "HintTarget": "none",
            "Action": action,
            "AutoAdvance": False,
            # 手动步骤才是“人工确认”；命令步骤没有验收规则，写 manual 会让人
            # 以为它有检查，实际检查器只认 WizardReady 的规则。
            "CheckTypes": ["manual"] if action == "manual" else [],
            "MenuPath": menu_path(panel),
            "CommandScript": "",
        })

    if raw["Verify"] or raw["Pitfall"]:
        order += 1
        tail = []
        if raw["Verify"]:
            tail.append("\u81ea\u67e5\uff1a" + re.sub(r"^[\u2460-\u2469\u2460]?\s*\u6821\u9a8c\u3000?", "", raw["Verify"]))
        if raw["Pitfall"]:
            tail.append("\u6613\u9519\uff1a" + re.sub(r"^\u6613\u9519\u3000?", "", raw["Pitfall"]))
        steps.append({
            "Order": order,
            "Title": "\u505a\u5b8c\u540e\u81ea\u67e5",
            "Instruction": "\u5bf9\u7167\u4e0b\u9762\u7684\u81ea\u67e5\u4e0e\u6613\u9519\u9879\u76ee\u5df2\u5b8c\u6210\u7684\u56fe\u5f62\u3002",
            "Command": "",
            "InputText": "\uff08\u672c\u6b65\u4e0d\u9700\u8981\u8f93\u5165\u547d\u4ee4\uff0c\u5bf9\u7167\u81ea\u67e5\uff09",
            "PanelPath": "",
            "Tip": "\n\n".join(tail),
            "ScreenHint": "",
            "Hint": "\u8fd9\u4e9b\u662f\u9898\u5e93\u7ed9\u7684\u81ea\u67e5\u65b9\u6cd5\uff0c\u9700\u8981\u4f60\u81ea\u5df1\u76ee\u89c6\u786e\u8ba4\uff0c\u52a9\u624b\u4e0d\u5e26\u4f60\u9a8c\u6536\u3002",
            "HintTarget": "none",
            "Action": "manual",
            "AutoAdvance": False,
            "CheckTypes": ["manual"],
            "MenuPath": "",
            "CommandScript": "",
        })

    summary = raw["Summary"] or raw["Title"]
    return {
        "Id": raw["Id"],
        "Unit": unit,
        "Title": raw["Title"],
        "Difficulty": "\u4e2d\u7ea7",
        "SourceFile": "",
        "ExpectedOutput": output,
        "Summary": summary + "\uff08\u9898\u5e93\u81ea\u62df\u9898\uff1b\u6b65\u9aa4\u4e0e\u9762\u677f\u8def\u5f84\u53d6\u81ea\u9898\u5e93\u539f\u6587\uff0c\u672a\u5728\u672c\u673a\u6838\u5b9e\uff0c\u4e0d\u81ea\u52a8\u9a8c\u6536\u3002\uff09",
        "WizardReady": False,
        "KnowledgePoints": points,
        "Steps": steps,
        "Checks": [{
            "Type": "manual",
            "Target": "geometry",
            "Expected": "pending",
            "Tolerance": 0,
            "Message": "\u672c\u9898\u5c1a\u672a\u5728\u672c\u673a\u6838\u5b9e\u56fe\u5f62\u533a\u57df\u4e0e\u5c3a\u5bf8\uff0c\u4e0d\u505a\u81ea\u52a8\u9a8c\u6536\u3002",
        }],
    }


def extract_spec_text(raw):
    return raw["Spec"] if "Spec" in raw else ""


def parse_questions(text):
    units = split_units(text)
    questions = []
    for number, unit_name, chunk in units:
        starts = [m.start() for m in re.finditer(r'<div class="pt">', chunk)]
        ends = starts[1:] + [len(chunk)]
        for start, end in zip(starts, ends):
            block = chunk[start:end]
            title_match = re.search(
                r'<h3 class="tt"><span class="no">\u9898\s*([^<]+)</span>(.*?)</h3>', block, re.S)
            if not title_match:
                continue
            question_id = to_text(title_match.group(1))
            title = to_text(title_match.group(2))
            meta = to_text(extract_span(block, '<span class="meta2">'))
            chips = [to_text(c) for c in re.findall(r'<span class="chip">(.*?)</span>', block, re.S)]
            steps = parse_steps(block)

            body_start = block.find('<span class="lab">\u9898\u76ee</span>')
            body_end = block.find('<span class="lab">\u89e3\u7b54\u6b65\u9aa4</span>')
            body = block[body_start:body_end] if body_start >= 0 and body_end > body_start else ""
            first_para = re.search(r"<p[^>]*>(.*?)</p>", body, re.S)
            summary = to_text(first_para.group(1)) if first_para else ""
            verify = ""
            verify_match = re.search(r'<div class="verbox">(.*?)</div>', block, re.S)
            if verify_match:
                verify = to_text(verify_match.group(1))
            pitfall = ""
            pitfall_match = re.search(r'<div class="warnbox">(.*?)</div>', block, re.S)
            if pitfall_match:
                pitfall = to_text(pitfall_match.group(1))

            questions.append({
                "UnitNumber": number,
                "UnitName": unit_name,
                "Id": question_id,
                "Title": title,
                "Meta": meta,
                "Chips": chips,
                "Summary": summary,
                "Spec": extract_spec(body),
                "Verify": verify,
                "Pitfall": pitfall,
                "Steps": steps,
            })
    return questions


def main():
    if len(sys.argv) < 3:
        print("usage: extract_question_bank.py <html> <out.json> [--keep existing.json]")
        return 2
    source, target = sys.argv[1], sys.argv[2]
    keep_path = None
    if "--keep" in sys.argv:
        keep_path = sys.argv[sys.argv.index("--keep") + 1]

    with io.open(source, "r", encoding="utf-8") as handle:
        text = handle.read()
    raw_questions = parse_questions(text)
    new_questions = [build_question(raw) for raw in raw_questions]

    kept = []
    if keep_path and os.path.exists(keep_path):
        with io.open(keep_path, "r", encoding="utf-8") as handle:
            existing = json.load(handle)
        new_ids = set(question["Id"] for question in new_questions)
        kept = [question for question in existing.get("Questions", [])
                if question.get("Id") not in new_ids]
        print("kept legacy questions:", [q.get("Id") for q in kept])

    payload = {
        "SchemaVersion": "1.0",
        "Questions": new_questions + kept,
    }
    with io.open(target, "w", encoding="utf-8") as handle:
        handle.write(json.dumps(payload, ensure_ascii=False, indent=2))

    total_steps = sum(len(question["Steps"]) for question in new_questions)
    empty_panel = sum(1 for q in new_questions for s in q["Steps"] if not s["PanelPath"])
    empty_cmd = sum(1 for q in new_questions for s in q["Steps"] if not s["Command"])
    menus = sum(1 for q in new_questions for s in q["Steps"] if s["MenuPath"])
    print("questions=%d steps=%d noPanel=%d noCommand=%d menuPath=%d"
          % (len(new_questions), total_steps, empty_panel, empty_cmd, menus))
    print("written:", target)
    return 0


if __name__ == "__main__":
    sys.exit(main())
