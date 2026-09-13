/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>

*/

using System;
using System.Drawing;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI {
  public partial class ReportForm : Form {

    private string report;

    public ReportForm() {
      InitializeComponent();
      // See ReportFile: the upload endpoint this used to post to is gone.
      sendButton.Text = "Save Report";
      try {
        titleLabel.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        reportTextBox.Font = new Font(FontFamily.GenericMonospace,
          SystemFonts.DefaultFont.Size);
      } catch { }
    }

    public string Report {
      get { return report; }
      set {
        report = value;
        reportTextBox.Text = report;
      }
    }

    private void sendButton_Click(object sender, EventArgs e) {
      if (ReportFile.SaveAndNotify(this, "hardware", reportTextBox.Text,
        commentTextBox.Text, emailTextBox.Text))
        Close();
    }
  }
}
