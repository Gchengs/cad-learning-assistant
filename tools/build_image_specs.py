# -*- coding: utf-8 -*-
"""按题面尺寸生成「最终结果示意图」的矢量描述（specs.json）。

这些图不是原始标准答案图：
  - 3.1 / 3.18 / 4.1 / 6.1 由 accoreconsole 直接把原 DWG 渲成 PNG（真图，见 tools/render_dwg.ps1）；
  - 其余 22 道题是题库自拟题，题面只给文字和坐标，这里按题面尺寸画出示意图形，
    目的是让人看出“做完应该长什么样”，不追求与标准答案逐线一致。
画法统一：黑色实线=可见轮廓，红色虚线=中心线/轴线，绿色=尺寸与技术要求，灰色=图框/辅助线。

用法： python tools/build_image_specs.py tools/image_specs.json
"""

import io
import json
import math
import sys

BLACK = '#1a1a1a'
RED = '#c0392b'
GREEN = '#127a3d'
GRAY = '#8a8a8a'
BLUE = '#1f4e79'
CYAN = '#008b8b'
MAGENTA = '#a020a0'
ORANGE = '#c07000'


def L(x1, y1, x2, y2, c=BLACK, w=2, dash=False):
    p = {"t": "line", "p": [x1, y1, x2, y2], "c": c, "w": w}
    if dash:
        p["dash"] = True
    return p


def R(x, y, w, h, c=BLACK, lw=2, dash=False):
    p = {"t": "rect", "x": x, "y": y, "w": w, "h": h, "c": c, "lw": lw}
    if dash:
        p["dash"] = True
    return p


def C(x, y, r, c=BLACK, w=2):
    return {"t": "circ", "x": x, "y": y, "r": r, "c": c, "w": w}


def P(pts, c=BLACK, w=2, close=False, dash=False):
    p = {"t": "poly", "p": [[round(a, 3), round(b, 3)] for a, b in pts], "c": c, "w": w}
    if close:
        p["close"] = True
    if dash:
        p["dash"] = True
    return p


def arcp(cx, cy, r, a0, a1, n=28):
    """从 a0 到 a1（度，逆时针）采样圆弧，返回折线点。"""
    if a1 < a0:
        a1 += 360
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]


def A(cx, cy, r, a0, a1, c=BLACK, w=2):
    return P(arcp(cx, cy, r, a0, a1), c, w)


def H(pts, gap=6, c=BLACK):
    """剖面线：多边形 + 线距（图形单位），渲染端会用裁剪区裁在多边形内。"""
    return {"t": "hatch", "p": [[round(a, 3), round(b, 3)] for a, b in pts], "gap": gap, "c": c}


# 文字：这里的 size 是“设计字号”，渲染端按像素落地，*2.2 让 10 号约等于 22px。
TEXT_SCALE = 2.2


def T(x, y, s, c='#444444', size=10, align=None):
    p = {"t": "text", "x": x, "y": y, "s": s, "c": c, "size": round(size * TEXT_SCALE, 1)}
    if align:
        p["align"] = align
    return p


def DIM(x1, y1, x2, y2, off, txt):
    return {"t": "dim", "x1": x1, "y1": y1, "x2": x2, "y2": y2, "off": off, "txt": txt}


def cross(x, y, ext, c=RED):
    return [L(x - ext, y, x + ext, y, c, 1, True), L(x, y - ext, x, y + ext, c, 1, True)]


def iso(x, y, z):
    return (0.866 * (x - y), 0.5 * (x + y) + z)


def box(x, y, z, w, d, h):
    out = []
    for dz in (0, h):
        a = iso(x, y, z + dz)
        b = iso(x + w, y, z + dz)
        c = iso(x + w, y + d, z + dz)
        e = iso(x, y + d, z + dz)
        out += [L(a[0], a[1], b[0], b[1]), L(b[0], b[1], c[0], c[1]),
                L(c[0], c[1], e[0], e[1]), L(e[0], e[1], a[0], a[1])]
    for (px, py) in ((x, y), (x + w, y), (x + w, y + d), (x, y + d)):
        a = iso(px, py, z)
        b = iso(px, py, z + h)
        out.append(L(a[0], a[1], b[0], b[1]))
    return out


def iso_circle(cx, cy, cz, r, plane='xy', n=36):
    pts = []
    for i in range(n + 1):
        t = 2 * math.pi * i / n
        if plane == 'xy':
            pts.append(iso(cx + r * math.cos(t), cy + r * math.sin(t), cz))
        elif plane == 'xz':
            pts.append(iso(cx + r * math.cos(t), cy, cz + r * math.sin(t)))
        else:
            pts.append(iso(cx, cy + r * math.cos(t), cz + r * math.sin(t)))
    return P(pts)


