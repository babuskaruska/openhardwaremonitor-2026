/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System.Collections.Generic;
using System.Windows.Forms;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Fan detection and calibration, the inline curve editor and fan profiles:
  /// the rest of the Fans page's host, and the tray's profile menu.
  ///
  /// Detection is advanced by <see cref="FanCurveController.Update"/>, which
  /// PollOnSensorThread already calls, and stopped by
  /// <see cref="FanCurveController.Release"/>, which every exit path already
  /// calls - so nothing else in MainForm needs to know about it.
  /// </summary>
  partial class MainForm {

    private FanControlService? fanService;

    private FanControlService FanService {
      get {
        return fanService ??= new FanControlService(this, computer, settings, fanCurves, poller);
      }
    }

    /// <summary>Called from InitializePages, once the Fans page exists.</summary>
    private void InitializeFanControl() {
      FanService.StateChanged += delegate {
        if (fansPage != null && fansPage.Visible)
          fansPage.UpdateValues();
      };
      systemTray.AddMenuItem(CreateFanProfileMenu());
    }

    private ToolStripMenuItem CreateFanProfileMenu() {
      ToolStripMenuItem menu = new ToolStripMenuItem("Fan profile");
      List<ToolStripMenuItem> items = new List<ToolStripMenuItem>();
      foreach (FanProfileKind kind in FanProfileStore.All) {
        ToolStripMenuItem item = new ToolStripMenuItem(FanProfileStore.DisplayName(kind));
        item.Click += delegate { FanService.ApplyProfile(kind); };
        items.Add(item);
        menu.DropDownItems.Add(item);
      }
      ToolStripMenuItem none = new ToolStripMenuItem("No controllable fans were found") {
        Enabled = false,
        Visible = false
      };
      menu.DropDownItems.Add(none);
      menu.DropDownOpening += delegate {
        bool any = poller.RunLocked(() => FanService.GetControls().Count > 0);
        FanProfileKind? active = FanService.Profiles.Active;
        for (int i = 0; i < items.Count; i++) {
          items[i].Checked = active == FanProfileStore.All[i];
          items[i].Enabled = any;
        }
        none.Visible = !any;
      };
      return menu;
    }

    FanCurve? IFanControlHost.GetCurve(ISensor control) {
      return fanCurves.GetCurve(control);
    }

    void IFanControlHost.SetCurve(ISensor control, FanCurve curve) {
      FanService.SetCurve(control, curve);
    }

    FanCurve? IFanControlHost.CreateDefaultCurve(ISensor control) {
      return poller.RunLocked(() => FanService.CreateDefaultCurve(control));
    }

    IReadOnlyList<ISensor> IFanControlHost.GetTemperatureSensors() {
      return poller.RunLocked(() => FanService.GetTemperatureSensors());
    }

    bool IFanControlHost.CanCalibrate(ISensor control) {
      return !FanControlService.IsGraphicsCard(control);
    }

    FanPairing? IFanControlHost.GetPairing(ISensor control) {
      return fanCurves.Store.GetPairing(control.Identifier.ToString());
    }

    FanCalibration? IFanControlHost.GetCalibration(ISensor control) {
      return fanCurves.Store.GetCalibration(control.Identifier.ToString());
    }

    FanTuningProgress? IFanControlHost.TuningProgress {
      get { return fanCurves.Tuning?.Progress; }
    }

    FanTuningState IFanControlHost.GetTuningState(ISensor control) {
      return FanService.GetTuningState(control);
    }

    FanTuningResult? IFanControlHost.GetTuningResult(ISensor control) {
      return FanService.GetResult(control);
    }

    bool IFanControlHost.StartTuning(IReadOnlyList<ISensor> controls) {
      return FanService.StartTuning(controls);
    }

    void IFanControlHost.CancelTuning() {
      FanService.CancelTuning();
    }

    FanProfileKind? IFanControlHost.ActiveProfile {
      get { return FanService.Profiles.Active; }
    }

    void IFanControlHost.ApplyProfile(FanProfileKind profile) {
      FanService.ApplyProfile(profile);
    }

    void IFanControlHost.SaveProfile(FanProfileKind profile) {
      FanService.SaveProfile(profile);
    }
  }
}
