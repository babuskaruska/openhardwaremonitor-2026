/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Diagnostics;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// The one-click "Export for AI" command: captures a diagnostics snapshot,
  /// writes it as Markdown and JSON under Documents, copies the Markdown to the
  /// clipboard and shows the file in Explorer. Nothing leaves the machine.
  /// </summary>
  internal static class DiagnosticsExport {

    private const string Caption = "Export for AI";

    // A message box pumps messages, so a second click (for example from the
    // tray) could otherwise start another export while one is being reported.
    private static bool running;

    /// <summary>Must be called on the UI thread, which owns the sensor tree.</summary>
    public static void Run(IWin32Window? owner, IComputer computer) {
      if (running)
        return;
      running = true;
      try {
        RunCore(owner, computer);
      } catch (Exception ex) {
        // An export is never worth taking the application down for.
        ShowError(owner, ex);
      } finally {
        running = false;
      }
    }

    private static void RunCore(IWin32Window? owner, IComputer computer) {
      DiagnosticExportResult result;
      try {
        Cursor.Current = Cursors.WaitCursor;
        DiagnosticSnapshot snapshot = DiagnosticSnapshot.Capture(computer,
          new DiagnosticCaptureOptions { ApplicationName = "Open Hardware Monitor" });
        result = DiagnosticExporter.WriteFiles(snapshot,
          DiagnosticExporter.DefaultDirectory);
      } finally {
        Cursor.Current = Cursors.Default;
      }

      bool copied = TryCopyToClipboard(result.Markdown);
      TryShowInExplorer(result.MarkdownPath);

      DiagnosticSnapshot s = result.Snapshot;
      string findings = string.Format(CultureInfo.InvariantCulture,
        "{0} critical, {1} warning, {2} info",
        s.CountFindings(DiagnosticSeverity.Critical),
        s.CountFindings(DiagnosticSeverity.Warning),
        s.CountFindings(DiagnosticSeverity.Info));

      MessageBox.Show(owner,
        (copied
          ? "The diagnostics report was copied to the clipboard. Paste it into " +
            "your AI assistant and describe the problem you are seeing."
          : "The diagnostics report was saved, but the clipboard was busy. Open " +
            "the .md file and copy its contents into your AI assistant.") +
        "\n\nFindings: " + findings +
        "\n\nMarkdown: " + result.MarkdownPath +
        "\nJSON: " + result.JsonPath,
        Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static bool TryCopyToClipboard(string text) {
      try {
        // Another process can hold the clipboard open briefly; retry a little.
        Clipboard.SetDataObject(text, true, 5, 100);
        return true;
      } catch (Exception ex) when (ex is ExternalException ||
        ex is ThreadStateException || ex is ArgumentException) {
        return false;
      }
    }

    private static void TryShowInExplorer(string path) {
      try {
        string explorer = Path.Combine(Environment.GetFolderPath(
          Environment.SpecialFolder.Windows), "explorer.exe");
        ProcessStartInfo info = new ProcessStartInfo(explorer,
          "/select,\"" + path + "\"") { UseShellExecute = false };
        using (Process.Start(info)) { }
      } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception ||
        ex is InvalidOperationException || ex is IOException ||
        ex is PlatformNotSupportedException) {
        // The confirmation still shows where the files are.
      }
    }

    private static void ShowError(IWin32Window? owner, Exception ex) {
      try {
        MessageBox.Show(owner,
          "The diagnostics could not be exported:\n\n" + ex.Message +
          "\n\nFolder: " + DiagnosticExporter.DefaultDirectory,
          Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
      } catch (Exception) {
        // Nothing more can be done without a working message box.
      }
    }
  }
}
