using System;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Asks the user what to do when an encoded file would land on top of one that already
    /// exists in the library. The project standard forbids silently overwriting media, so this
    /// is shown before any existing file is touched. Offers Keep Both (the safe default),
    /// Overwrite, or Skip, with an option to apply the same choice to the rest of the session.
    /// </summary>
    public class ConflictDialog : BaseForm
    {
        private readonly CheckBox _applyToAll;

        public ConflictResolution Resolution { get; private set; }
        public bool ApplyToAll { get { return _applyToAll.Checked; } }

        public ConflictDialog(string targetPath)
        {
            Text = "File already exists — " + AppInfo.DisplayName;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 230);
            Resolution = ConflictResolution.KeepBoth;

            var heading = new Label
            {
                Text = "A file with this name is already in your library:",
                AutoSize = false,
                Location = new Point(15, 15),
                Size = new Size(530, 20)
            };

            var pathLabel = new Label
            {
                Text = targetPath,
                AutoSize = false,
                Location = new Point(15, 38),
                Size = new Size(530, 40),
                ForeColor = Color.FromArgb(0, 0, 160)
            };

            var prompt = new Label
            {
                Text = "What would you like to do?",
                AutoSize = true,
                Location = new Point(15, 86)
            };

            var keepBoth = new Button { Text = "Keep Both", Location = new Point(15, 120), Size = new Size(150, 34) };
            keepBoth.Click += (s, e) => Choose(ConflictResolution.KeepBoth);

            var overwrite = new Button { Text = "Overwrite", Location = new Point(180, 120), Size = new Size(150, 34) };
            overwrite.Click += (s, e) => Choose(ConflictResolution.Overwrite);

            var skip = new Button { Text = "Skip (keep existing)", Location = new Point(345, 120), Size = new Size(180, 34) };
            skip.Click += (s, e) => Choose(ConflictResolution.Skip);

            _applyToAll = new CheckBox
            {
                Text = "Do this for all remaining conflicts this session",
                AutoSize = true,
                Location = new Point(15, 175)
            };

            AcceptButton = keepBoth; // Enter = the safe choice
            Controls.Add(heading);
            Controls.Add(pathLabel);
            Controls.Add(prompt);
            Controls.Add(keepBoth);
            Controls.Add(overwrite);
            Controls.Add(skip);
            Controls.Add(_applyToAll);
        }

        private void Choose(ConflictResolution resolution)
        {
            Resolution = resolution;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
