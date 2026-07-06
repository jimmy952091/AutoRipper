using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Shows the candidate matches from a lookup and makes the user pick one explicitly.
    /// This is the enforced "never silently auto-match" step: wrong metadata means wrong
    /// placement in a permanent library, so the app always requires a human confirmation —
    /// even when only one candidate came back.
    /// </summary>
    public class ConfirmMatchDialog : BaseForm
    {
        private readonly ListBox _list;
        private readonly Button _ok;

        public MetadataCandidate SelectedCandidate { get; private set; }

        public ConfirmMatchDialog(string query, List<MetadataCandidate> candidates)
        {
            Text = "Confirm the match";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(420, 300);

            var prompt = new Label
            {
                Text = "Results for \"" + query + "\". Select the correct one — nothing is " +
                       "matched automatically.",
                AutoSize = false,
                Location = new Point(15, 12),
                Size = new Size(530, 36),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _list = new ListBox
            {
                Location = new Point(15, 52),
                Size = new Size(530, 300),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                IntegralHeight = false
            };
            foreach (MetadataCandidate c in candidates)
            {
                _list.Items.Add(new CandidateItem(c));
            }
            if (_list.Items.Count > 0) { _list.SelectedIndex = 0; }
            _list.DoubleClick += (s, e) => { if (_list.SelectedItem != null) Accept(); };

            _ok = new Button
            {
                Text = "Use this match",
                Size = new Size(130, 30),
                Location = new Point(285, 366),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _ok.Click += (s, e) => Accept();

            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(120, 30),
                Location = new Point(425, 366),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            AcceptButton = _ok;
            CancelButton = cancel;

            Controls.Add(prompt);
            Controls.Add(_list);
            Controls.Add(_ok);
            Controls.Add(cancel);
        }

        private void Accept()
        {
            var item = _list.SelectedItem as CandidateItem;
            if (item == null)
            {
                MessageBox.Show(this, "Select a match first.", "Confirm the match",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            SelectedCandidate = item.Candidate;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Wraps a candidate so the ListBox shows a rich two-part label.</summary>
        private class CandidateItem
        {
            public MetadataCandidate Candidate { get; }
            public CandidateItem(MetadataCandidate c) { Candidate = c; }

            public override string ToString()
            {
                string year = string.IsNullOrEmpty(Candidate.Year) ? "" : " (" + Candidate.Year + ")";
                string detail = string.IsNullOrEmpty(Candidate.Detail) ? "" : "   —   " + Candidate.Detail;
                return Candidate.Title + year + detail;
            }
        }
    }
}