def rounded_rect(x, y, w, h, r, c=BLACK, corners=(0, 1, 2, 3)):
    """按逆时针顺序拼圆角矩形：0=左下 1=右下 2=右上 3=左上。"""
    pts = []
    segs = [
        ((x + r, y + r), 180, 270, 0),
        ((x + w - r, y + r), 270, 360, 1),
        ((x + w - r, y + h - r), 0, 90, 2),
        ((x + r, y + h - r), 90, 180, 3),
    ]
    for (cx, cy), a0, a1, idx in segs:
        if idx in corners:
            pts += arcp(cx, cy, r, a0, a1, 8)
        else:
            pts += [(cx + r * math.cos(math.radians(a0)), cy + r * math.sin(math.radians(a0))),
                    (cx + r * math.cos(math.radians(a1)), cy + r * math.sin(math.radians(a1)))]
    return P(pts, c, 2, True)


def circumcircle(p1, p2, p3):
    ax, ay = p1
    bx, by = p2
    cx, cy = p3
    d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by))
    ux = ((ax ** 2 + ay ** 2) * (by - cy) + (bx ** 2 + by ** 2) * (cy - ay) + (cx ** 2 + cy ** 2) * (ay - by)) / d
    uy = ((ax ** 2 + ay ** 2) * (cx - bx) + (bx ** 2 + by ** 2) * (ax - cx) + (cx ** 2 + cy ** 2) * (bx - ax)) / d
    return (ux, uy, math.hypot(ax - ux, ay - uy))


TITLES = {
    '1-A': '1-A  A3 机械样板（图框 + 标题栏 + 5 个图层）',
    '1-B': '1-B  环境检验图',
    '2-A': '2-A  七种图元盘',
    '2-B': '2-B  曲线 / 变宽线 / 等分点',
    '2-C': '2-C  多线墙体与剖面填充',
    '3-A': '3-A  待整理的「乱图」',
    '3-B': '3-B  粗糙度属性块 CCD',
    '3-C': '3-C  筛选与填充',
    '4-A': '4-A  带槽支座',
    '4-B': '4-B  孔组阵列',
    '4-C': '4-C  参照编辑与面域',
    '5-A': '5-A  对称端盖',
    '5-B': '5-B  摇臂',
    '5-C': '5-C  楼梯剖面',
    '6-A': '6-A  平板件',
    '6-B': '6-B  公差 / 基准 / 粗糙度',
    '6-C': '6-C  三开间平面',
    '7-A': '7-A  支座（三维）',
    '7-B': '7-B  带轮（三维）',
    '7-C': '7-C  V 形楔块（三维）',
    '8-A': '8-A  支架零件图',
    '8-B': '8-B  法兰盘',
}


BOUNDS = {
    '3-B': [-8, -26, 66, 32],
    '5-B': [-130, -100, 130, 70],
    '1-B': [0, -10, 360, 180],
    '4-A': [-130, -46, 150, 150],
}


def img(qid, caption, prims):
    item = {"id": qid, "title": TITLES.get(qid, qid), "caption": caption,
            "prims": [p for p in prims if p]}
    if qid in BOUNDS:
        item["bounds"] = BOUNDS[qid]
    return item


IMAGES = []


def q1a():
    pr = [R(0, 0, 420, 297, GRAY, 1, True), R(25, 5, 385, 287, BLACK, 2)]
    # 标题栏：180×56，左列放比例/图号，右列放图名（文字一律居中，避免溢出框外）
    pr.append(R(230, 5, 180, 56, BLACK, 2))
    pr += [L(230, 25, 410, 25, BLACK, 1), L(230, 41, 410, 41, BLACK, 1), L(275, 5, 275, 61, BLACK, 1)]
    pr += [T(252, 51, 'A3', '#333333', 7, 'c'), T(252, 33, '1:1', '#333333', 7, 'c'), T(252, 15, 'GB', '#333333', 7, 'c'),
           T(342, 51, 'KSCAD1-A.dwt', BLUE, 7.5, 'c'), T(342, 33, '机械样板图', '#333333', 7.5, 'c'),
           T(342, 15, '第三角投影', '#333333', 7, 'c')]
    # 图例：五个图层用真实颜色的小色块 + 名称
    sw = [('01 粗实线', BLACK), ('02 细实线', '#777777'), ('03 中心线', RED), ('04 虚线', '#c9a227'), ('05 标注', '#2e8b57')]
    for i, (name, col) in enumerate(sw):
        x = 22 + i * 80
        pr += [R(x, -74, 34, 15, col, 4), T(x + 17, -100, name, '#444444', 8.5, 'c')]
    pr += [T(210, -40, '图形界限 420 × 297（A3 横放）', GREEN, 9, 'c'),
           T(210, -126, '图层 5 个 · 线型 CENTER / DASHED · LTSCALE = 3 · 工程字 · GB 标注样式', '#444444', 9, 'c')]
    return img('1-A', 'A3 样板做完的样子：图框 + 标题栏 + 5 个图层（示意图，非标准答案图）', pr)


