using System;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Help &gt; Uninstall AutoRipper... — a confirm dialog with the "remove everything" checkbox
    /// the user wanted. Unchecked = a normal uninstall (program files only, settings kept for a
    /// future reinstall, MSI-standard behavior). Checked = also erase settings, logs, and the
    /// registry markers a plain uninstall leaves behind, so nothing lingers.
    /// </summary>
    public class UninstallDialog : BaseForm
    {
        private readonly CheckBox _removeAll;

        public UninstallDialog()
        {
            Text = "Uninstall AutoRipper";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 250);

            var heading = new Label
            {
                Text = "Uninstall AutoRipper?",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 16)
            };

            var body = new Label
            {
                Text = "This removes the AutoRipper program and its shortcuts. Your ripped media " +
                       "library is never touched — only the app itself.\r\n\r\n" +
                       "By default your settings (tool paths, API keys, preferences) are kept so a " +
                       "future reinstall picks up where you left off.",
                AutoSize = false,
                Location = new Point(16, 46),
                Size = new Size(490, 74)
            };

            _removeAll = new CheckBox
            {
                Text = "Also remove my settings, logs, and preferences (leave nothing behind)",
                AutoSize = true,
                Location = new Point(16, 126)
            };

            var caution = new Label
            {
                Text = "Tick this only if you're removing AutoRipper for good — it erases your saved " +
                       "tool paths, API keys, and window preferences (both current and legacy locations) " +
                       "and their registry entries. It cannot be undone.",
                AutoSize = false,
                ForeColor = ThemeManager.Warn,
                Location = new Point(34, 150),
                Size = new Size(472, 48)
            };

            var uninstall = new Button { Text = "Uninstall", Size = new Size(110, 32), Location = new Point(280, 206) };
            var cancel = new Button { Text = "Cancel", Size = new Size(100, 32), Location = new Point(400, 206), DialogResult = DialogResult.Cancel };
            uninstall.Click += OnUninstall;
            AcceptButton = cancel; // default to the safe action
            CancelButton = cancel;

            Controls.Add(heading);
            Controls.Add(body);
            Controls.Add(_removeAll);
            Controls.Add(caution);
            Controls.Add(uninstall);
            Controls.Add(cancel);
        }

        private void OnUninstall(object sender, EventArgs e)
        {
            bool removeAll = _removeAll.Checked;

            string confirmText = removeAll
                ? "Uninstall AutoRipper AND permanently erase all settings, logs, and preferences?\r\n\r\n" +
                  "Your ripped media library is not affected."
                : "Uninstall AutoRipper? Your settings will be kept for a future reinstall.";
            if (MessageBox.Show(this, confirmText, "Confirm uninstall",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }

            string dataSummary;
            bool msiLaunched;
            UninstallService.RunUninstall(removeAll, out dataSummary, out msiLaunched);

            if (!msiLaunched)
            {
                // Run from a copied folder (no MSI registration) — we can't remove the program
                // files, but we did (optionally) purge the data. Tell the user what's left to do.
                string msg = "AutoRipper isn't registered as an installed program on this machine " +
                    "(it looks like it's running from a copied folder), so there's nothing for the " +
                    "installer to remove — just delete the AutoRipper folder yourself.";
                if (removeAll) { msg += "\r\n\r\n" + dataSummary; }
                MessageBox.Show(this, msg, "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            // The MSI remover is now running and will delete the exe — close the app so it can.
            DialogResult = DialogResult.OK;
            Close();
            Application.Exit();
        }
    }
}
