using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;
using Microsoft.Win32;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Hand-rolled theming for the whole app (WinForms has no built-in dark mode). BaseForm calls
    /// <see cref="Apply"/> on load, so every window gets themed automatically; switching themes in
    /// Settings re-applies to all open windows live.
    ///
    /// Known WinForms limits (deliberate, not bugs): tab headers and progress bars are drawn by
    /// Windows and stay light; list-view GROUP HEADER text keeps its system accent color.
    /// Everything else — window chrome (Win10+), panels, lists, grids, menus, inputs — themes.
    /// </summary>
    public static class ThemeManager
    {
        public static bool IsDark { get; private set; }

        public static void Initialize(ThemePreference preference)
        {
            IsDark = Resolve(preference);
        }

        private static bool Resolve(ThemePreference preference)
        {
            if (preference == ThemePreference.Dark) { return true; }
            if (preference == ThemePreference.Light) { return false; }

            // System: Windows 10+ stores the "app mode" here; missing (Win7/8) means light.
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return value is int && (int)value == 0;
            }
            catch (Exception ex)
            {
                Logger.Info("Theme: couldn't read the Windows app-mode setting (" + ex.Message + "); using Light.");
                return false;
            }
        }

        // ---- palette ----

        public static Color WindowBack { get { return IsDark ? Color.FromArgb(32, 32, 32) : SystemColors.Control; } }
        public static Color FieldBack { get { return IsDark ? Color.FromArgb(45, 45, 48) : SystemColors.Window; } }
        public static Color Text { get { return IsDark ? Color.FromArgb(230, 230, 230) : SystemColors.ControlText; } }
        public static Color Border { get { return IsDark ? Color.FromArgb(70, 70, 74) : SystemColors.ControlDark; } }
        public static Color LinkColor { get { return IsDark ? Color.FromArgb(110, 170, 255) : Color.FromArgb(0, 102, 204); } }

        // Status colors, tuned per theme for contrast (dark greens/reds vanish on a dark background).
        public static Color Ok { get { return IsDark ? Color.FromArgb(96, 205, 130) : Color.FromArgb(0, 110, 0); } }
        public static Color Bad { get { return IsDark ? Color.FromArgb(255, 120, 120) : Color.FromArgb(180, 0, 0); } }
        public static Color Warn { get { return IsDark ? Color.FromArgb(235, 180, 90) : Color.FromArgb(176, 96, 0); } }

        // ---- application ----

        public static void Apply(Form form)
        {
            if (form == null || form.IsDisposed) { return; }
            ApplyTitleBar(form);
            form.BackColor = WindowBack;
            form.ForeColor = Text;
            ApplyRecursive(form);
        }

        public static void ApplyToAllOpenForms()
        {
            foreach (Form form in Application.OpenForms)
            {
                Apply(form);
            }
        }

        private static void ApplyRecursive(Control root)
        {
            foreach (Control c in root.Controls)
            {
                ApplyControl(c);
                ApplyRecursive(c);
            }
        }

        private static void ApplyControl(Control c)
        {
            if (c is Button)
            {
                var b = (Button)c;
                if (IsDark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.BackColor = Color.FromArgb(55, 55, 58);
                    b.ForeColor = Text;
                }
                else
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.BackColor = SystemColors.Control;
                    b.ForeColor = SystemColors.ControlText;
                    b.UseVisualStyleBackColor = true;
                }
            }
            else if (c is LinkLabel)
            {
                ((LinkLabel)c).LinkColor = LinkColor;
                c.BackColor = Color.Transparent;
            }
            else if (c is TextBox || c is ComboBox || c is NumericUpDown || c is ListBox || c is ListView)
            {
                c.BackColor = FieldBack;
                c.ForeColor = Text;
            }
            else if (c is DataGridView)
            {
                var g = (DataGridView)c;
                g.BackgroundColor = FieldBack;
                g.GridColor = Border;
                g.DefaultCellStyle.BackColor = FieldBack;
                g.DefaultCellStyle.ForeColor = Text;
                g.DefaultCellStyle.SelectionBackColor = IsDark ? Color.FromArgb(60, 90, 140) : SystemColors.Highlight;
                g.DefaultCellStyle.SelectionForeColor = IsDark ? Text : SystemColors.HighlightText;
                g.ColumnHeadersDefaultCellStyle.BackColor = IsDark ? Color.FromArgb(50, 50, 53) : SystemColors.Control;
                g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
                g.EnableHeadersVisualStyles = !IsDark;
            }
            else if (c is MenuStrip)
            {
                var m = (MenuStrip)c;
                m.RenderMode = ToolStripRenderMode.Professional;
                m.Renderer = new ToolStripProfessionalRenderer(IsDark ? (ProfessionalColorTable)new DarkColorTable() : new ProfessionalColorTable());
                m.BackColor = WindowBack;
                m.ForeColor = Text;
                foreach (ToolStripItem item in m.Items) { ApplyMenuItem(item); }
            }
            else if (c is ProgressBar || c is TabControl)
            {
                // System-drawn; leave alone (documented limitation).
            }
            else
            {
                // Containers and passive controls: GroupBox, Panel, Label, CheckBox, TabPage, etc.
                c.BackColor = WindowBack;
                c.ForeColor = Text;
            }
        }

        private static void ApplyMenuItem(ToolStripItem item)
        {
            item.ForeColor = Text;
            var menuItem = item as ToolStripMenuItem;
            if (menuItem == null) { return; }
            foreach (ToolStripItem child in menuItem.DropDownItems) { ApplyMenuItem(child); }
        }

        // ---- dark title bar (Windows 10 1809+; harmless no-op elsewhere) ----

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private static void ApplyTitleBar(Form form)
        {
            if (!form.IsHandleCreated) { return; }
            try
            {
                int dark = IsDark ? 1 : 0;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (1903+); 19 = the pre-1903 value. Try both.
                DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
                DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
            }
            catch
            {
                // Windows 7/8 or missing DWM — title bar just stays default.
            }
        }

        /// <summary>Professional color table with the gradients flattened to dark surfaces.</summary>
        private class DarkColorTable : ProfessionalColorTable
        {
            private static readonly Color Back = Color.FromArgb(43, 43, 46);
            private static readonly Color Hover = Color.FromArgb(62, 62, 66);
            private static readonly Color Edge = Color.FromArgb(70, 70, 74);

            public override Color MenuStripGradientBegin { get { return Color.FromArgb(32, 32, 32); } }
            public override Color MenuStripGradientEnd { get { return Color.FromArgb(32, 32, 32); } }
            public override Color ToolStripDropDownBackground { get { return Back; } }
            public override Color ImageMarginGradientBegin { get { return Back; } }
            public override Color ImageMarginGradientMiddle { get { return Back; } }
            public override Color ImageMarginGradientEnd { get { return Back; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Hover; } }
            public override Color MenuItemPressedGradientEnd { get { return Hover; } }
            public override Color MenuItemBorder { get { return Edge; } }
            public override Color MenuBorder { get { return Edge; } }
            public override Color SeparatorDark { get { return Edge; } }
            public override Color SeparatorLight { get { return Back; } }
        }
    }
}