def q1b():
    pr = [L(50, 50, 250, 50), L(250, 50, 250, 150)]
    pr += [L(50, 50, 50 + 80 * math.cos(math.radians(30)), 50 + 80 * math.sin(math.radians(30)), BLUE, 2)]
    pr += [L(50, 50, 50 + 60 * math.cos(math.radians(120)), 50 + 60 * math.sin(math.radians(120)), ORANGE, 2)]
    pr += [L(290, 50, 340, 50), L(250, 50, 290, 50, GRAY, 1, True)]
    pr += [C(200, 120, 30)] + cross(200, 120, 40)
    pr += [T(150, 20, '200', GREEN, 11), T(160, 80, '100', GREEN, 11), T(78, 82, '30°', BLUE, 11),
           T(10, 106, '@60<120', ORANGE, 10), T(300, 62, '40 + 50', GREEN, 10), T(200, 164, 'Φ60', GREEN, 11, 'c')]
    return img('1-B', '检验图：水平 + 竖直 + 极轴 30° + 动态输入 + 追踪 + 中心线', pr)


def q2a():
    """七组图元改成 4×2 网格摆放：题面坐标会让它们互相压住，网格才看得清每组是什么。"""
    pr = []
    cx = [80, 240, 400, 560]
    cy = [200, 60]
    # A 圆角矩形
    pr += [rounded_rect(cx[0] - 55, cy[0] - 32, 110, 64, 12), T(cx[0], cy[0] - 52, 'A 圆角矩形 R12', '#444444', 10, 'c')]
    # B 正六边形 边长 30
    hexp = [(cx[1] - 30, cy[0] - 26)]
    px, py, ang = cx[1] - 30, cy[0] - 26, 0
    for _ in range(5):
        ang += 60
        px += 30 * math.cos(math.radians(ang))
        py += 30 * math.sin(math.radians(ang))
        hexp.append((px, py))
    pr += [P(hexp, BLACK, 2, True), T(cx[1], cy[0] - 52, 'B 六边形 边长 30', '#444444', 10, 'c')]
    # C 两点圆
    pr += [C(cx[2], cy[0], 30), L(cx[2] - 30, cy[0], cx[2] + 30, cy[0], GRAY, 1, True),
           T(cx[2], cy[0] - 52, 'C 2P 圆', '#444444', 10, 'c')]
    # D 三点圆
    ux, uy, ur = circumcircle((cx[3] - 26, cy[0] - 18), (cx[3] + 26, cy[0] - 18), (cx[3], cy[0] + 26))
    pr += [C(ux, uy, ur), T(cx[3], cy[0] - 52, 'D 3P 圆', '#444444', 10, 'c')]
    # E 直角三角形 + TTR 内切圆
    pr += [P([(cx[0] - 55, cy[1] - 35), (cx[0] + 55, cy[1] - 35), (cx[0] - 55, cy[1] + 35)], BLACK, 2, True),
           C(cx[0] - 32, cy[1] - 12, 23), T(cx[0], cy[1] - 55, 'E 直角 + TTR R20', '#444444', 10, 'c')]
    # F 椭圆
    ell = [(cx[1] + 48 * math.cos(2 * math.pi * i / 60), cy[1] + 26 * math.sin(2 * math.pi * i / 60)) for i in range(61)]
    pr += [P(ell, BLACK, 2, True), T(cx[1], cy[1] - 55, 'F 椭圆 100×60', '#444444', 10, 'c')]
    # G 圆环
    pr += [C(cx[2], cy[1], 42), C(cx[2], cy[1], 32), T(cx[2], cy[1] - 55, 'G 圆环 80/100', '#444444', 10, 'c')]
    # H 三点弧
    pr += [A(cx[3], cy[1] - 30, 42, 30, 150), T(cx[3], cy[1] - 55, 'H 三点弧', '#444444', 10, 'c')]
    return img('2-A', '一张图里跑完七种图元：A–H 各占一格，互不重叠（示意摆放，非题面坐标）', pr)


def q2b():
    s = [(10, 90), (50, 90)] + arcp(80, 90, 30, 0, 180)[::-1] + arcp(140, 90, 30, 180, 360)[1:]
    pr = [P(s, BLACK, 3)]
    pr.append(P([(10, 180), (50, 210), (90, 180), (130, 210), (170, 180)], BLUE, 3))
    pr.append(L(10, 240, 190, 240))
    for i in range(7):
        x = 10 + 30 * i
        pr.append(L(x, 233, x, 247, GRAY, 1))
        if i < 7:
            pr.append(C(x, 240, 5))
    pr.append(L(10, 265, 210, 265))
    for i in range(9):
        x = 10 + 25 * i
        pr.append(L(x, 262, x, 268, GRAY, 1))
    pr += [L(234, 259, 246, 271, BLACK, 2), L(234, 271, 246, 259, BLACK, 2)]
    pr += [T(90, 215, '样条曲线 5 点', BLUE, 10, 'c'), T(100, 252, '六等分 + 每个 Φ10', '#444444', 9, 'c'),
           T(110, 278, '间距 25 定距等分', '#444444', 9, 'c'), T(240, 248, '点样式 ×5', '#444444', 9, 'c')]
    return img('2-B', 'S 形变宽线 + 样条 + 定数等分 + 定距等分 + 点样式', pr)


