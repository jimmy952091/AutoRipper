using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Help &gt; Report a problem... — packages the logs and queue files into one .zip on the
    /// Desktop and opens a new GitHub issue, so a bug report arrives with the evidence needed to
    /// actually diagnose it instead of "it didn't work".
    ///
    /// Deliberate design points:
    ///  - The zip is written locally and NOTHING is uploaded by the app. The user attaches it
    ///    themselves, so they stay in control of what leaves their machine.
    ///  - The dialog LISTS every file that will be included before writing it, and states plainly
    ///    that a GitHub issue is public — logs name the machine, folder paths and disc titles.
    ///  - settings.json is never included (API keys live there).
    ///  - A zip is used because GitHub refuses .log and .json attachments outright.
    /// </summary>
    public class ReportProblemDialog : BaseForm
    {
        private const string IssueUrl = "https://github.com/jimmy952091/AutoRipper/issues/new?template=bug_report.yml";

        private readonly AppSettings _settings;
        private readonly TextBox _description;
        private readonly Label _contents;

        public ReportProblemDialog(AppSettings settings)
        {
            _settings = settings;

            Text = "Report a problem — " + AppInfo.DisplayName;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(620, 560);
            MinimumSize = new Size(560, 480);

            var heading = new Label
            {
                Text = "Report a problem",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 14)
            };

            var intro = new Label
            {
                Text = "This gathers your recent logs and queue files into a single .zip on your Desktop, " +
                       "then opens a new issue on GitHub. Attach the .zip to the issue — it contains the " +
                       "details needed to work out what went wrong.",
                AutoSize = false,
                Location = new Point(16, 40),
                Size = new Size(586, 46)
            };

            var prompt = new Label
            {
                Text = "What happened? (what you were doing, and what you expected instead)",
                AutoSize = true,
                Location = new Point(16, 92)
            };

            _description = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 112),
                Size = new Size(586, 120),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var contentsHeading = new Label
            {
                Text = "What the .zip will contain",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 244)
            };

            _contents = new Label
            {
                Text = DescribeContents(),
                AutoSize = false,
                Location = new Point(16, 266),
                Size = new Size(586, 108),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var privacy = new Label
            {
                Text = "Please read: a GitHub issue is PUBLIC. These files name this computer, your " +
                       "folder paths, and the discs you've ripped. Your API keys and the connection " +
                       "shared secret are never included. If you'd rather not post that publicly, create " +
                       "the .zip anyway and email it privately instead.",
                AutoSize = false,
                ForeColor = ThemeManager.Warn,
                Location = new Point(16, 380),
                Size = new Size(586, 62),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var fleetNote = new Label
            {
                Text = "Running a fleet? The encoder server's own logs are usually needed too — do this " +
                       "on that machine as well and attach both.",
                AutoSize = false,
                Location = new Point(16, 446),
                Size = new Size(586, 34),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var create = new Button { Text = "Create .zip && open GitHub", Size = new Size(190, 32), Location = new Point(236, 496), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var zipOnly = new Button { Text = "Create .zip only", Size = new Size(130, 32), Location = new Point(100, 496), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            var cancel = new Button { Text = "Cancel", Size = new Size(90, 32), Location = new Point(436, 496), DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

            create.Click += (s, e) => CreateReport(openGitHub: true);
            zipOnly.Click += (s, e) => CreateReport(openGitHub: false);
            CancelButton = cancel;

            Controls.Add(heading);
            Controls.Add(intro);
            Controls.Add(prompt);
            Controls.Add(_description);
            Controls.Add(contentsHeading);
            Controls.Add(_contents);
            Controls.Add(privacy);
            Controls.Add(fleetNote);
            Controls.Add(zipOnly);
            Controls.Add(create);
            Controls.Add(cancel);
        }

        /// <summary>Lists the actual files that would be included, so nothing is a surprise.</summary>
        private static string DescribeContents()
        {
            List<DiagnosticsPackager.Item> items = DiagnosticsPackager.Preview();
            if (items.Count == 0)
            {
                return "A summary of your AutoRipper version, Windows version and settings " +
                       "(no keys). No log or queue files were found to include.";
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("• report.txt — AutoRipper version, Windows version, and which tools/presets are set");
            long total = 0;
            foreach (DiagnosticsPackager.Item item in items)
            {
                sb.AppendLine("• " + item.Name + "  (" + Math.Max(1, item.Bytes / 1024) + " KB)");
                total += item.Bytes;
            }
            sb.Append("Total before compression: about " + Math.Max(1, total / 1024) + " KB.");
            return sb.ToString();
        }

        private void CreateReport(bool openGitHub)
        {
            string zipPath;
            try
            {
                zipPath = DiagnosticsPackager.CreateReport(_settings, _description.Text);
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't create the diagnostics report.", ex);
                MessageBox.Show(this,
                    "Couldn't create the report file:\r\n\r\n" + ex.Message,
                    "Report", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Show it in Explorer, already selected, so attaching it is a drag away.
            try { Process.Start("explorer.exe", "/select,\"" + zipPath + "\""); }
            catch { /* not fatal — the path is in the message below */ }

            if (openGitHub)
            {
                try { Process.Start(new ProcessStartInfo(IssueUrl) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    Logger.Info("Couldn't open the GitHub issue page (" + ex.Message + ").");
                }
            }

            MessageBox.Show(this,
                "Report created:\r\n\r\n" + zipPath + "\r\n\r\n" +
                (openGitHub
                    ? "The GitHub issue page is opening in your browser. Drag this .zip onto the issue " +
                      "before posting it."
                    : "Attach this .zip to a GitHub issue, or email it, whichever you prefer."),
                "Report ready", MessageBoxButtons.OK, MessageBoxIcon.Information);

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
