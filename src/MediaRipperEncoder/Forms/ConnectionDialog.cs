using System;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Edit → ES Connection → Edit connection... — a focused dialog for the encoder-server (ES)
    /// session settings, so changing the server address or shared secret doesn't require digging
    /// through Settings → Advanced (the same fields remain there for initial setup). Edits the
    /// same persisted settings; connection changes take effect on the next launch, and the dialog
    /// says so rather than leaving the user wondering why nothing changed.
    /// </summary>
    public class ConnectionDialog : BaseForm
    {
        private readonly AppSettings _settings;

        private readonly ComboBox _role;
        private readonly TextBox _host;
        private readonly NumericUpDown _port;
        private readonly TextBox _secret;
        private readonly NumericUpDown _maxClients;

        // Online dashboard (Phase A) — reuses the shared secret above.
        private readonly CheckBox _dashboardEnabled;
        private readonly NumericUpDown _dashboardPort;
        private readonly TextBox _dashboardReportTo;
        // Phase B: allow disc setup from the dashboard on this machine.
        private readonly CheckBox _dashboardAllowSetup;

        public ConnectionDialog(AppSettings settings)
        {
            _settings = settings;

            Text = "ES Connection — AutoRipper";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 626);

            var heading = new Label
            {
                Text = "Encoder-server (ES) connection",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 14)
            };

            var roleLabel = new Label { Text = "This machine's role:", AutoSize = true, Location = new Point(16, 48) };
            _role = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(180, 44),
                Size = new Size(320, 23)
            };
            // Order MUST match the NodeRole enum (Standalone=0, EncoderServer=1, RipperClient=2).
            _role.Items.AddRange(new object[]
            {
                "Standalone (rip + encode on this PC)",
                "Server Node — encoder (receives rips to encode)",
                "Client Node — ripper (sends rips to the server)"
            });
            _role.SelectedIndex = (int)settings.NodeRole;

            var hostLabel = new Label { Text = "Encoder server host/IP:", AutoSize = true, Location = new Point(16, 84) };
            _host = new TextBox
            {
                Location = new Point(180, 81),
                Size = new Size(320, 23),
                Text = settings.NodeServerHost ?? ""
            };

            var portLabel = new Label { Text = "Port (same on every node):", AutoSize = true, Location = new Point(16, 118) };
            _port = new NumericUpDown
            {
                Location = new Point(180, 115),
                Size = new Size(100, 23),
                Minimum = 1024,
                Maximum = 65535,
                Value = settings.NodePort >= 1024 && settings.NodePort <= 65535 ? settings.NodePort : 47820
            };

            var secretLabel = new Label { Text = "Shared secret:", AutoSize = true, Location = new Point(16, 152) };
            _secret = new TextBox
            {
                Location = new Point(180, 149),
                Size = new Size(240, 23),
                Text = settings.NodeSharedSecret ?? "",
                UseSystemPasswordChar = true
            };
            var showSecret = new CheckBox { Text = "Show", AutoSize = true, Location = new Point(430, 151) };
            showSecret.CheckedChanged += (s, e) => _secret.UseSystemPasswordChar = !showSecret.Checked;

            var maxLabel = new Label { Text = "Max ripper clients (Server role):", AutoSize = true, Location = new Point(16, 186) };
            _maxClients = new NumericUpDown
            {
                Location = new Point(220, 183),
                Size = new Size(70, 23),
                Minimum = 1,
                Maximum = 10,
                Value = settings.NodeMaxClients >= 1 && settings.NodeMaxClients <= 10 ? settings.NodeMaxClients : 3
            };
            var maxBlurb = new Label
            {
                Text = "How many ripping PCs may be connected to this server at once. Rips still transfer " +
                       "one at a time (extra rippers wait their turn to send), so more clients means faster " +
                       "disc-swapping across a big collection — not a faster server.",
                AutoSize = false,
                Location = new Point(16, 212),
                Size = new Size(486, 48)
            };

            // --- Online dashboard section ---
            var dashHeading = new Label
            {
                Text = "Online dashboard (monitor rips/encodes from a browser)",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(16, 272)
            };
            _dashboardEnabled = new CheckBox
            {
                Text = "Run the dashboard web page on this machine (this is the host)",
                AutoSize = true,
                Location = new Point(16, 298),
                Checked = settings.DashboardEnabled
            };

            var dashPortLabel = new Label { Text = "Dashboard port:", AutoSize = true, Location = new Point(16, 328) };
            _dashboardPort = new NumericUpDown
            {
                Location = new Point(180, 325),
                Size = new Size(100, 23),
                Minimum = 1024,
                Maximum = 65535,
                Value = settings.DashboardPort >= 1024 && settings.DashboardPort <= 65535 ? settings.DashboardPort : 8211
            };

            var reportLabel = new Label { Text = "Show this PC on host:", AutoSize = true, Location = new Point(16, 360) };
            _dashboardReportTo = new TextBox
            {
                Location = new Point(180, 357),
                Size = new Size(320, 23),
                Text = settings.DashboardReportTo ?? ""
            };

            var dashBlurb = new Label
            {
                Text = "Tick the box on ONE machine to host the page (open http://<that PC>:<port>/ in any " +
                       "browser). On every machine you want to see — including standalones — put the host's " +
                       "name or IP in \"Show this PC on host\". All of them share the SAME secret and port " +
                       "above. Log in to the page with that secret. LAN only — do not port-forward.",
                AutoSize = false,
                Location = new Point(16, 386),
                Size = new Size(486, 60)
            };

            _dashboardAllowSetup = new CheckBox
            {
                Text = "Allow setting up new discs from the dashboard on this PC (scan, confirm, rip)",
                AutoSize = true,
                Location = new Point(16, 450),
                Checked = settings.DashboardAllowRemoteSetup
            };

            var note = new Label
            {
                Text = "Every machine in the session — the server and each ripper — must use the same port " +
                       "and the same secret. Connection changes take effect the next time AutoRipper starts " +
                       "on this machine.\r\n\r\n" +
                       "LAN only — do not forward this port through your router; use a VPN for remote access.",
                AutoSize = false,
                Location = new Point(16, 482),
                Size = new Size(486, 76)
            };

            var save = new Button { Text = "Save", Size = new Size(110, 30), Location = new Point(280, 578) };
            var cancel = new Button { Text = "Cancel", Size = new Size(100, 30), Location = new Point(400, 578), DialogResult = DialogResult.Cancel };
            save.Click += OnSave;
            AcceptButton = save;
            CancelButton = cancel;

            Controls.Add(heading);
            Controls.Add(roleLabel);
            Controls.Add(_role);
            Controls.Add(hostLabel);
            Controls.Add(_host);
            Controls.Add(portLabel);
            Controls.Add(_port);
            Controls.Add(secretLabel);
            Controls.Add(_secret);
            Controls.Add(showSecret);
            Controls.Add(maxLabel);
            Controls.Add(_maxClients);
            Controls.Add(maxBlurb);
            Controls.Add(dashHeading);
            Controls.Add(_dashboardEnabled);
            Controls.Add(dashPortLabel);
            Controls.Add(_dashboardPort);
            Controls.Add(reportLabel);
            Controls.Add(_dashboardReportTo);
            Controls.Add(dashBlurb);
            Controls.Add(_dashboardAllowSetup);
            Controls.Add(note);
            Controls.Add(save);
            Controls.Add(cancel);
        }

        private void OnSave(object sender, EventArgs e)
        {
            var role = (NodeRole)_role.SelectedIndex;
            string host = (_host.Text ?? "").Trim();
            string secret = (_secret.Text ?? "").Trim();
            bool dashEnabled = _dashboardEnabled.Checked;
            string reportTo = (_dashboardReportTo.Text ?? "").Trim();

            // Catch the two configurations that LOOK saved but can't work, before persisting.
            if (role == NodeRole.RipperClient && host.Length == 0)
            {
                MessageBox.Show(this, "A Client Node needs the encoder server's host name or IP address.",
                    "Missing host", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (role != NodeRole.Standalone && secret.Length == 0)
            {
                MessageBox.Show(this, "Server and Client nodes need a shared secret (the same one on every machine).",
                    "Missing shared secret", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // The dashboard reuses the shared secret (for browser login + signed reports), so it
            // can't work without one — catch that here rather than failing quietly at runtime.
            if ((dashEnabled || reportTo.Length > 0) && secret.Length == 0)
            {
                MessageBox.Show(this, "The online dashboard uses the shared secret above (for browser " +
                    "login and to sign status reports). Set a shared secret to host or report to a dashboard.",
                    "Missing shared secret", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool changed = _settings.NodeRole != role ||
                           _settings.NodeServerHost != host ||
                           _settings.NodePort != (int)_port.Value ||
                           _settings.NodeSharedSecret != secret ||
                           _settings.NodeMaxClients != (int)_maxClients.Value ||
                           _settings.DashboardEnabled != dashEnabled ||
                           _settings.DashboardPort != (int)_dashboardPort.Value ||
                           _settings.DashboardReportTo != reportTo ||
                           _settings.DashboardAllowRemoteSetup != _dashboardAllowSetup.Checked;

            _settings.NodeRole = role;
            _settings.NodeServerHost = host;
            _settings.NodePort = (int)_port.Value;
            _settings.NodeSharedSecret = secret;
            _settings.NodeMaxClients = (int)_maxClients.Value;
            _settings.DashboardEnabled = dashEnabled;
            _settings.DashboardPort = (int)_dashboardPort.Value;
            _settings.DashboardReportTo = reportTo;
            _settings.DashboardAllowRemoteSetup = _dashboardAllowSetup.Checked;

            try
            {
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save the connection settings:\r\n\r\n" + ex.Message,
                    "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (changed)
            {
                MessageBox.Show(this,
                    "Connection settings saved. They take effect the next time AutoRipper starts.",
                    "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