def q2c():
    """墙体画成双线 + 240 厚填满剖面线，否则看不出“墙”。"""
    pr = []
    bottom = [(-120, -120), (4120, -120), (4120, 120), (-120, 120)]
    right = [(3880, 120), (4120, 120), (4120, 2880), (3880, 2880)]
    inner = [(1880, 120), (2120, 120), (2120, 600), (1880, 600)]
    inner2 = [(1880, 1500), (2120, 1500), (2120, 2000), (1880, 2000)]
    for poly in (bottom, right, inner, inner2):
        pr += [H(poly, 55, GRAY), P(poly, GRAY, 1)]
    pr += [P(bottom, BLACK, 2, True), P(right, BLACK, 2, True),
           P([(1880, 120), (1880, 2000), (2120, 2000), (2120, 120)], BLACK, 2)]
    # 洞口：y 600–1500 之间把内墙两条线断开
    pr += [L(1880, 600, 2120, 600, BLACK, 2), L(1880, 1500, 2120, 1500, BLACK, 2)]
    pr += [L(0, 0, 4000, 0, RED, 1, True), L(4000, 0, 4000, 3000, RED, 1, True),
           L(2000, 0, 2000, 2000, RED, 1, True)]
    pr += [DIM(0, 0, 4000, 0, -420, '4000'), DIM(4000, 0, 4000, 3000, 420, '3000')]
    pr += [T(2000, 1050, '900 洞口', GREEN, 9, 'c'),
           T(700, 3300, '墙 240 · ANSI31 比例 20 · 孤岛普通', '#444444', 9),
           T(2000, 2200, '内墙', GREEN, 8, 'c')]
    return img('2-C', 'L 形墙体：双线墙 240 + T 接 + 900 洞口 + 剖面线', pr)


def q3a():
    pr = [L(20, 20, 200, 20), L(20, 20, 20, 120), C(150, 80, 20), C(60, 80, 10), R(250, 20, 150, 100)]
    pr += [L(20, 180, 200, 180, BLUE), C(300, 80, 10, BLUE), C(380, 220, 15, BLUE)]
    pr += [L(250, 140, 400, 140, BLACK, 2, True)]
    pr += [L(20, 140, 200, 140, CYAN), C(100, 200, 10, CYAN)]
    pr += [L(220, 20, 220, 120, MAGENTA), C(330, 200, 25, MAGENTA)]
    pr += [L(20, 160, 200, 160, '#2e8b57')]
    pr += [T(110, -6, '0 层白对象', '#444444', 10, 'c'), T(110, 196, '蓝：改色不改层', BLUE, 10, 'c'),
           T(340, 240, 'B 层洋红', MAGENTA, 10, 'c'), T(112, 152, 'C 层绿', '#2e8b57', 10, 'c'),
           T(330, 126, '0 层 DASHED', '#444444', 10, 'c')]
    return img('3-A', '要整理的那张「乱图」：颜色、线型、图层三种混乱都在', pr)


def q3b():
    """只画一个放大的符号（三个重复的小符号在面板里根本看不清），右边列三个属性值。"""
    pr = []
    # 尖端在 (0,0)：左腿 @7<120、右腿 @7<60，右腿顶端再向右延长 10
    pr += [L(0, 0, -3.5, 6.06, BLACK, 2.5), L(0, 0, 3.5, 6.06, BLACK, 2.5),
           L(3.5, 6.06, 13.5, 6.06, BLACK, 2.5)]
    pr += [T(4.2, 8.4, 'RA', BLUE, 8), T(4.2, 12.4, '6.3', BLUE, 10)]
    pr += [T(7.0, 3.4, '10', GREEN, 7.5, 'c')]
    pr += [T(0, -14, '尖端 (0,0) · 两腿与水平成 60° · 属性 RA / 字高 3.5', '#444444', 7)]
    for i, v in enumerate(('6.3', '3.2', '1.6')):
        pr += [T(42, 10 - i * 9, '插入 ' + str(i + 1) + '：Ra ' + v, '#444444', 9)]
    pr += [T(42, -20, '块名 CCD（基点=尖端）', GREEN, 9), T(42, -29, '写块另存 CCD1.dwg，再以 0.5 缩放、90° 旋转插入', GREEN, 8)]
    return img('3-B', '粗糙度符号块：一个符号 + 三次插入的属性值', pr)


def q3c():
    pr = []
    for i in range(17):
        pr.append(L(250, 26 + i * 6, 400, 26 + i * 6 + 6, '#c0c0c0', 1))
    pr.append(R(250, 20, 150, 100, BLACK, 2))
    for (x, y, r) in ((60, 80, 10), (100, 80, 10), (140, 80, 10), (300, 80, 15), (330, 200, 25), (100, 200, 20)):
        pr.append(C(x, y, r))
    pr.append(L(20, 20, 200, 20))
    pr += [T(325, 150, 'HATCH 层 ANSI31', '#444444', 10, 'c'), T(110, 20, '6 个圆：R10×3 + R15/20/25', '#444444', 10, 'c'),
           T(110, 232, '过滤器：类型=圆 且 半径≥10', GREEN, 11, 'c')]
    return img('3-C', '筛出的圆 + 封闭区域填充（填充线白色、不随层）', pr)


