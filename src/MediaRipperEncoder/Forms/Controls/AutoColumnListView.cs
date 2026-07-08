using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MediaRipperEncoder.Forms.Controls
{
    /// <summary>
    /// A ListView whose columns, when the user double-clicks the divider between two column
    /// headers, auto-fit to the WIDER of the cell content or the column header text.
    ///
    /// The stock ListView's built-in double-click auto-fit sizes to the content only, ignoring
    /// the header — so a column like "Titles" whose cells contain just "8" collapses to a
    /// single character and hides its own heading. This subclass intercepts that gesture and
    /// sizes to whichever is wider, so the header is always readable.
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
