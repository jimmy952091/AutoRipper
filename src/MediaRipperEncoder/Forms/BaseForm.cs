using System;
using System.Drawing;
using System.Windows.Forms;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Base class for every window in the app. Enforces the project's minimum-resolution
    /// rule: the app must remain fully usable on a 1366x768 screen (2010-era laptop).
    ///
    /// It does two things:
    ///  1. On load, if the window is taller/wider than the visible desktop area of the
    ///     monitor it's on (which excludes the taskbar), it shrinks the window to fit.
    ///     This also covers users running 125%/150% Windows display scaling, which
    ///     effectively reduces usable space well below 1366x768.
    ///  2. AutoScroll is on, so whenever the window is smaller than its content —
    ///     because of rule 1, or because the user resized it — scrollbars appear and
    ///     every control stays reachable. No button is ever cut off unreachably.
    /// </summary>
    public class BaseForm : Form
    {
        public BaseForm()
        {
            AutoScroll = true;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Theme first (colors), then clamp (geometry) — every window inherits both behaviours.
            ThemeManager.Apply(this);
            ClampToScreenWorkingArea();
        }

        private void ClampToScreenWorkingArea()
        {
            // WorkingArea is the monitor's resolution minus the taskbar and any docked
            // toolbars — the space a window can actually occupy.
            Rectangle workArea = Screen.FromControl(this).WorkingArea;

            int width = Math.Min(Width, workArea.Width);
            int height = Math.Min(Height, workArea.Height);

            if (width != Width || height != Height)
            {
                // Fixed-size dialogs (FormBorderStyle.FixedDialog) ignore Size changes
                // smaller than their content unless we allow resizing first, so
                // temporarily lift the restriction while we shrink the window.
                FormBorderStyle originalStyle = FormBorderStyle;
                if (originalStyle == FormBorderStyle.FixedDialog ||
                    originalStyle == FormBorderStyle.FixedSingle ||
                    originalStyle == FormBorderStyle.Fixed3D)
                {
                    FormBorderStyle = FormBorderStyle.Sizable;
                    Size = new Size(width, height);
                    FormBorderStyle = originalStyle;
                }
                else
                {
                    Size = new Size(width, height);
                }
            }

            // If clamping (or anything else) pushed the window partly off-screen,
            // pull it back so the title bar and edges are visible.
            int x = Math.Max(workArea.Left, Math.Min(Left, workArea.Right - Width));
            int y = Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - Height));
            Location = new Point(x, y);
        }
    }
}