def q4a():
    poly = [(-100, 30), (-100, 0), (-40, 0), (-40, 10), (40, 10), (40, 0), (100, 0), (100, 30),
            (20, 30), (20, 120), (-20, 120), (-20, 30)]
    pr = [P(poly, BLACK, 2, True), L(0, -16, 0, 140, RED, 1, True), C(-70, 15, 7), C(70, 15, 7)]
    pr += [L(-30, 15, 30, 15, RED, 1, True), T(0, -30, '槽 20×10', '#444444', 10, 'c')]
    pr += [DIM(-70, 15, 70, 15, -26, '140'), DIM(100, 0, 100, 120, 30, '120')]
    pr += [T(-60, 100, '立板顶部 +20', GREEN, 10), T(30, 44, 'R10 / 2×45° / R5', '#444444', 10)]
    return img('4-A', '镜像 → 合并 → 开槽 → 打孔 → 倒角 → 立板 +20', pr)


def q4b():
    """三种阵列分三块摆开，各自加引线标签，避免叠在一起看不清。"""
    pr = []
    # ① 环形 8 项 / 270°
    cx, cy = -150, 60
    pr += [C(cx, cy, 15), C(cx, cy, 60, RED, 1)]
    for i in range(8):
        a = math.radians(90 - i * 270.0 / 7)
        pr += [C(cx + 60 * math.cos(a), cy + 60 * math.sin(a), 6)]
    pr += cross(cx, cy, 24)
    pr += [T(cx, cy + 92, '① 环形 8 项 / 270°', GREEN, 9, 'c'), T(cx, cy - 36, 'Φ30 + 8×Φ10', '#444444', 8, 'c')]
    # ② 矩形 3 行 5 列 / 15°
    ax, ay = 60, 40
    for row in range(3):
        for col in range(5):
            lx = col * 32
            ly = row * 26
            a2 = math.radians(15)
            px = ax + lx * math.cos(a2) - ly * math.sin(a2)
            py = ay + lx * math.sin(a2) + ly * math.cos(a2)
            pr.append(R(px, py, 22, 12, BLUE, 1.5))
    pr += [T(ax + 60, ay - 24, '② 矩形 3×5 / 角 15°', BLUE, 9, 'c'),
           T(ax + 60, ay - 44, '行距 30 / 列距 25', '#444444', 8, 'c')]
    # ③ 沿路径 6 项
    px0 = -150
    pr += [L(px0, -130, px0, -280, RED, 1, True)]
    for i in range(6):
        pr.append(R(px0 - 11, -280 + i * 30, 22, 12, ORANGE, 1.5))
    pr += [T(px0, -300, '③ 沿路径 6 项', ORANGE, 9, 'c')]
    return img('4-B', '环形 270° / 矩形 3×5 带 15° 角 / 沿路径 6 项', pr)


def q4c():
    """六个操作各占一格，比原来撒在一张图上清楚得多。"""
    pr = []
    cx = [80, 250, 420]
    top = 200
    bottom = 70

    def cell(i, row, label):
        x = cx[i]
        y = top if row == 0 else bottom
        return x, y, T(x, y - 58, label, '#444444', 10, 'c')

    # 1 参照旋转（上排左）
    x, y, t = cell(0, 0, '1 参照旋转：只转不改长')
    pr += [L(x - 50, y - 30, x + 10, y + 10, GRAY, 1, True),
           L(x - 50, y - 30, x + 50, y - 30, BLACK, 2), t]
    # 2 参照缩放（上排中）
    x, y, t = cell(1, 0, '2 参照缩放 37 → 100')
    pr += [R(x - 45, y - 26, 37, 23, GRAY, 1, True), R(x - 45, y - 26, 90, 50), t]
    # 3 打断（上排右）
    x, y, t = cell(2, 0, '3 打断 30–50')
    pr += [L(x - 50, y, x - 12, y, BLACK, 3), L(x + 12, y, x + 50, y, BLACK, 3),
           L(x - 12, y - 9, x + 12, y + 9, RED, 1), t]
    # 4 延伸（下排左）
    x, y, t = cell(0, 1, '4 延伸')
    pr += [L(x - 50, y + 26, x + 50, y + 26, BLACK, 2),
           L(x - 20, y - 20, x - 20, y + 26, BLACK, 2), t]
    # 5 面域（下排中）
    x, y, t = cell(1, 1, '5 面域（查面积）')
    tri = [(x - 40, y - 20), (x + 40, y - 20), (x, y + 40)]
    pr += [P(tri, BLUE, 2, True), t]
    # 6 边界（下排右）
    x, y, t = cell(2, 1, '6 边界 → 闭合多段线')
    pr += [P([(x - 40, y - 20), (x + 40, y - 20), (x - 40, y + 30)], BLACK, 2, True), t]
    return img('4-C', '参照旋转 / 参照缩放 / 打断 / 延伸 / 面域 / 边界：一格一个', pr)


