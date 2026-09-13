/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware.LowLevel;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// The guided PawnIO setup, reached from Settings › Hardware access. The
  /// work lives in <see cref="PawnIoSetupDialog"/> and the library; this only
  /// connects it to the settings page and to the restart.
  /// </summary>
  partial class MainForm {

    private void AddPawnIoSetupRow(SettingsSection access) {
      // The row refreshes its visibility and then its description; one
      // snapshot serves both.
      PawnIoSetupStatus? last = null;
      access.AddButton("Full sensor access", null, "Set up", ShowPawnIoSetup,
        ButtonKind.Primary,
        () => !(last = PawnIoSetupStatus.Capture()).IsComplete,
        () => PawnIoSetupDialog.Summarize(last ?? PawnIoSetupStatus.Capture()));
    }

    private void ShowPawnIoSetup() {
      bool restart;
      using (PawnIoSetupDialog dialog = new PawnIoSetupDialog(uiTheme)) {
        dialog.ShowDialog(this);
        restart = dialog.RestartRequested;
      }
      settingsPage?.RefreshValues();
      if (restart)
        RestartAsAdministrator();
    }
  }
}
