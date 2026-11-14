using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CadLearningAssistant.Services
{
    // 只读探测 AutoCAD 功能区（Ribbon）结构。
    // 关键取舍：不引用 AdWindows.dll，全部走反射。
    // 原因是本机 AdWindows 的成员名（RibbonPanel.Source / RibbonItem.Visual 等）
    // 我无法在编译前核实，直接引用一旦名字不对就是 error CS；
    // 反射则在成员不存在时只是导不出内容，不会让整个工程编译失败。
    internal static class RibbonProbe
    {
        // 功能区上的按钮不是独立的 HWND，是 Autodesk 自绘的 WPF 元素，
        // 所以只能用 Ribbon 对象树 + Visual.PointToScreen 求屏幕坐标。
        private static readonly string[] ChildProperties = new string[]
        {
            "Tabs", "Panels", "Items", "Groups", "DropDownItems", "MenuItems", "Source"
        };

        private static readonly Type PointType = Type.GetType("System.Windows.Point, WindowsBase");
        private const uint MN_GETHMENU = 0x01E1;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const int PointerLifetimeMilliseconds = 30000;
        private static PointerForm _pointerForm;

        public static string ShowStepPointer(global::CadLearningAssistant.LearningStep step)
        {
            string message;
            TryShowStepPointer(step, out message);
            return message;
        }

        // 与 ShowStepPointer 同源，但把“指到了没有”明确告诉调用方：
        // 指不到时面板要自动改用命令方式，而不是让用户点一下什么都没发生。
        internal static bool TryShowStepPointer(global::CadLearningAssistant.LearningStep step, out string message)
        {
            message = string.Empty;
            try
            {
                ClearPointer();
                IntPtr host = GetHostWindow();
                uint processId;
                uint threadId = GetWindowThreadProcessId(host, out processId);
                IntPtr foreground = GetForegroundWindow();
                if (step == null || host == IntPtr.Zero || !IsWindowVisible(host) || IsIconic(host)
                    || threadId == 0 || threadId != GetCurrentThreadId()
                    || processId != (uint)Process.GetCurrentProcess().Id
                    || foreground == IntPtr.Zero || GetWindowProcessId(foreground) != processId
                    || !global::CadLearningAssistant.Plugin.IsLearningPaletteVisible)
                {
                    message = "CAD 窗口不在前台或无法核实，暂时指不出来。";
                    return false;
                }
                // 未配置经核实的路径时不按教学标题猜按钮，也不回退到功能区同名项。
                string[] parts;
                if (!global::CadLearningAssistant.Infrastructure.QuestionBank.TryParseMenuPath(step.MenuPath, out parts)
                    || parts.Length == 0)
                {
                    message = "本题还没有经实机核实的菜单路径，指不出来，已改用命令方式。";
                    return false;
                }
                string menuResult = ShowMenuPointer(step.MenuPath);
                if (menuResult.Length > 0)
                {
                    message = menuResult;
                    return true;
                }
                // 2016 装好后菜单栏默认是隐藏的，GetMenu 直接为空。这时说“菜单里没找到”
                // 会让人以为路径写错了，实际只是没把菜单栏打开。
                if (GetMenu(host) == IntPtr.Zero)
                {
                    message = "当前没有打开经典菜单栏：在命令行输 MENUBAR 回车 1 回车打开，或直接按面板路径操作。";
                    return false;
                }
                message = "经典菜单里没找到这一步对应的项（可能不在菜单里），已改用命令方式。";
                return false;
            }
            catch (Exception exception)
            {
                ClearPointer();
                message = "定位菜单失败：" + exception.Message + "，已改用命令方式。";
                return false;
            }
        }

        public static void ClearPointer()
        {
            try
            {
                if (_pointerForm != null)
                {
                    PointerForm form = _pointerForm;
                    _pointerForm = null;
                    form.Close();
                    form.Dispose();
                }
            }
            catch
            {
                _pointerForm = null;
            }
        }

        private static string ShowMenuPointer(string menuPath)
        {
            string[] parts;
            if (!global::CadLearningAssistant.Infrastructure.QuestionBank.TryParseMenuPath(menuPath, out parts)
                || parts.Length == 0)
            {
                return string.Empty;
            }

            IntPtr host = GetHostWindow();
            if (host == IntPtr.Zero || !IsWindowVisible(host))
            {
                return string.Empty;
            }
            IntPtr rootMenu = GetMenu(host);
            if (rootMenu == IntPtr.Zero)
            {
                return string.Empty;
            }
            uint rootProcessId;
            if (GetWindowThreadProcessId(host, out rootProcessId) == 0
                || rootProcessId != (uint)Process.GetCurrentProcess().Id)
            {
                return string.Empty;
            }

            // 先核实完整路径，任何层级未知或重名都不能指向已匹配的前缀。
            IntPtr[] menus = new IntPtr[parts.Length];
            MenuMatch[] matches = new MenuMatch[parts.Length];
            IntPtr currentMenu = rootMenu;
            for (int level = 0; level < parts.Length; level++)
            {
                menus[level] = currentMenu;
                matches[level] = FindMenuItem(currentMenu, parts[level]);
                if (matches[level] == null)
                {
                    return string.Empty;
                }
                if (level < parts.Length - 1)
                {
                    currentMenu = GetSubMenu(currentMenu, matches[level].Index);
                    if (currentMenu == IntPtr.Zero)
                    {
                        return string.Empty;
                    }
                }
            }

            IntPtr currentWindow = host;
            for (int level = 0; level < parts.Length; level++)
            {
                RECT itemRect;
                MenuMatch match = matches[level];
                if (!TryGetMenuItemRect(currentWindow, menus[level], match.Index, out itemRect))
                {
                    return string.Empty;
                }
                bool isTarget = level == parts.Length - 1;
                IntPtr nextWindow = isTarget ? IntPtr.Zero : FindVisiblePopup(menus[level + 1]);
                if (isTarget || nextWindow == IntPtr.Zero)
                {
                    ShowPointer(host, currentWindow, menus[level], match.Index, itemRect, menuPath);
                    return isTarget ? "已定位菜单项：“" + match.Text + "”"
                        : "已核实完整菜单路径；请先点击“" + match.Text + "”，再点此按钮继续定位";
                }
                currentWindow = nextWindow;
            }
            return string.Empty;
        }

        private static IntPtr GetHostWindow()
        {
            try
            {
                IntPtr handle = Process.GetCurrentProcess().MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }
            catch
            {
            }
            return IntPtr.Zero;
        }

        private sealed class MenuMatch
        {
            public int Index;
            public string Text;
        }

        private static MenuMatch FindMenuItem(IntPtr menu, string wanted)
        {
            string normalizedWanted = NormalizeDisplay(wanted);
            if (normalizedWanted.Length == 0)
            {
                return null;
            }
            MenuMatch match = null;
            int count = GetMenuItemCount(menu);
            for (int index = 0; index < count; index++)
            {
                string text = GetMenuItemText(menu, index);
                if (NormalizeDisplay(text) != normalizedWanted)
                {
                    continue;
                }
                if (match != null)
                {
                    return null;
                }
                match = new MenuMatch { Index = index, Text = NormalizeDisplay(text) };
            }
            return match != null && IsMenuItemEnabled(menu, match.Index) ? match : null;
        }

        private static bool IsMenuItemEnabled(IntPtr menu, int index)
        {
            if (menu == IntPtr.Zero || index < 0 || index >= GetMenuItemCount(menu))
            {
                return false;
            }
            uint state = GetMenuState(menu, (uint)index, 0x0400);
            return state != UInt32.MaxValue && (state & 0x0003) == 0;
        }

        private static string GetMenuItemText(IntPtr menu, int index)
        {
            StringBuilder text = new StringBuilder(512);
            int length = GetMenuString(menu, (uint)index, text, text.Capacity, 0x0400);
            return length > 0 && length < text.Capacity - 1 ? text.ToString() : string.Empty;
        }

        private static string NormalizeDisplay(string value)
        {
            string text = (value ?? string.Empty).Trim();
            int tab = text.IndexOf('\t');
            if (tab >= 0)
            {
                text = text.Substring(0, tab);
            }
            text = RegexRemoveShortcut(text);
            text = text.Replace("&", string.Empty).Trim();
            while (text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith("…", StringComparison.Ordinal))
            {
                text = text.EndsWith("...", StringComparison.Ordinal)
                    ? text.Substring(0, text.Length - 3).TrimEnd()
                    : text.Substring(0, text.Length - 1).TrimEnd();
            }
            return text;
        }

        private static string RegexRemoveShortcut(string text)
        {
            StringBuilder result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(' && i + 3 < text.Length && text[i + 1] == '&'
                    && text[i + 3] == ')')
                {
                    i += 3;
                    while (result.Length > 0 && Char.IsWhiteSpace(result[result.Length - 1]))
                    {
                        result.Length--;
                    }
                    continue;
                }
                result.Append(text[i]);
            }
            return result.ToString();
        }

        private static bool TryGetMenuItemRect(IntPtr window, IntPtr menu, int index, out RECT rect)
        {
            rect = new RECT();
            if (window == IntPtr.Zero || menu == IntPtr.Zero || index < 0 || !IsWindowVisible(window)
                || IsIconic(window) || !IsMenuItemEnabled(menu, index))
            {
                return false;
            }
            IntPtr host = GetHostWindow();
            if (window == host ? GetMenu(host) != menu : FindVisiblePopup(menu) != window)
            {
                return false;
            }
            RECT windowRect;
            if (!GetWindowRect(window, out windowRect)
                || !GetMenuItemRect(window, menu, (uint)index, out rect))
            {
                return false;
            }
            if (rect.Right <= rect.Left || rect.Bottom <= rect.Top
                || rect.Left < windowRect.Left || rect.Top < windowRect.Top
                || rect.Right > windowRect.Right || rect.Bottom > windowRect.Bottom)
            {
                return false;
            }
            // 坐标存在不等于当前可见；被面板或其它窗口遮挡时不画箭头。
            Point center = new Point(rect.Left + (rect.Right - rect.Left) / 2,
                rect.Top + (rect.Bottom - rect.Top) / 2);
            return WindowFromPoint(center) == window && MenuItemFromPoint(window, menu, center) == index;
        }

        private static IntPtr FindVisiblePopup(IntPtr menu)
        {
            if (menu == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            IntPtr host = GetHostWindow();
            if (host == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            uint processId;
            uint hostThreadId = GetWindowThreadProcessId(host, out processId);
            uint currentThreadId = GetCurrentThreadId();
            if (hostThreadId == 0 || currentThreadId == 0
                || hostThreadId != currentThreadId
                || processId != (uint)Process.GetCurrentProcess().Id)
            {
                return IntPtr.Zero;
            }
            IntPtr found = IntPtr.Zero;
            EnumThreadWindows(currentThreadId, delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsWindowVisible(window) || GetWindowProcessId(window) != processId)
                {
                    return true;
                }
                StringBuilder className = new StringBuilder(32);
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != "#32768")
                {
                    return true;
                }
                IntPtr actualMenu = SendMessage(window, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero);
                if (actualMenu == menu)
                {
                    found = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static uint GetWindowProcessId(IntPtr window)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            return processId;
        }

        private static bool IsMenuPathCurrent(IntPtr host, string menuPath, IntPtr targetMenu, int targetIndex)
        {
            string[] parts;
            if (!global::CadLearningAssistant.Infrastructure.QuestionBank.TryParseMenuPath(menuPath, out parts)
                || parts.Length == 0)
            {
                return false;
            }
            IntPtr menu = GetMenu(host);
            bool containsTarget = false;
            for (int level = 0; level < parts.Length; level++)
            {
                MenuMatch match = FindMenuItem(menu, parts[level]);
                if (match == null)
                {
                    return false;
                }
                containsTarget = containsTarget || (menu == targetMenu && match.Index == targetIndex);
                if (level < parts.Length - 1)
                {
                    menu = GetSubMenu(menu, match.Index);
                    if (menu == IntPtr.Zero)
                    {
                        return false;
                    }
                }
            }
            return containsTarget;
        }

        private static void ShowPointer(IntPtr host, IntPtr menuWindow, IntPtr menu, int itemIndex, RECT rect,
            string menuPath)
        {
            ClearPointer();
            _pointerForm = new PointerForm(host, menuWindow, menu, itemIndex, rect, menuPath);
            _pointerForm.Show();
        }

        private static void DestroyPointerFromTimer(PointerForm form)
        {
            if (object.ReferenceEquals(_pointerForm, form))
            {
                _pointerForm = null;
            }
            try
            {
                form.Close();
                form.Dispose();
            }
            catch
            {
            }
        }

        private sealed class PointerForm : Form
        {
            private readonly IntPtr _host;
            private readonly IntPtr _menuWindow;
            private readonly IntPtr _menu;
            private readonly int _itemIndex;
            private readonly RECT _expectedRect;
            private readonly Timer _timer;
            private readonly string _menuPath;
            private readonly int _startedAt;

            public PointerForm(IntPtr host, IntPtr menuWindow, IntPtr menu, int itemIndex, RECT expectedRect,
                string menuPath)
            {
                _host = host;
                _menuWindow = menuWindow;
                _menu = menu;
                _itemIndex = itemIndex;
                _expectedRect = expectedRect;
                _menuPath = menuPath;
                _startedAt = Environment.TickCount;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.White;
                TransparencyKey = Color.White;
                Size = new Size(52, 24);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer, true);
                // 箭头尖端位于窗口内 (11,20)，实际对准菜单项顶部中央。
                Bounds = new Rectangle(expectedRect.Left + (expectedRect.Right - expectedRect.Left) / 2 - 11,
                    expectedRect.Top - 20, 52, 24);
                _timer = new Timer { Interval = 250 };
                _timer.Tick += ValidatePointer;
            }

            protected override bool ShowWithoutActivation
            {
                get { return true; }
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams parameters = base.CreateParams;
                    parameters.ExStyle |= 0x08000000 | 0x00000080;
                    return parameters;
                }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                _timer.Start();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(TransparencyKey);
                base.OnPaint(e);
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    e.Graphics.FillPolygon(brush, new Point[]
                    {
                        new Point(5, 4), new Point(17, 4), new Point(11, 20)
                    });
                }
            }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == WM_NCHITTEST)
                {
                    message.Result = new IntPtr(HTTRANSPARENT);
                    return;
                }
                base.WndProc(ref message);
            }

            private void ValidatePointer(object sender, EventArgs e)
            {
                try
                {
                    if (unchecked((uint)(Environment.TickCount - _startedAt)) >= PointerLifetimeMilliseconds
                        || !IsLearningPaletteVisibleSafely()
                        || GetHostWindow() != _host || !IsWindowVisible(_host) || IsIconic(_host)
                        || !IsForegroundInCurrentProcess()
                        || !IsMenuPathCurrent(_host, _menuPath, _menu, _itemIndex))
                    {
                        DestroyPointerFromTimer(this);
                        return;
                    }
                    if (_menu != IntPtr.Zero)
                    {
                        if (_menuWindow != _host && (FindVisiblePopup(_menu) != _menuWindow
                            || !IsWindowVisible(_menuWindow)))
                        {
                            DestroyPointerFromTimer(this);
                            return;
                        }
                        RECT currentRect;
                        if (!TryGetMenuItemRect(_menuWindow, _menu, _itemIndex, out currentRect)
                            || !SameRect(_expectedRect, currentRect))
                        {
                            DestroyPointerFromTimer(this);
                        }
                    }
                }
                catch
                {
                    DestroyPointerFromTimer(this);
                }
            }

            private bool IsLearningPaletteVisibleSafely()
            {
                try
                {
                    return global::CadLearningAssistant.Plugin.IsLearningPaletteVisible;
                }
                catch
                {
                    return false;
                }
            }

            private bool IsForegroundInCurrentProcess()
            {
                IntPtr foreground = GetForegroundWindow();
                return foreground != IntPtr.Zero
                    && GetWindowProcessId(foreground) == (uint)Process.GetCurrentProcess().Id;
            }

            private static bool SameRect(RECT first, RECT second)
            {
                return first.Left == second.Left && first.Top == second.Top
                    && first.Right == second.Right && first.Bottom == second.Bottom;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && _timer != null)
                {
                    _timer.Stop();
                    _timer.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        public static string WriteDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("CAD学习助手 - AutoCAD 经典菜单与功能区结构导出");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            // 经典菜单先独立导出，不依赖 AdWindows，也不触发菜单展开或初始化。
            DumpClassicMenus(sb);
            try
            {
                DumpRibbon(sb);
                DumpComponentManager(sb);
            }
            catch (Exception exception)
            {
                sb.AppendLine("AdWindows 诊断失败：" + exception.Message);
            }
            try
            {
                DumpChildWindows(sb);
            }
            catch (Exception exception)
            {
                sb.AppendLine("子窗口诊断失败：" + exception.Message);
            }

            string folder = Path.GetDirectoryName(typeof(RibbonProbe).Assembly.Location);
            string path = Path.Combine(folder, "ribbon-dump.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static void DumpRibbon(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("=== Ribbon ===");
            object ribbon = GetRibbon();
            if (ribbon == null)
            {
                sb.AppendLine("未能取到功能区对象：Autodesk.Windows.ComponentManager.Ribbon 为 null 或取不到。");
                sb.AppendLine("可能原因：当前用的是经典菜单而不是功能区，或 AdWindows.dll 未被加载。");
            }
            else
            {
                sb.AppendLine("根对象类型：" + ribbon.GetType().FullName);
                sb.AppendLine();
                HashSet<object> seen = new HashSet<object>();
                int[] counts = new int[3];
                Walk(ribbon, "Ribbon", 0, sb, seen, counts);
                sb.AppendLine();
                sb.AppendLine("统计：节点 " + counts[0]
                    + "，带文字或标题的项 " + counts[1]
                    + "，能算出屏幕坐标的项 " + counts[2]);
                sb.AppendLine();
                sb.AppendLine("说明：Rect 为“屏幕左上角 X,Y + 宽x高”，单位是物理像素。");
                sb.AppendLine("IsSelected=true 的是当前显示的选项卡；其它选项卡里的项通常拿不到 Rect。");
            }
        }

        private const int MenuDumpMaxDepth = 16;
        private const int MenuDumpMaxItems = 4096;
        private const int MenuDumpMaxTextLength = 4096;

        private static void DumpClassicMenus(StringBuilder sb)
        {
            sb.AppendLine("=== 经典菜单树（GetMenu / GetSubMenu，只读） ===");
            sb.AppendLine("path 为逐级原始文字数组，保留助记符和快捷键；indexPath 为逐级零基索引。");
            sb.AppendLine("空白/取不到文字的层级用空字符串保留，pathComplete=false，不猜测名称。");
            sb.AppendLine("enabled 为该项自身状态；unknown 表示读取失败，不代表父级可用。");
            sb.AppendLine("仅 TryGetMenuItemRect 成功才输出 visibleRect=(左,上)-(右,下)，单位为屏幕像素。");
            sb.AppendLine("无 visibleRect 不等于不可见；闭合子菜单不取布局、不推算坐标，不展开或初始化菜单。");
            sb.AppendLine("上限：层级 " + MenuDumpMaxDepth + "，菜单项 " + MenuDumpMaxItems + "。");
            int visitedItems = 0;
            try
            {
                IntPtr host = GetHostWindow();
                if (host == IntPtr.Zero || GetWindowProcessId(host) != (uint)Process.GetCurrentProcess().Id)
                {
                    sb.AppendLine("没有可核实的当前进程主窗口，跳过经典菜单树。");
                    return;
                }
                IntPtr menu = GetMenu(host);
                sb.AppendLine("host=0x" + host.ToInt64().ToString("X")
                    + " rootMenu=0x" + menu.ToInt64().ToString("X"));
                if (menu == IntPtr.Zero)
                {
                    sb.AppendLine("GetMenu 返回 0：当前主窗口没有可读取的经典菜单，不猜测替代路径。");
                    return;
                }
                WalkClassicMenu(host, menu, string.Empty, string.Empty, true, 0, sb,
                    new HashSet<IntPtr>(), ref visitedItems);
            }
            catch (Exception exception)
            {
                sb.AppendLine("经典菜单读取失败：" + exception.Message);
            }
            finally
            {
                sb.AppendLine("经典菜单统计：已读取 " + visitedItems + " 项。");
                sb.AppendLine();
            }
        }

        private static void WalkClassicMenu(IntPtr window, IntPtr menu, string parentPath,
            string parentIndexPath, bool parentPathComplete, int depth, StringBuilder sb,
            HashSet<IntPtr> ancestors, ref int visitedItems)
        {
            if (depth >= MenuDumpMaxDepth)
            {
                sb.AppendLine(Pad(depth) + "（达到深度上限，子树截断）indexPath=[" + parentIndexPath + "]");
                return;
            }
            if (!ancestors.Add(menu))
            {
                sb.AppendLine(Pad(depth) + "（菜单句柄循环，跳过）indexPath=[" + parentIndexPath + "]");
                return;
            }
            int count = GetMenuItemCount(menu);
            if (count < 0)
            {
                sb.AppendLine(Pad(depth) + "（GetMenuItemCount 失败）indexPath=[" + parentIndexPath + "]");
            }
            for (int index = 0; index < count; index++)
            {
                if (visitedItems >= MenuDumpMaxItems)
                {
                    sb.AppendLine(Pad(depth) + "（达到菜单项总数上限，剩余项截断）");
                    break;
                }
                visitedItems++;
                string text;
                bool textRead = TryReadMenuDumpText(menu, index, out text);
                bool pathComplete = parentPathComplete && textRead && !string.IsNullOrWhiteSpace(text);
                string path = parentPath + (depth == 0 ? string.Empty : ", ") + QuoteMenuDumpText(text);
                string indexPath = parentIndexPath + (depth == 0 ? string.Empty : ", ")
                    + index.ToString(CultureInfo.InvariantCulture);
                uint state = GetMenuState(menu, (uint)index, 0x0400);
                string enabled = state == UInt32.MaxValue ? "unknown"
                    : ((state & 0x0003) == 0 ? "true" : "false");
                IntPtr subMenu = GetSubMenu(menu, index);
                sb.Append(Pad(depth)).Append("path=[").Append(path).Append("] indexPath=[")
                    .Append(indexPath).Append("] pathComplete=").Append(pathComplete ? "true" : "false")
                    .Append(" enabled=").Append(enabled).Append(" menu=0x")
                    .Append(menu.ToInt64().ToString("X")).Append(" subMenu=0x")
                    .Append(subMenu.ToInt64().ToString("X"));
                if (state != UInt32.MaxValue && (state & 0x0800) != 0)
                {
                    sb.Append(" separator=true");
                }
                RECT rect;
                if (TryGetMenuItemRect(window, menu, index, out rect))
                {
                    sb.Append(" visibleRect=(").Append(rect.Left).Append(",").Append(rect.Top)
                        .Append(")-(").Append(rect.Right).Append(",").Append(rect.Bottom).Append(")");
                }
                sb.AppendLine();
                if (subMenu != IntPtr.Zero && visitedItems < MenuDumpMaxItems)
                {
                    // 只关联当前已显示的弹出窗口；闭合菜单仍遍历结构，但不尝试取矩形。
                    WalkClassicMenu(FindVisiblePopup(subMenu), subMenu, path, indexPath, pathComplete,
                        depth + 1, sb, ancestors, ref visitedItems);
                }
                else if (subMenu != IntPtr.Zero)
                {
                    sb.AppendLine(Pad(depth + 1) + "（达到菜单项总数上限，子树截断）");
                }
            }
            ancestors.Remove(menu);
        }

        private static bool TryReadMenuDumpText(IntPtr menu, int index, out string text)
        {
            text = string.Empty;
            int length = GetMenuString(menu, (uint)index, null, 0, 0x0400);
            if (length <= 0 || length > MenuDumpMaxTextLength)
            {
                return false;
            }
            StringBuilder buffer = new StringBuilder(length + 1);
            int copied = GetMenuString(menu, (uint)index, buffer, buffer.Capacity, 0x0400);
            if (copied != length)
            {
                return false;
            }
            text = buffer.ToString();
            return true;
        }

        private static string QuoteMenuDumpText(string text)
        {
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        private static object GetRibbon()
        {
            try
            {
                Type manager = Type.GetType("Autodesk.Windows.ComponentManager, AdWindows");
                if (manager == null)
                {
                    return null;
                }
                PropertyInfo property = manager.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static);
                return property == null ? null : property.GetValue(null, null);
            }
            catch
            {
                return null;
            }
        }

        // 经典工作空间里没有功能区，只有菜单栏和工具条。这里把 ComponentManager 的
        // 静态属性全列出来（含当前值类型），就能看出菜单栏挂在哪个成员上。
        private static void DumpComponentManager(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("=== AdWindows / ComponentManager ===");
            Type manager = null;
            try
            {
                manager = Type.GetType("Autodesk.Windows.ComponentManager, AdWindows");
            }
            catch (Exception exception)
            {
                sb.AppendLine("取 ComponentManager 失败：" + exception.Message);
            }
            if (manager == null)
            {
                sb.AppendLine("取不到 Autodesk.Windows.ComponentManager, AdWindows（程序集未加载）。");
                return;
            }
            sb.AppendLine("程序集：" + manager.Assembly.FullName);
            sb.AppendLine("程序集位置：" + SafeLocation(manager.Assembly));

            sb.AppendLine();
            sb.AppendLine("-- 与菜单/工具条/功能区相关的公开类型 --");
            Type[] types = SafeTypes(manager.Assembly);
            int listed = 0;
            for (int index = 0; index < types.Length; index++)
            {
                Type type = types[index];
                if (type == null || !type.IsPublic)
                {
                    continue;
                }
                string name = type.Name;
                if (name.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("ToolBar", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Toolbar", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Ribbon", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                sb.AppendLine("  " + type.FullName);
                listed++;
                if (listed >= 200)
                {
                    sb.AppendLine("  （已截断）");
                    break;
                }
            }
            if (listed == 0)
            {
                sb.AppendLine("  （没有匹配的类型）");
            }

            sb.AppendLine();
            sb.AppendLine("-- ComponentManager 的公开静态属性及当前值 --");
            PropertyInfo[] properties = manager.GetProperties(BindingFlags.Public | BindingFlags.Static);
            for (int index = 0; index < properties.Length; index++)
            {
                PropertyInfo property = properties[index];
                string value;
                try
                {
                    object current = property.GetValue(null, null);
                    value = current == null ? "null" : current.GetType().FullName;
                }
                catch (Exception exception)
                {
                    value = "<取值异常 " + exception.GetType().Name + ">";
                }
                sb.AppendLine("  " + property.Name + " : " + property.PropertyType.Name + " → " + value);
            }
        }

        private static string SafeLocation(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch (Exception)
            {
                return "<不可用>";
            }
        }

        private static Type[] SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types;
            }
            catch (Exception)
            {
                return new Type[0];
            }
        }

        // 菜单栏和工具条如果是独立 HWND，这里能直接看到类名和屏幕矩形。
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr param);

        [DllImport("user32.dll")]
        private static extern IntPtr GetMenu(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSubMenu(IntPtr menu, int position);

        [DllImport("user32.dll")]
        private static extern int GetMenuItemCount(IntPtr menu);

        [DllImport("user32.dll")]
        private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int maxCount, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMenuItemRect(IntPtr hWnd, IntPtr menu, uint item, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        private static extern int MenuItemFromPoint(IntPtr window, IntPtr menu, Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static void DumpChildWindows(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("=== AutoCAD 主窗口的子窗口 ===");
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = Process.GetCurrentProcess().MainWindowHandle;
            }
            catch (Exception exception)
            {
                sb.AppendLine("取主窗口句柄失败：" + exception.Message);
            }
            if (handle == IntPtr.Zero)
            {
                sb.AppendLine("主窗口句柄为 0，跳过。");
                return;
            }
            sb.AppendLine("主窗口句柄：0x" + handle.ToInt64().ToString("X"));
            List<IntPtr> children = new List<IntPtr>();
            try
            {
                EnumChildWindows(handle, delegate(IntPtr child, IntPtr param)
                {
                    children.Add(child);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception exception)
            {
                sb.AppendLine("枚举子窗口失败：" + exception.Message);
                return;
            }
            sb.AppendLine("子窗口数：" + children.Count);
            for (int index = 0; index < children.Count && index < 400; index++)
            {
                IntPtr child = children[index];
                StringBuilder className = new StringBuilder(256);
                StringBuilder text = new StringBuilder(256);
                RECT rect;
                GetClassName(child, className, 256);
                GetWindowText(child, text, 256);
                GetWindowRect(child, out rect);
                sb.AppendLine("  0x" + child.ToInt64().ToString("X")
                    + "  cls=" + className.ToString()
                    + "  vis=" + (IsWindowVisible(child) ? "1" : "0")
                    + "  rect=(" + rect.Left + "," + rect.Top + ")-(" + rect.Right + "," + rect.Bottom + ")"
                    + "  " + (rect.Right - rect.Left) + "x" + (rect.Bottom - rect.Top)
                    + (text.Length > 0 ? "  text=\"" + text.ToString() + "\"" : string.Empty));
            }
        }

        private static void Walk(object node, string path, int depth, StringBuilder sb, HashSet<object> seen, int[] counts)
        {
            if (node == null || depth > 7 || sb.Length > 500000)
            {
                return;
            }
            if (seen.Contains(node))
            {
                sb.AppendLine(Pad(depth) + path + "  (同一对象已出现，跳过)");
                return;
            }
            seen.Add(node);
            counts[0] = counts[0] + 1;

            string line = Pad(depth) + path + "  [" + node.GetType().Name + "]";
            string text = ReadString(node, "Text");
            string title = ReadString(node, "Title");
            string name = ReadString(node, "Name");
            string id = ReadString(node, "Id");
            if (text.Length > 0)
            {
                line += "  Text=\"" + text + "\"";
            }
            if (title.Length > 0)
            {
                line += "  Title=\"" + title + "\"";
            }
            if (name.Length > 0)
            {
                line += "  Name=" + name;
            }
            if (id.Length > 0)
            {
                line += "  Id=" + id;
            }
            if (text.Length > 0 || title.Length > 0)
            {
                counts[1] = counts[1] + 1;
            }
            string selected = ReadBool(node, "IsSelected");
            if (selected.Length > 0)
            {
                line += "  IsSelected=" + selected;
            }
            if (ReadBool(node, "IsVisible") == "false")
            {
                line += "  IsVisible=false";
            }
            string rect = TryScreenRect(node);
            if (rect.Length > 0)
            {
                line += "  Rect=" + rect;
                counts[2] = counts[2] + 1;
            }
            sb.AppendLine(line);

            for (int i = 0; i < ChildProperties.Length; i++)
            {
                object child = ReadProperty(node, ChildProperties[i]);
                if (child == null || child is string)
                {
                    continue;
                }
                IEnumerable sequence = child as IEnumerable;
                if (sequence == null)
                {
                    // Source 这类“单个子对象”也走一遍，否则进不到面板里的按钮。
                    if (child.GetType().IsPrimitive)
                    {
                        continue;
                    }
                    Walk(child, path + "/" + ChildProperties[i], depth + 1, sb, seen, counts);
                    continue;
                }
                int index = 0;
                foreach (object element in sequence)
                {
                    if (index >= 500)
                    {
                        break;
                    }
                    Walk(element, path + "/" + ChildProperties[i] + "[" + index + "]", depth + 1, sb, seen, counts);
                    index++;
                }
            }
        }

        private static string TryScreenRect(object node)
        {
            try
            {
                object visual = ReadProperty(node, "Visual");
                if (visual == null || PointType == null)
                {
                    return string.Empty;
                }
                MethodInfo toScreen = visual.GetType().GetMethod("PointToScreen", new Type[] { PointType });
                if (toScreen == null)
                {
                    return string.Empty;
                }
                object point = Activator.CreateInstance(PointType, new object[] { 0.0, 0.0 });
                object screen = toScreen.Invoke(visual, new object[] { point });
                if (screen == null)
                {
                    return string.Empty;
                }
                double x = ToDouble(ReadProperty(screen, "X"));
                double y = ToDouble(ReadProperty(screen, "Y"));
                double width = ToDouble(ReadProperty(visual, "ActualWidth"));
                double height = ToDouble(ReadProperty(visual, "ActualHeight"));
                if (width <= 0 || height <= 0)
                {
                    return string.Empty;
                }
                return "(" + (int)Math.Round(x) + "," + (int)Math.Round(y) + ") "
                    + (int)Math.Round(width) + "x" + (int)Math.Round(height);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static object ReadProperty(object target, string name)
        {
            if (target == null || string.IsNullOrEmpty(name))
            {
                return null;
            }
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length > 0)
                {
                    return null;
                }
                return property.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadString(object target, string name)
        {
            object value = ReadProperty(target, name);
            if (value == null)
            {
                return string.Empty;
            }
            try
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadBool(object target, string name)
        {
            object value = ReadProperty(target, name);
            if (value is bool)
            {
                return ((bool)value) ? "true" : "false";
            }
            return string.Empty;
        }

        private static double ToDouble(object value)
        {
            if (value == null)
            {
                return 0;
            }
            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static string Pad(int depth)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < depth; i++)
            {
                sb.Append("  ");
            }
            return sb.ToString();
        }
    }
}