def q5a():
    pr = [rounded_rect(-80, -50, 160, 100, 15), C(0, 0, 30)]
    for (x, y) in ((-60, -30), (60, -30), (-60, 30), (60, 30)):
        pr.append(C(x, y, 6))
        pr += cross(x, y, 11)
    pr += cross(0, 0, 92)
    pr += [DIM(-80, -50, 80, -50, -20, '160'), DIM(80, -50, 80, 50, 20, '100'),
           DIM(-60, 30, 60, 30, 16, '120')]
    pr += [T(0, 41, 'Φ60', GREEN, 9, 'c'), T(-62, -42, '4×Φ12', '#444444', 8, 'c'),
           T(52, 62, 'R15', '#444444', 8)]
    return img('5-A', '端盖：160×100 R15 + Φ60 凸台 + 4×Φ12（孔心距 120×60）', pr)


def q5b():
    pr = []
    # R30 外切连接弧：圆心到两圆心分别为 50 / 42，半径 30。
    # 上下两条各 156°，四个切点分别落在两个已知圆上，不会再连成整圆。
    upper = arcp(4.09, -9.49, 30, 13, 169, 30)[::-1]
    lower = arcp(4.09, 9.49, 30, 191, 347, 30)
    pr += [P(upper, BLACK, 2), P(lower, BLACK, 2)]
    pr += [C(-45, 0, 20), C(45, 0, 12), L(-120, -50, 120, -50)]
    pr.append(P(arcp(-27.68, -10, 40, 150, 270, 30), GRAY, 1.5))
    pr.append(L(45, 0, 45 + 80 * math.cos(math.radians(-30)), 80 * math.sin(math.radians(-30))))
    pr += [L(-45, 0, 45, 0, RED, 1, True), DIM(-45, 0, 45, 0, -92, '90')]
    pr += [T(-45, 30, 'R20', GREEN, 11, 'c'), T(56, 22, 'R12', GREEN, 11), T(0, 26, 'R30 外切', GREEN, 11, 'c'),
           T(10, -88, '灰色 R40：与底线相切、与 R20 内切', '#666666', 8, 'c'), T(72, -26, '-30°', GREEN, 9)]
    return img('5-B', '摇臂：R30 外切两圆、R40 内切、30° 斜肋', pr)


def q5c():
    pts = [(0, 0)]
    x = y = 0
    for i in range(10):
        x += 300
        pts.append((x, y))
        y += 150
        pts.append((x, y))
    pts.append((x + 1200, y))
    y2 = y
    # 第二跑（镜像）
    for i in range(10):
        x += 300
        pts.append((x, y))
        y -= 150
        pts.append((x, y))
    pr = [P(pts, BLACK, 2)]
    pr += [L(0, -100, 0, 3100), L(x + 1200, -100, x + 1200, 3100)]
    pr += [L(0, 0, x + 1200, 0, GRAY, 1)]
    pr += [L(0, 3000, x + 1200, 3000, GRAY, 1)]
    pr += cross(0, 0, 180)
    pr += [T(3000, 1700, '平台 1200 / 标高 1.500', '#444444', 11, 'c')]
    pr += [DIM(0, -100, 3000, -100, -220, '10×300=3000'), DIM(3000, -100, 4200, -100, -220, '1200')]
    pr += [T(1600, 3220, '层高 3000 · 20 级 150×300 · 墙 240 · 板厚 100', '#444444', 11, 'c')]
    return img('5-C', '楼梯剖面：两跑各 10 级、平台 1200、层高 3000', pr)


def q6a():
    poly = [(-85, -60), (85, -60)]
    poly += arcp(85, -45, 15, 270, 360)[1:]
    poly += [(100, 40), (65.36, 60), (-85, 60)]
    poly += arcp(-85, 45, 15, 90, 180)[1:]
    poly += [(-100, -45)]
    poly += arcp(-85, -45, 15, 180, 270)[1:]
    pr = [P(poly, BLACK, 2.5, True), C(0, 0, 20)]
    for (x, y) in ((-75, -35), (75, -35), (-75, 35), (75, 35)):
        pr.append(C(x, y, 8))
        pr += cross(x, y, 13)
    pr += cross(0, 0, 84)
    pr += [DIM(-100, -60, 100, -60, -22, '200'), DIM(100, -60, 100, 60, 22, '120')]
    pr += [T(0, 28, 'Φ40', GREEN, 9, 'c'), T(-72, -48, '4×Φ16', '#444444', 8, 'c'),
           T(40, 50, '30° / 40', GREEN, 8), T(-92, 68, 'R15', '#444444', 8)]
    return img('6-A', '平板件：200×120、三角 R15、右上 30° 切角、Φ40 + 4×Φ16', pr)


