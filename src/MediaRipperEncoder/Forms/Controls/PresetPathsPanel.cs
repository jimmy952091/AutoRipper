/*
 * AutoRipper — rips and encodes physical media into Plex/Jellyfin-ready libraries.
 * Copyright (C) 2026 Heto <heto.black@gmail.com>
 *
 * This program is free software: you can redistribute it and/or modify it under the terms of the
 * GNU Affero General Public License as published by the Free Software Foundation, either version 3
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
 * even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License along with this program.
 * If not, see <https://www.gnu.org/licenses/>.
 */
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Forms.Controls
{
    /// <summary>
    /// The four HandBrake preset-file fields (general, animation, UHD, UHD animation), each with
    /// a Browse button. Shared by the Settings dialog's Advanced tab and the first-run Setup
    /// Wizard's Advanced tab so the two never drift apart. Pure path entry — validation happens
    /// when the preset is actually used (and the per-disc screen falls back to the general preset
    /// for any slot left blank).
    /// </summary>
    public class PresetPathsPanel : UserControl
    {
        private TextBox _presetBox;
        private TextBox _animationPresetBox;
        private TextBox _uhdPresetBox;
        private TextBox _uhdAnimationPresetBox;

        private const int PanelWidth = 708;

        public PresetPathsPanel()
        {
            Width = PanelWidth;

            var heading = new Label
            {
                Text = "HandBrake presets (.json exported from HandBrake)",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, 0)
            };
            var blurb = new Label
            {
                Text = "The general preset is used for standard DVD/Blu-ray; the UHD preset is used when a " +
                       "disc is marked UHD Blu-ray (4K HDR needs HEVC 10-bit — an x264/8-bit preset would " +
                       "strip the HDR). Animation variants are optional, picked per disc for cartoons/anime. " +
                       "Leave any you don't use blank.",
                AutoSize = false,
                Location = new Point(14, 26),
                Size = new Size(680, 60)
            };
            Controls.Add(heading);
            Controls.Add(blurb);

            int y = 90;
            AddPresetRow("General preset (standard DVD / Blu-ray):", ref y, out _presetBox);
            AddPresetRow("Animation preset (optional):", ref y, out _animationPresetBox);
            AddPresetRow("UHD preset (4K Blu-ray — HEVC/HDR):", ref y, out _uhdPresetBox);
            AddPresetRow("UHD animation preset (optional):", ref y, out _uhdAnimationPresetBox);

            Height = y;
        }

        public void LoadFrom(AppSettings settings)
        {
            _presetBox.Text = settings.HandBrakePresetPath ?? "";
            _animationPresetBox.Text = settings.HandBrakeAnimationPresetPath ?? "";
            _uhdPresetBox.Text = settings.HandBrakeUhdPresetPath ?? "";
            _uhdAnimationPresetBox.Text = settings.HandBrakeUhdAnimationPresetPath ?? "";
        }

        public void ApplyTo(AppSettings settings)
        {
            settings.HandBrakePresetPath = (_presetBox.Text ?? "").Trim();
            settings.HandBrakeAnimationPresetPath = (_animationPresetBox.Text ?? "").Trim();
            settings.HandBrakeUhdPresetPath = (_uhdPresetBox.Text ?? "").Trim();
            settings.HandBrakeUhdAnimationPresetPath = (_uhdAnimationPresetBox.Text ?? "").Trim();
        }

        /// <summary>Adds a "label / textbox / Browse..." preset-path row.</summary>
        private void AddPresetRow(string labelText, ref int y, out TextBox box)
        {
            var label = new Label { Text = labelText, AutoSize = true, Location = new Point(14, y) };
            box = new TextBox
            {
                Location = new Point(14, y + 18),
                Size = new Size(560, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            TextBox target = box;
            var browse = new Button { Text = "Browse...", Location = new Point(584, y + 17), Size = new Size(90, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            browse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "HandBrake preset (*.json)|*.json|All files (*.*)|*.*" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) { target.Text = dlg.FileName; }
                }
            };
            Controls.Add(label);
            Controls.Add(box);
            Controls.Add(browse);
            y += 50;
        }
    }
}
