using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MediaRipperEncoder.Forms.Controls
{
    /// <summary>
    /// A ListView with two upgrades over the stock control:
    ///
    /// 1. Column divider double-click auto-fits to the WIDER of cell content or header text
    ///    (the stock behaviour ignores the header, so a column like "Titles" whose cells contain
    ///    just "8" collapses to one character and hides its own heading).
    ///
    /// 2. Excel-style COLLAPSIBLE GROUPS: each ListViewGroup header gets a native ▲/▼ chevron and
    ///    its rows fold up into the header line (used so finished discs shrink to one line in the
    ///    rip queue). Windows' list-view supports this natively since Vista, but .NET Framework
    ///    never exposed it — so we set the group state via LVM_SETGROUPINFO interop.
    /// </summary>
    public class AutoColumnListView : ListView
    {
        public AutoColumnListView()
        {
            // Double-buffer so in-place cell/progress updates don't repaint-flicker the whole
            // list. DoubleBuffered is protected, so it can only be set from a subclass like this.
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        // ================= collapsible groups (Excel-style fold-into-header) =================

        private const int LVM_FIRST = 0x1000;
        private const int LVM_SETGROUPINFO = LVM_FIRST + 147;
        private const int LVM_GETGROUPINFO = LVM_FIRST + 149;
        private const int LVM_GETGROUPRECT = LVM_FIRST + 98;
        private const int LVGGR_HEADER = 1;
        private const int WM_LBUTTONDOWN = 0x0201;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;    // set to LVGGR_HEADER before sending LVM_GETGROUPRECT
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);

        private const int LVGF_STATE = 0x00000004;
        private const int LVGS_COLLAPSED = 0x00000001;
        private const int LVGS_COLLAPSIBLE = 0x00000008;

        // Custom-draw plumbing for dark-mode group headers. Windows paints group header text with
        // a theme accent that ignores ForeColor AND survives DarkMode_Explorer (verified on real
        // hardware: header stayed blue-on-dark). So in dark mode we paint the header ourselves.
        private const int WM_REFLECT_NOTIFY = 0x204E;   // WM_REFLECT | WM_NOTIFY
        private const int NM_CUSTOMDRAW = -12;
        private const int CDDS_PREPAINT = 0x00000001;
        private const int CDRF_SKIPDEFAULT = 0x00000004;
        private const int LVCDI_GROUP = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct NMCUSTOMDRAW
        {
            public NMHDR hdr;
            public int dwDrawStage;
            public IntPtr hdc;
            public RECT rc;
            public IntPtr dwItemSpec;   // for groups: the native group id
            public int uItemState;
            public IntPtr lItemlParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMLVCUSTOMDRAW
        {
            public NMCUSTOMDRAW nmcd;
            public int clrText;
            public int clrTextBk;
            public int iSubItem;
            public int dwItemType;      // LVCDI_GROUP when this notification is for a group header
            public int clrFace;
            public int iIconEffect;
            public int iIconPhase;
            public int iPartId;
            public int iStateId;
            public RECT rcText;
            public int uAlign;
        }

        // Full Vista+ LVGROUP layout. cbSize must match the struct the OS expects — passing the
        // shorter pre-Vista layout makes comctl32 v6 reject LVM_SETGROUPINFO outright (verified:
        // group state silently failed to apply with the short struct).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LVGROUP
        {
            public uint cbSize;
            public uint mask;
            public IntPtr pszHeader;
            public int cchHeader;
            public IntPtr pszFooter;
            public int cchFooter;
            public int iGroupId;
            public uint stateMask;
            public uint state;
            public uint uAlign;
            public IntPtr pszSubtitle;
            public uint cchSubtitle;
            public IntPtr pszTask;
            public uint cchTask;
            public IntPtr pszDescriptionTop;
            public uint cchDescriptionTop;
            public IntPtr pszDescriptionBottom;
            public uint cchDescriptionBottom;
            public int iTitleImage;
            public int iExtendedImage;
            public int iFirstItem;
            public uint cItems;
            public IntPtr pszSubsetTitle;
            public uint cchSubsetTitle;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVGROUP lParam);

        // Desired state per group, so it can be re-applied if Windows recreates the control's
        // handle (which silently resets native group state).
        private readonly Dictionary<ListViewGroup, bool> _groupCollapsed = new Dictionary<ListViewGroup, bool>();

        /// <summary>
        /// Gives a group the native collapse chevron (and optionally starts it collapsed). The user
        /// can then click the ▲/▼ on the group header to fold its rows into the header line.
        /// </summary>
        public void SetGroupCollapsed(ListViewGroup group, bool collapsed)
        {
            if (group == null || group.ListView != this) { return; }
            _groupCollapsed[group] = collapsed;
            if (IsHandleCreated) { ApplyGroupState(group, collapsed); }
        }

        /// <summary>
        /// Reads the group's CURRENT collapsed state from the OS — the ground truth that includes
        /// the user's own chevron clicks, which happen natively without telling WinForms (or us).
        /// Falls back to our bookkeeping if the handle isn't ready.
        /// </summary>
        public bool IsGroupCollapsed(ListViewGroup group)
        {
            bool tracked;
            _groupCollapsed.TryGetValue(group, out tracked);

            int groupId = GetNativeGroupId(group);
            if (group == null || group.ListView != this || groupId < 0 || !IsHandleCreated)
            {
                return tracked;
            }

            var lvg = new LVGROUP
            {
                cbSize = (uint)Marshal.SizeOf(typeof(LVGROUP)),
                mask = LVGF_STATE,
                stateMask = LVGS_COLLAPSIBLE | LVGS_COLLAPSED
            };
            SendMessage(Handle, LVM_GETGROUPINFO, (IntPtr)groupId, ref lvg);
            return (lvg.state & LVGS_COLLAPSED) != 0;
        }

        /// <summary>
        /// Changes a group's header text WITHOUT losing its fold state. Two problems this solves:
        /// setting ListViewGroup.Header makes WinForms re-send the group to the OS minus the
        /// collapsible flag (killing the chevron), and our own bookkeeping doesn't know about the
        /// user's manual chevron clicks. So: read the real state from the OS first, set the text,
        /// then re-apply chevron + the state we just read — the user's choice survives.
        /// </summary>
        public void SetGroupHeaderPreservingState(ListViewGroup group, string header)
        {
            if (group == null || group.ListView != this || group.Header == header) { return; }

            bool tracked = _groupCollapsed.ContainsKey(group);
            bool collapsed = tracked && IsGroupCollapsed(group); // read BEFORE the header stomps it

            group.Header = header;

            if (tracked)
            {
                _groupCollapsed[group] = collapsed; // adopt the user's latest choice as ours
                if (IsHandleCreated) { ApplyGroupState(group, collapsed); }
            }
        }

        private void ApplyGroupState(ListViewGroup group, bool collapsed)
        {
            int groupId = GetNativeGroupId(group);
            if (groupId < 0) { return; }

            var lvg = new LVGROUP
            {
                cbSize = (uint)Marshal.SizeOf(typeof(LVGROUP)),
                mask = LVGF_STATE,
                stateMask = LVGS_COLLAPSIBLE | LVGS_COLLAPSED,
                state = (uint)(LVGS_COLLAPSIBLE | (collapsed ? LVGS_COLLAPSED : 0))
            };
            SendMessage(Handle, LVM_SETGROUPINFO, (IntPtr)groupId, ref lvg);
        }

        /// <summary>
        /// The native list-view addresses groups by an internal integer id that WinForms assigns
        /// but never exposes publicly — read it via reflection. Returns -1 if unavailable.
        /// </summary>
        private static int GetNativeGroupId(ListViewGroup group)
        {
            try
            {
                PropertyInfo prop = typeof(ListViewGroup).GetProperty("ID",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                return prop != null ? (int)prop.GetValue(group, null) : -1;
            }
            catch
            {
                return -1;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // A handle recreation (theme change, certain style changes) resets native group state;
            // re-apply what the app asked for. BeginInvoke so WinForms finishes re-adding groups first.
            if (_groupCollapsed.Count > 0)
            {
                BeginInvoke((Action)(() =>
                {
                    foreach (KeyValuePair<ListViewGroup, bool> kv in _groupCollapsed)
                    {
                        if (kv.Key.ListView == this) { ApplyGroupState(kv.Key, kv.Value); }
                    }
                }));
            }
        }

        // Win32 header-control notification plumbing.
        private const int WM_NOTIFY = 0x004E;
        private const int HDN_FIRST = -300;
        private const int HDN_DIVIDERDBLCLICKA = HDN_FIRST - 5;   // -305
        private const int HDN_DIVIDERDBLCLICKW = HDN_FIRST - 25;  // -325

        [StructLayout(LayoutKind.Sequential)]
        private struct NMHDR
        {
            public IntPtr hwndFrom;
            public IntPtr idFrom;
            public int code;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NMHEADER
        {
            public NMHDR hdr;
            public int iItem;    // the column index whose divider was double-clicked
            public int iButton;
            public IntPtr pitem;
        }

        protected override void WndProc(ref Message m)
        {
            // Fold/unfold on a group-header click. The native list-view DRAWS the chevron for
            // collapsible groups but does not toggle them on click in a WinForms host (Explorer
            // implements that itself) — verified on a real run: clicks did nothing. So we own the
            // gesture: any click on a tracked disc's header line (chevron included) toggles it.
            // The message is swallowed so nothing else reinterprets the click.
            if (m.Msg == WM_LBUTTONDOWN && ShowGroups && _groupCollapsed.Count > 0)
            {
                var clickPoint = new Point(
                    (short)((long)m.LParam & 0xFFFF),
                    (short)(((long)m.LParam >> 16) & 0xFFFF));

                ListViewGroup hit = TrackedGroupHeaderAt(clickPoint);
                if (hit != null)
                {
                    SetGroupCollapsed(hit, !IsGroupCollapsed(hit));
                    return;
                }
            }

            if (m.Msg == WM_NOTIFY && m.LParam != IntPtr.Zero)
            {
                var hdr = (NMHDR)Marshal.PtrToStructure(m.LParam, typeof(NMHDR));
                if (hdr.code == HDN_DIVIDERDBLCLICKW || hdr.code == HDN_DIVIDERDBLCLICKA)
                {
                    var nmh = (NMHEADER)Marshal.PtrToStructure(m.LParam, typeof(NMHEADER));
                    AutoFitColumn(nmh.iItem);
                    return; // swallow the default (content-only) auto-fit
                }
            }

            // Dark mode: paint group headers ourselves (see the custom-draw constants above for
            // why). Only group notifications are intercepted — item drawing stays with WinForms,
            // so row colors/selection behave exactly as before. Light mode never enters here.
            if (m.Msg == WM_REFLECT_NOTIFY && m.LParam != IntPtr.Zero && ThemeManager.IsDark)
            {
                var rhdr = (NMHDR)Marshal.PtrToStructure(m.LParam, typeof(NMHDR));
                if (rhdr.code == NM_CUSTOMDRAW)
                {
                    var cd = (NMLVCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMLVCUSTOMDRAW));
                    if (cd.dwItemType == LVCDI_GROUP && (cd.nmcd.dwDrawStage & CDDS_PREPAINT) != 0)
                    {
                        DrawDarkGroupHeader(cd.nmcd.hdc, (int)cd.nmcd.dwItemSpec);
                        m.Result = (IntPtr)CDRF_SKIPDEFAULT;
                        return;
                    }
                }
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// Paints one group header for dark mode: disc label in a readable accent, a separator
        /// line, and the fold chevron — replacing Windows' default rendering, whose text color
        /// can't be changed and is illegible on a dark background.
        /// </summary>
        private void DrawDarkGroupHeader(IntPtr hdc, int nativeGroupId)
        {
            ListViewGroup group = FindGroupByNativeId(nativeGroupId);

            var rc = new RECT { top = LVGGR_HEADER };
            SendMessage(Handle, LVM_GETGROUPRECT, (IntPtr)nativeGroupId, ref rc);
            Rectangle bounds = Rectangle.FromLTRB(rc.left, rc.top, rc.right, rc.bottom);
            if (bounds.Width <= 0 || bounds.Height <= 0) { return; }

            string text = group != null ? (group.Header ?? "") : "";
            bool collapsed = group != null && IsGroupCollapsed(group);

            // Soft blue-on-dark: keeps the "header accent" feel without the illegibility.
            Color headerText = Color.FromArgb(140, 185, 240);

            using (Graphics g = Graphics.FromHdc(hdc))
            {
                using (var back = new SolidBrush(ThemeManager.FieldBack))
                {
                    g.FillRectangle(back, bounds);
                }

                var textRect = new Rectangle(bounds.X + 6, bounds.Y, Math.Max(0, bounds.Width - 44), bounds.Height);
                const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(g, text, Font, textRect, headerText, flags);

                // Separator from the end of the text to the chevron, like the native header.
                int textWidth = TextRenderer.MeasureText(g, text, Font).Width;
                int lineStart = Math.Min(bounds.X + 6 + textWidth + 8, bounds.Right - 40);
                int midY = bounds.Y + bounds.Height / 2;
                if (lineStart < bounds.Right - 30)
                {
                    using (var pen = new Pen(ThemeManager.Border))
                    {
                        g.DrawLine(pen, lineStart, midY, bounds.Right - 30, midY);
                    }
                }

                // Chevron glyph (decorative — clicks anywhere on the header toggle the fold).
                var glyphRect = new Rectangle(bounds.Right - 28, bounds.Y, 22, bounds.Height);
                TextRenderer.DrawText(g, collapsed ? "▼" : "▲", Font, glyphRect, headerText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>Finds the managed group matching a native group id, or null.</summary>
        private ListViewGroup FindGroupByNativeId(int nativeGroupId)
        {
            foreach (ListViewGroup group in Groups)
            {
                if (GetNativeGroupId(group) == nativeGroupId) { return group; }
            }
            return null;
        }

        /// <summary>
        /// Returns the tracked (collapsible) group whose header line contains the given client
        /// point, or null. Header rectangles come from the control itself (LVM_GETGROUPRECT), so
        /// this stays correct as groups scroll, resize, or collapse.
        /// </summary>
        private ListViewGroup TrackedGroupHeaderAt(Point clientPoint)
        {
            foreach (ListViewGroup group in _groupCollapsed.Keys)
            {
                if (group.ListView != this) { continue; }
                int groupId = GetNativeGroupId(group);
                if (groupId < 0) { continue; }

                var rect = new RECT { top = LVGGR_HEADER }; // in: which rectangle; out: coordinates
                SendMessage(Handle, LVM_GETGROUPRECT, (IntPtr)groupId, ref rect);

                if (clientPoint.X >= rect.left && clientPoint.X < rect.right &&
                    clientPoint.Y >= rect.top && clientPoint.Y < rect.bottom)
                {
                    return group;
                }
            }
            return null;
        }

        /// <summary>Sizes the given column to the wider of its content or its header text.</summary>
        private void AutoFitColumn(int index)
        {
            if (index < 0 || index >= Columns.Count) { return; }

            AutoResizeColumn(index, ColumnHeaderAutoResizeStyle.ColumnContent);
            int contentWidth = Columns[index].Width;

            AutoResizeColumn(index, ColumnHeaderAutoResizeStyle.HeaderSize);
            int headerWidth = Columns[index].Width;

            Columns[index].Width = Math.Max(contentWidth, headerWidth);
        }
    }
}