def q6b():
    pr = [rounded_rect(-100, -60, 200, 120, 15), C(0, 0, 20)]
    for (x, y) in ((-75, -35), (75, -35), (-75, 35), (75, 35)):
        pr.append(C(x, y, 8))
    # 总长 200 ±0.1：尺寸线放在零件上方，界线从两个角点引出
    pr += [DIM(-100, 60, 100, 60, 12, '200 ±0.1')]
    pr += [T(-58, 12, 'Φ40 +0.025/0', GREEN, 8, 'c')]
    # 基准 A：方框在孔上方，短线连到孔的上素线
    pr += [R(-40, 40, 26, 12, BLACK, 1), T(-27, 46, 'A', GREEN, 8.5, 'c'), L(-27, 40, -27, 20, BLACK, 1)]
    # 形位公差框：垂直度 Φ0.02 对 A，引线指向板的上表面
    pr += [R(-76, 30, 62, 12, BLACK, 1), T(-45, 36, '⊥ Φ0.02 A', GREEN, 7),
           L(-14, 36, -6, 36, BLACK, 1), P([(-6, 36), (0, 33), (0, 39)], BLACK, 1, True)]
    # 粗糙度：三角形贴在底边
    pr += [P([(-96, -60), (-88, -60), (-92, -50)], BLACK, 1, True), T(-108, -54, 'Ra1.6', GREEN, 7.5, 'c')]
    pr += [T(0, -78, 'Ra 1.6 / 3.2 / 6.3 · 4 条技术要求', '#444444', 8.5, 'c')]
    return img('6-B', '6-A 基础上加：极限偏差、垂直度 Φ0.02 A、粗糙度、技术要求', pr)


def q6c():
    pr = []
    total = 10800
    depth = 3600
    # 轴线
    for i in range(4):
        ax = i * 3600
        pr += [L(ax, -700, ax, 4400, RED, 1, True)]
    # 上下外墙（240 厚，轴线居中）
    pr += [R(-120, -120, total + 240, 240, BLACK, 2), R(-120, depth - 120, total + 240, 240, BLACK, 2)]
    # 左右外墙
    pr += [R(-120, 120, 240, depth - 240, BLACK, 2), R(total - 120, 120, 240, depth - 240, BLACK, 2)]
    # 窗洞：每开间 1500，贴内墙面
    for i in range(3):
        x0 = i * 3600 + 120
        pr += [L(x0, depth + 120, x0 + 1500, depth + 120, '#ffffff', 7),
               L(x0, depth - 120, x0 + 1500, depth - 120, '#ffffff', 7),
               L(x0, depth - 120, x0, depth + 120, BLACK, 1.5), L(x0 + 1500, depth - 120, x0 + 1500, depth + 120, BLACK, 1.5)]
    # 轴号圈
    for i in range(4):
        ax = i * 3600
        pr += [C(ax, 4800, 190, BLACK, 1.5), T(ax, 4800, str(i + 1), BLUE, 9, 'c')]
    # 三道尺寸
    pr += [DIM(-120, -120, total + 120, -120, -420, '11040')]
    for i in range(3):
        pr.append(DIM(i * 3600, -120, (i + 1) * 3600, -120, -760, '3600'))
    pr.append(DIM(0, -120, 3600, -120, -1100, '120 / 1500 / 1860 / 120'))
    pr += [DIM(total + 120, -120, total + 120, depth + 120, 420, '3720')]
    # 弧长台阶
    pr.append(A(5400, -2900, 2300, 60, 120, BLUE))
    pr += [T(7900, -3200, '弧长 R2300 / 60°', BLUE, 8, 'c')]
    # 标高
    for yy, tx in ((0, '±0.000'), (1500, '1.500'), (3600, '3.600')):
        pr += [L(11600, yy, 14200, yy, GRAY, 1), T(13600, yy + 230, tx, GREEN, 8, 'c')]
    return img('6-C', '三开间平面：11040 / 3×3600 / 120·1500·1860·120 + 弧长与标高', pr)


def q7a():
    pr = box(0, 0, 0, 120, 80, 15) + box(0, 60, 15, 120, 20, 60)
    pr += [iso_circle(60, 60, 40, 15, 'xz'), iso_circle(20, 40, 15, 6, 'xy'), iso_circle(100, 40, 15, 6, 'xy')]
    pr += [T(iso(60, 60, 112)[0], iso(60, 60, 112)[1], 'Φ30', GREEN, 11, 'c'),
           T(iso(60, 108, 0)[0], iso(60, 108, 0)[1] - 26, '底板 120×80×15 · 立板 120×20×60', '#444444', 11, 'c'),
           T(iso(0, 0, 0)[0] - 40, iso(0, 0, 0)[1] + 34, 'C2 三条棱边', '#444444', 10)]
    return img('7-A', '支座三维：底板 + 立板 + Φ30 + 2×Φ12 + C2（等轴测示意）', pr)


