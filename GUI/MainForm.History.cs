/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Windows.Forms;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Sensor history: the history page, the ways to open it, and saving the
  /// recorded history while running.
  ///
  /// The history is a page in the main window rather than a separate window.
  /// It drills into one sensor from wherever it was opened, the way More info
  /// drills into hardware, and Back or Esc returns there; no second window to
  /// theme, place and keep track of. It is not a navigation tab, so the tab
  /// it was opened from stays selected, and choosing any tab leaves it.
  /// </summary>
  partial class MainForm {

    private HistoryPanel? historyPage;
    private HistoryAutosave? historyAutosave;

    /// <summary>Called at the end of the constructor, once the pages exist.</summary>
    private void InitializeHistory() {
      historyAutosave = new HistoryAutosave(computer, poller.Sync, settings,
        GetConfigurationPath());
      // FormClosing runs before FormClosed, where the final save happens; a
      // periodic save still running must not land after it.
      FormClosing += delegate { historyAutosave.WaitForPendingSave(); };
      FormClosed += delegate { historyAutosave.Dispose(); };

      historyPage = new HistoryPanel(computer, poller.Sync, unitManager) {
        Dock = DockStyle.Fill,
        Theme = uiTheme,
        Visible = false,
        RangeIndex = settings.GetValue("historyRange", 1)
      };
      historyPage.BackClicked += delegate { ShowPage(currentPage); };
      historyPage.RangeIndexChanged += delegate {
        settings.SetValue("historyRange", historyPage.RangeIndex);
      };
      historyPage.SensorChanged += delegate {
        if (historyPage.Sensor != null)
          settings.SetValue("historySensor", historyPage.Sensor.Identifier.ToString());
      };
      SuspendLayout();
      Controls.Add(historyPage);
      // Index 0 docks last, below the navigation bar.
      Controls.SetChildIndex(historyPage, 0);
      ResumeLayout(true);

      overview.HistoryRequested += delegate(object? sender, ISensor sensor) {
        ShowHistory(sensor);
      };

      // The history page replaces the old plot, which the interface no longer
      // offers. Do not leave it open from an earlier session with no way to
      // close it.
      if (showPlot.Value)
        showPlot.Value = false;
    }

    /// <summary>The history page while it is showing, for ShowPage to leave from.</summary>
    private Control? VisibleHistoryPage {
      get { return historyPage != null && historyPage.Visible ? historyPage : null; }
    }

    /// <summary>Opens the history of a sensor over the current page.</summary>
    private void ShowHistory(ISensor? sensor) {
      if (historyPage == null)
        return;
      historyPage.BackText = currentPage < navigation.Tabs.Length
        ? navigation.Tabs[currentPage] : "Back";
      historyPage.ShowSensor(sensor);
      if (!historyPage.Visible)
        ViewTransition.Switch(pages[currentPage], historyPage, true);
      historyPage.Focus();
    }

    /// <summary>In the refresh after each sensor update.</summary>
    private void UpdateHistoryPage() {
      if (historyPage != null && historyPage.Visible)
        historyPage.UpdateValues();
    }

    /// <summary>Esc or Alt+Left on the history page returns to where it was opened.</summary>
    private bool ProcessHistoryKey(Keys keyData) {
      if (VisibleHistoryPage == null)
        return false;
      if (keyData == Keys.Escape || keyData == (Keys.Alt | Keys.Left)) {
        ShowPage(currentPage);
        return true;
      }
      return false;
    }

    /// <summary>
    /// "⋯" › Sensor history: the sensor selected in the list, else the one
    /// viewed last, else the processor temperature.
    /// </summary>
    private void ShowHistoryFromMenu() {
      ISensor? sensor = null;
      if (currentPage == 1 && treeView.SelectedNode?.Tag is SensorNode node)
        sensor = node.Sensor;
      if (sensor == null) {
        string last = settings.GetValue("historySensor", "");
        ISensor? found = null, best = null;
        int bestScore = -1;
        poller.RunLocked(() => computer.Accept(new SensorVisitor(delegate(ISensor candidate) {
          if (found == null && candidate.Identifier.ToString() == last)
            found = candidate;
          int score = candidate.SensorType != SensorType.Temperature ? 0
            : candidate.Hardware.HardwareType != HardwareType.CPU ? 1
            : candidate.Name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ? 3 : 2;
          if (score > bestScore) {
            best = candidate;
            bestScore = score;
          }
        })));
        sensor = found ?? best;
      }
      ShowHistory(sensor);
    }
  }
}
