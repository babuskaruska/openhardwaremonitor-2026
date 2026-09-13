/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Diagnostics;
using OpenHardwareMonitor.Hardware.Alerts;
using OpenHardwareMonitor.Hardware.Maintenance;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// "Report a problem": exports the diagnostics, opens a new GitHub bug
  /// report with the version, Windows version and access tier filled in, and
  /// shows the exported file in Explorer so it can be attached. Nothing is
  /// sent until the user submits the issue.
  /// </summary>
  internal static class ProblemReporter {

    private const string Caption = "Report a problem";

    /// <summary>Must be called on the UI thread, which owns the sensor tree.</summary>
    public static void Run(IWin32Window? owner, IComputer computer, object hardwareLock,
      IReadOnlyList<AlertRecord>? recentAlerts = null) {
      DiagnosticsExport.Run(owner, computer, hardwareLock, recentAlerts, Present);
    }

    private static void Present(IWin32Window? owner, DiagnosticExportResult result) {
      bool copied = DiagnosticsExport.TryCopyToClipboard(result.Markdown);
      DiagnosticsExport.TryShowInExplorer(result.MarkdownPath);
      bool opened = ShellLauncher.OpenUrl(IssueLink.BugReport(EnvironmentFacts.Capture()));
      ApplicationLog.Info("Report a problem: diagnostics exported as " +
        Path.GetFileName(result.MarkdownPath) + ".");

      MessageBox.Show(owner,
        (opened
          ? "A new GitHub issue is opening in your browser, with the version and " +
            "Windows version filled in."
          : "The browser could not be opened. Report the problem at " +
            IssueLink.NewIssueUrl + ".") +
        "\n\nFile Explorer shows the diagnostics report. Drag the file into the issue" +
        (copied ? ", or paste it into the diagnostics field. It is already on the clipboard."
          : ".") +
        "\n\nThe report lists your hardware and sensor readings. Nothing is sent until " +
        "you submit the issue." +
        "\n\nFile: " + result.MarkdownPath,
        Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
  }
}
