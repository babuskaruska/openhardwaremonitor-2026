/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>

*/

using System;
using System.Text;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI {
  public partial class CrashForm : Form {

    private Exception exception;

    public CrashForm() {
      InitializeComponent();
      // See ReportFile: the upload endpoint this used to post to is gone.
      sendButton.Text = "Save Report";
    }

    public Exception Exception {
      get { return exception; }
      set {
        exception = value;
        StringBuilder s = new StringBuilder();
        Version version = typeof(CrashForm).Assembly.GetName().Version;
        s.Append("Version: "); s.AppendLine(version.ToString());
        s.AppendLine();
        s.AppendLine(exception.ToString());
        s.AppendLine();
        if (exception.InnerException != null) {
          s.AppendLine(exception.InnerException.ToString());
          s.AppendLine();
        }
        s.Append("Common Language Runtime: ");
        s.AppendLine(Environment.Version.ToString());
        s.Append("Operating System: ");
        s.AppendLine(Environment.OSVersion.ToString());
        s.Append("Process Type: ");
        s.AppendLine(IntPtr.Size == 4 ? "32-Bit" : "64-Bit");
        reportTextBox.Text = s.ToString();
      }
    }

    private void sendButton_Click(object sender, EventArgs e) {
      if (ReportFile.SaveAndNotify(this, "crash", reportTextBox.Text,
        commentTextBox.Text, emailTextBox.Text))
        Close();
    }
  }
}