def q7b():
    pr = [L(0, 15, 80, 15), L(0, 30, 80, 30), L(0, 15, 0, 30), L(80, 15, 80, 30)]
    pr += [L(20, 30, 20, 90), L(60, 30, 60, 90), L(18, 90, 62, 90), L(18, 100, 62, 100),
           L(20, 90, 20, 100), L(60, 90, 60, 100), L(18, 90, 18, 100)]
    pr += [L(30, 30, 30, -30, GRAY, 1), L(50, 30, 50, -30, GRAY, 1)]
    pr += [L(0, -15, 80, -15), L(0, -30, 80, -30), L(0, -15, 0, -30), L(80, -15, 80, -30)]
    pr += [L(20, -30, 20, -90), L(60, -30, 60, -90), L(18, -90, 62, -90), L(18, -100, 62, -100),
           L(20, -90, 20, -100), L(60, -90, 60, -100), L(18, -90, 18, -100)]
    pr += [L(0, 0, 80, 0, RED, 1, True)]
    pr += [T(90, 104, 'Φ200 / Φ180', GREEN, 10), T(90, 40, 'Φ60 / Φ30', GREEN, 10),
           T(90, -70, '6×Φ30 / 孔心 R60', GREEN, 10), T(-8, 4, 'X 轴', RED, 10)]
    return img('7-B', '带轮半剖：内孔 Φ30、轮毂 Φ60、辐板 R30–90、轮缘 Φ200、6×Φ30', pr)


def q7c():
    pr = box(0, 0, 0, 100, 60, 40)
    pr += [P([iso(0, 60, 40), iso(100, 60, 40), iso(100, 30, 22.7), iso(0, 30, 22.7)], RED, 1)]
    pr += [P([iso(0, 0, 40), iso(100, 0, 40), iso(100, 30, 22.7), iso(0, 30, 22.7)], BLUE, 1)]
    pr += [iso_circle(50, 30, 40, 10, 'xy')]
    pr += box(-30, -30, -10, 160, 120, 10)
    pr += [T(iso(50, 30, 44)[0], iso(50, 30, 44)[1] + 30, 'Φ20', GREEN, 11, 'c'),
           T(iso(50, 30, 40)[0], iso(50, 30, 40)[1] + 66, 'V 槽 60 · 槽底 z≈22.7', GREEN, 10, 'c'),
           T(iso(100, 90, 0)[0], iso(100, 90, 0)[1] - 22, '底座 160×120×10', '#444444', 10, 'c')]
    return img('7-C', 'V 形楔块：100×60×40 → 30° 求交 → 镜像并集 → 对位底座', pr)


def q8a():
    pr = [R(0, 0, 420, 297, GRAY, 1, True), R(25, 5, 385, 287, BLACK, 2), R(230, 5, 180, 56, BLACK, 2), L(230, 25, 410, 25)]
    # 主视图（全剖）：底板 140×15，立板 60×70，Φ40
    pr += [R(70, 150, 140, 15), R(110, 165, 60, 70), C(140, 205, 20),
           C(95, 157, 6), C(185, 157, 6)]
    pr += [L(140, 165, 140, 235, RED, 1, True)]
    pr += [T(140, 132, '主视图（全剖）', '#444444', 8.5, 'c')]
    # 俯视图（外形）
    pr += [R(70, 40, 140, 70), R(110, 40, 60, 70, GRAY, 1, True), C(95, 75, 6), C(185, 75, 6)]
    pr += [T(140, 22, '俯视图（外形）', '#444444', 8.5, 'c')]
    pr += [DIM(70, 40, 210, 40, -12, '140'), DIM(70, 150, 210, 150, 8, '140')]
    pr += [T(166, 205, 'Φ40 H7', GREEN, 8)]
    pr += [T(300, 150, 'Ra1.6 孔', GREEN, 8), T(300, 132, 'Ra3.2 底面', GREEN, 8),
           T(300, 114, 'Ra6.3 其余', GREEN, 8), T(300, 92, 'M10 螺纹画法练习', '#444444', 8)]
    pr += [T(300, 62, 'A3 · 1:1 · HT200', '#444444', 8)]
    return img('8-A', 'A3 支架零件图：底板 140×80、立板 60×70、Φ40 全剖 + 俯视 + 标注', pr)


def q8b():
    pr = [C(0, 0, 100), C(0, 0, 80), C(0, 0, 40)]
    for i in range(6):
        a = math.radians(i * 60)
        pr += [C(80 * math.cos(a), 80 * math.sin(a), 9)]
    pr += cross(0, 0, 122)
    pr += [L(200, -100, 200, 100), L(232, -100, 232, 100), L(200, -100, 232, -100),
           L(200, 100, 232, 100), L(200, 40, 232, 40), L(200, -40, 232, -40), L(200, 40, 232, 40, GRAY, 1)]
    pr += [T(216, 118, '半剖', '#444444', 10, 'c'), T(0, -134, 'Φ200 · Φ80 · 6×Φ18 @Φ160', GREEN, 11, 'c'),
           T(0, -156, '厚 30 · 两端 1.5×45° · HT200', '#444444', 10, 'c')]
    return img('8-B', '法兰盘：主视图圆 + 左视图半剖（1:2 出图、图框放大 2 倍）', pr)


for fn in (q1a, q1b, q2a, q2b, q2c, q3a, q3b, q3c, q4a, q4b, q4c,
           q5a, q5b, q5c, q6a, q6b, q6c, q7a, q7b, q7c, q8a, q8b):
    IMAGES.append(fn())

out = sys.argv[1] if len(sys.argv) > 1 else 'tools/image_specs.json'
io.open(out, 'w', encoding='utf-8').write(json.dumps({"images": IMAGES}, ensure_ascii=False, indent=1))
print('images:', len(IMAGES), '->', out)
