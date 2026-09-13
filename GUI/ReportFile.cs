/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Saves crash and hardware reports locally.
  ///
  /// Both report forms used to POST to http://openhardwaremonitor.org/report.php
  /// over plain HTTP, using the synchronous and now obsolete WebRequest API.
  /// That endpoint no longer exists, so "Send" could only ever fail. Reports
  /// are written to disk and copied to the clipboard instead, ready to attach
  /// to a bug report by hand. Nothing leaves the machine automatically.
  /// </summary>
  internal static class ReportFile {

    public static bool SaveAndNotify(IWin32Window owner, string kind,
      string report, string comment, string contact) {

      StringBuilder text = new StringBuilder();
      Version? version = typeof(ReportFile).Assembly.GetName().Version;
      text.AppendLine("Open Hardware Monitor " + kind + " report");
      text.AppendLine("Version: " + version);
      text.AppendLine("Created: " + DateTime.Now.ToString("u"));
      if (!string.IsNullOrWhiteSpace(contact))
        text.AppendLine("Contact: " + contact.Trim());
      if (!string.IsNullOrWhiteSpace(comment)) {
        text.AppendLine();
        text.AppendLine("Comment:");
        text.AppendLine(comment.Trim());
      }
      text.AppendLine();
      text.AppendLine(report);

      string directory = Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData),
        "OpenHardwareMonitor", "Reports");
      string path = Path.Combine(directory, kind + "-report-" +
        DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");

      try {
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, text.ToString(), Encoding.UTF8);
      } catch (Exception ex) when (ex is IOException ||
        ex is UnauthorizedAccessException) {
        MessageBox.Show(owner, "The report could not be saved:\n\n" +
          ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
      }

      bool copied = false;
      try {
        Clipboard.SetText(text.ToString());
        copied = true;
      } catch (Exception) {
        // The clipboard can be held open by another process. The file is the
        // part that matters.
      }

      MessageBox.Show(owner,
        "The report was saved to:\n\n" + path +
        (copied ? "\n\nIt has also been copied to the clipboard." : "") +
        "\n\nAttach this file when reporting the problem.",
        "Report Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
      return true;
    }
  }
}
