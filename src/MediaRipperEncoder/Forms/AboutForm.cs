using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Help &gt; About dialog. Shows the app identity, version, copyright, and the FULL license
    /// text (read from the LICENSE file shipped next to the exe). Beyond being good manners, this
    /// is groundwork for AGPL §13: once the network/connector feature ships, the running program
    /// must surface an "appropriate legal notice" (license + copyright + a link to the source).
    /// </summary>
    public class AboutForm : BaseForm
    {
        public AboutForm()
        {
            Text = "About AutoRipper";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(560, 480);
            ClientSize = new Size(640, 560);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(14)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // header block
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // license text
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // buttons

            layout.Controls.Add(BuildHeader(), 0, 0);
            layout.Controls.Add(BuildLicenseBox(), 0, 1);
            layout.Controls.Add(BuildButtons(), 0, 2);

            Controls.Add(layout);
        }

        private Control BuildHeader()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 8)
            };

            var heading = new Label
            {
                Text = "AutoRipper",
                Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };

            var tagline = new Label
            {
                Text = "Automated disc ripping & encoding for Plex, Jellyfin, Emby, Kodi, and more.",
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6)
            };

            var version = new Label
            {
                Text = "Version " + GetVersion(),
                AutoSize = true,
                Margin = new Padding(0)
            };

            var copyright = new Label
            {
                Text = GetCopyright(),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6)
            };

            var licenseLine = new Label
            {
                Text = "Licensed under the GNU Affero General Public License, version 3 or (at your " +
                       "option) any later version. This is free software: you may share and modify it, " +
                       "but it can never be made closed-source. The full license text is below.",
                AutoSize = true,
                MaximumSize = new Size(590, 0),
                Margin = new Padding(0, 0, 0, 4)
            };

            var link = new LinkLabel
            {
                Text = "https://www.gnu.org/licenses/agpl-3.0.html",
                AutoSize = true,
                Margin = new Padding(0)
            };
            link.LinkClicked += (s, e) => OpenUrl("https://www.gnu.org/licenses/agpl-3.0.html");

            // AGPL §13 "appropriate legal notice": the running program tells its users where the
            // source lives — this matters especially for the network (server node) feature.
            var sourceLink = new LinkLabel
            {
                Text = "Source code: https://github.com/jimmy952091/AutoRipper",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 0)
            };
            sourceLink.LinkClicked += (s, e) => OpenUrl("https://github.com/jimmy952091/AutoRipper");

            panel.Controls.Add(heading);
            panel.Controls.Add(tagline);
            panel.Controls.Add(version);
            panel.Controls.Add(copyright);
            panel.Controls.Add(licenseLine);
            panel.Controls.Add(link);
            panel.Controls.Add(sourceLink);
            return panel;
        }

        private Control BuildLicenseBox()
        {
            // Read-only, scrollable view of the actual LICENSE file. Monospaced so the AGPL's
            // fixed-width formatting stays intact.
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 8.5f),
                Text = LoadLicenseText(),
                BackColor = Color.White
            };
            box.Select(0, 0);
            return box;
        }

        private Control BuildButtons()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0)
            };

            var close = new Button { Text = "Close", Size = new Size(90, 30), DialogResult = DialogResult.OK };
            AcceptButton = close;
            CancelButton = close;
            panel.Controls.Add(close);
            return panel;
        }

        // --- helpers ---

        private static string GetVersion()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                // Three parts normally (0.2.3); include the fourth only when it's a hotfix
                // revision (0.2.3.1) — otherwise About couldn't distinguish a hotfix build.
                // Hotfix revisions are shown ZERO-PADDED to two digits (0.2.4.01) so they sort
                // and read unambiguously up to .99. Windows stores it as a plain integer, so
                // 0.2.4.1 and 0.2.4.01 are the same build — the padding is presentation only.
                string text = v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
                return v.Revision > 0 ? text + "." + v.Revision.ToString("00") : text;
            }
            catch
            {
                return "0.2.4.01";
            }
        }

        private static string GetCopyright()
        {
            try
            {
                var attr = (AssemblyCopyrightAttribute)Attribute.GetCustomAttribute(
                    Assembly.GetExecutingAssembly(), typeof(AssemblyCopyrightAttribute));
                if (attr != null && !string.IsNullOrWhiteSpace(attr.Copyright))
                {
                    return attr.Copyright;
                }
            }
            catch
            {
                // fall through to default
            }
            return "Copyright (C) 2026 James Spurgeon";
        }

        /// <summary>
        /// Loads the LICENSE file shipped alongside the exe. Falls back to a short notice (with a
        /// pointer to the canonical text) if the file isn't present, so the dialog is never blank.
        /// </summary>
        private static string LoadLicenseText()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LICENSE");
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("About: couldn't read LICENSE file.", ex);
            }

            return "GNU Affero General Public License v3 or later." + Environment.NewLine +
                   "The full license text should ship in a file named 'LICENSE' next to the " +
                   "application, and is available at https://www.gnu.org/licenses/agpl-3.0.txt";
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("About: couldn't open URL " + url, ex);
            }
        }
    }
}
