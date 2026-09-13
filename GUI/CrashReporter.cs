/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware.Maintenance;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Handles an unhandled exception: the crash report is written to disk and
  /// the application log flushed first, and only then is a dialog shown. A
  /// crash during an unattended run is therefore never lost, even when the
  /// dialog cannot be created or nobody closes it.
  /// </summary>
  internal static class CrashReporter {

    private const int MaxSecondaryLogEntries = 5;

    private static int reporting;
    private static int secondary;

    /// <summary>Returns once the dialog is closed. Safe from any thread.</summary>
    public static void Report(Exception exception) {
      if (Interlocked.Exchange(ref reporting, 1) != 0) {
        // Often a consequence of the first crash, raised while its dialog is
        // open. Worth a log entry, but not another report or dialog.
        if (Interlocked.Increment(ref secondary) <= MaxSecondaryLogEntries) {
          ApplicationLog.Error("Another unhandled exception while reporting a crash.", exception);
          ApplicationLog.Flush();
        }
        return;
      }

      EnvironmentFacts facts;
      try {
        facts = EnvironmentFacts.Capture();
      } catch (Exception) {
        facts = new EnvironmentFacts();
      }

      string report;
      try {
        report = CrashReport.Format(exception, facts,
          ApplicationLog.Tail(CrashReport.LogLines), DateTimeOffset.Now, DescribeThread());
      } catch (Exception) {
        report = SafeScrub(exception.ToString());
      }

      string? path = null;
      try {
        path = CrashReport.Write(CrashReport.DefaultDirectory, report, DateTimeOffset.Now);
        ApplicationLog.Error("Unhandled " + exception.GetType().FullName +
          ". Crash report: " + Path.GetFileName(path));
      } catch (Exception ex) {
        ApplicationLog.Error("Unhandled exception. The crash report could not be written (" +
          ex.Message + ").", exception);
      }
      ApplicationLog.Flush();

      string issueUrl;
      try {
        issueUrl = IssueLink.BugReport(facts);
      } catch (Exception) {
        issueUrl = IssueLink.NewIssueUrl;
      }
      ShowDialog(report, path, issueUrl);
    }

    private static string DescribeThread() {
      Thread thread = Thread.CurrentThread;
      return (string.IsNullOrEmpty(thread.Name) ? "Unnamed thread" : thread.Name) +
        " (" + thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static string SafeScrub(string text) {
      try {
        return PrivacyFilter.Current.Scrub(text);
      } catch (Exception) {
        return "(The report could not be created.)";
      }
    }

    private static void ShowDialog(string report, string? path, string issueUrl) {
      try {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) {
          ShowDialogCore(report, path, issueUrl);
          return;
        }
        // Worker threads are MTA and cannot host a window; give the dialog
        // its own thread and wait, because the process ends when this returns.
        Thread thread = new Thread(() => ShowDialogCore(report, path, issueUrl)) {
          Name = "Crash dialog"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
      } catch (Exception ex) {
        ApplicationLog.Error("The crash dialog could not be shown.", ex);
        ApplicationLog.Flush();
      }
    }

    private static void ShowDialogCore(string report, string? path, string issueUrl) {
      try {
        using (CrashForm form = new CrashForm(report, path, issueUrl))
          form.ShowDialog();
      } catch (Exception ex) {
        ApplicationLog.Error("The crash dialog could not be shown.", ex);
        ApplicationLog.Flush();
        try {
          MessageBox.Show("Open Hardware Monitor stopped working." +
            (path != null ? "\n\nA crash report was saved to:\n" + path : ""),
            "Open Hardware Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        } catch (Exception) {
          // The report is on disk; nothing more can be shown.
        }
      }
    }
  }
}
