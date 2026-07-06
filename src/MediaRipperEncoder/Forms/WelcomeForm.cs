using System;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// First-launch welcome screen. Phase 0: static placeholder text and navigation only.
    /// The "don't show this again" checkbox is displayed but not yet persisted — that
    /// arrives with the settings store in Phase 1.
    /// </summary>
    public class WelcomeForm : BaseForm
    {
        private readonly CheckBox _dontShowAgainCheckBox;

        public WelcomeForm()
        {
            Text = "Welcome — " + AppInfo.DisplayName;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(540, 450);

            var heading = new Label
            {
                Text = AppInfo.DisplayName,
                Font = new Font(Font.FontFamily, 14f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(20, 20)
            };

            var body = new Label
            {
                Text = "This app automates ripping and encoding physical media (DVD, Blu-ray, " +
                       "4K UHD Blu-ray) into files organized for popular media servers, including " +
                       "Plex, Jellyfin, Emby, Kodi, and Universal Media Server — or any other " +
                       "server that reads the standard Movies / TV Shows folder layout. It can " +
                       "also be used for personal media preservation.\r\n\r\n" +
                       "IMPORTANT — this app requires the COMMAND-LINE versions of two programs " +
                       "you install yourself:\r\n\r\n" +
                       "•  MakeMKV — the standard installer already includes the command-line " +
                       "tool (makemkvcon.exe), found in the MakeMKV install folder.\r\n\r\n" +
                       "•  HandBrake — the normal HandBrake app is NOT enough. You must also " +
                       "download \"HandBrakeCLI\" (HandBrakeCLI.exe), a separate download on " +
                       "handbrake.fr under Downloads → Command Line Version.\r\n\r\n" +
                       "The setup wizard on the next screen will ask where both of these are " +
                       "located and verify they work before you can continue.",
                Location = new Point(20, 60),
                Size = new Size(500, 310)
            };

            _dontShowAgainCheckBox = new CheckBox
            {
                Text = "Don't show this again",
                AutoSize = true,
                Location = new Point(20, 378)
            };

            var nextButton = new Button
            {
                Text = "Next",
                DialogResult = DialogResult.OK,
                Size = new Size(100, 32),
                Location = new Point(420, 398)
            };

            AcceptButton = nextButton;
            Controls.Add(heading);
            Controls.Add(body);
            Controls.Add(_dontShowAgainCheckBox);
            Controls.Add(nextButton);
        }

        /// <summary>Exposed so Phase 1 can persist this choice to settings.</summary>
        public bool DontShowAgain
        {
            get { return _dontShowAgainCheckBox.Checked; }
        }
    }
}
