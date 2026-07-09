using System;
using System.Collections.Generic;
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

        private const int LVGF_STATE = 0x00000004;
        private const int LVGS_COLLAPSED = 0x00000001;
        private const int LVGS_COLLAPSIBLE = 0x00000008;

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
            base.WndProc(ref m);
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
