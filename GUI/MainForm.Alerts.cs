/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Alerts;
using OpenHardwareMonitor.Hardware.Diagnostics;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Alerts in the interface. The engine lives in the library and runs on the
  /// sensor thread after every update (PollOnSensorThread); this part turns
  /// its alerts into Windows notifications, the overview chip, a settings
  /// section and the rule and history dialogs.
  /// </summary>
  partial class MainForm {

    private const string AlertNotificationsSetting = "alerts.notifications";

    private AlertEngine alerts = null!;

    /// <summary>Called from InitializePages, before sensor polling starts.</summary>
    private void InitializeAlerts() {
      alerts = new AlertEngine(computer, settings) { FormatValue = FormatAlertValue };
      alerts.AlertRaised += OnAlertRaised;
      overview.AlertStatus = GetAlertStatus;
      overview.AlertsRequested += delegate { ShowRecentAlerts(); };
    }

    private (int Count, Severity Severity) GetAlertStatus() {
      int count = alerts.ActiveCount;
      Severity severity = alerts.HighestActiveSeverity == DiagnosticSeverity.Critical
        ? Severity.Hot : Severity.Warm;
      return (count, severity);
    }

    // Called on the sensor thread; it only reads the unit setting.
    private string FormatAlertValue(float value, SensorType type) {
      return AlertValueFormat.Format(value, type, unitManager.TemperatureUnit);
    }

    /// <summary>Raised on the sensor thread; the tray marshals to the UI thread.</summary>
    private void OnAlertRaised(object? sender, AlertRecord record) {
      if (!record.Notify || !settings.GetValue(AlertNotificationsSetting, true))
        return;
      systemTray.ShowNotification(record.Title, record.Message,
        record.Severity == DiagnosticSeverity.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning,
        BringWindowForward);
    }

    private void BringWindowForward() {
      if (!Visible)
        Visible = true;
      if (WindowState == FormWindowState.Minimized)
        WindowState = FormWindowState.Normal;
      Activate();
    }

    // ---- settings ---------------------------------------------------------------

    private void BuildAlertSettings(SettingsPanel page) {
      SettingsSection section = page.AddSection("Alerts",
        "Readings are checked after every update. You are told when something needs attention.");
      section.AddToggle("Watch for problems", "Check sensors against the rules below.",
        () => alerts.Enabled, value => alerts.Enabled = value);
      section.AddToggle("Show notifications",
        "A Windows notification when an alert fires. The same alert repeats at most every " +
        alerts.Cooldown.TotalMinutes.ToString("0", CultureInfo.CurrentCulture) + " minutes.",
        () => settings.GetValue(AlertNotificationsSetting, true),
        value => settings.SetValue(AlertNotificationsSetting, value),
        () => alerts.Enabled);

      foreach (AlertRuleInfo rule in AlertEngine.BuiltInRules) {
        section.AddToggle(rule.Title, DescribeAlertRule(rule),
          () => alerts.IsRuleEnabled(rule.Id), value => alerts.SetRuleEnabled(rule.Id, value),
          () => alerts.Enabled);
        // Descriptions quote temperatures, which follow the unit setting.
        BindDescription(section, () => DescribeAlertRule(rule));
      }

      section.AddButton("Custom rules", DescribeCustomRules(), "Manage", ShowAlertRulesDialog);
      BindDescription(section, DescribeCustomRules);
      section.AddInfo("Latest alert", DescribeLatestAlert);
      section.AddButton("Recent alerts",
        "The last " + AlertEngine.MaxRecentAlerts.ToString(CultureInfo.CurrentCulture) +
        " alerts since Open Hardware Monitor started.", "Show", ShowRecentAlerts);
    }

    /// <summary>
    /// Lets the row just added re-read its description whenever the page
    /// refreshes. The settings API only offers that for info rows, so this
    /// reaches the row directly; it is always the section's last child.
    /// </summary>
    private static void BindDescription(SettingsSection section, Func<string> description) {
      if (section.Controls.Count > 0 &&
        section.Controls[section.Controls.Count - 1] is SettingRow row)
        row.DescriptionProvider = description;
    }

    private string DescribeAlertRule(AlertRuleInfo rule) {
      TemperatureUnit unit = unitManager.TemperatureUnit;
      string duration = AlertValueFormat.Duration((int)rule.Duration.TotalSeconds);
      switch (rule.Id) {
        case DiagnosticRules.CpuTemperatureNearTjMax:
          return "Within " + AlertValueFormat.TemperatureDifference(
            DiagnosticThresholds.CpuTjMaxWarningMargin, unit) +
            " of the throttling limit (TjMax) for " + duration + ".";
        case DiagnosticRules.GpuTemperatureHigh:
          return "Graphics core at " + AlertValueFormat.Format(DiagnosticThresholds.GpuCoreWarning,
            SensorType.Temperature, unit) + " or hotter for " + duration + ".";
        case DiagnosticRules.StorageTemperatureHigh:
          return "A drive at " + AlertValueFormat.Format(DiagnosticThresholds.StorageTemperatureWarning,
            SensorType.Temperature, unit) + " or hotter for " + duration + ".";
        case DiagnosticRules.FanStoppedWhileHot:
          return "A fan stays at 0 RPM for " + duration + " while its hardware is above " +
            AlertValueFormat.Format(DiagnosticThresholds.FanHotTemperature,
              SensorType.Temperature, unit) + ".";
        case DiagnosticRules.StorageMediaErrors:
          return "A drive reports more media errors than last time. Errors it already had do not alert again.";
        case DiagnosticRules.StorageSpareLow:
          return "SSD spare capacity below " + AlertValueFormat.Format(
            DiagnosticThresholds.StorageSpareWarning, SensorType.Level, unit) +
            " or below the drive's own limit.";
        case DiagnosticRules.VoltageOutOfRange:
          return "A 3.3, 5 or 12 V supply rail more than " + AlertValueFormat.Format(
            DiagnosticThresholds.RailTolerance * 100, SensorType.Level, unit) +
            " off for " + duration + ".";
        case DiagnosticRules.CmosBatteryLow:
          return "The motherboard coin cell below " + AlertValueFormat.Format(
            DiagnosticThresholds.CmosBatteryLow, SensorType.Voltage, unit) +
            " for " + duration + ".";
        default:
          return "";
      }
    }

    private string DescribeCustomRules() {
      int count = alerts.CustomRules.Count;
      const string What = "Alert when a sensor you choose goes above or below a value.";
      if (count == 0)
        return What;
      return (count == 1 ? "1 rule. " : count.ToString(CultureInfo.CurrentCulture) + " rules. ") + What;
    }

    private string DescribeLatestAlert() {
      AlertRecord? latest = alerts.LatestAlert;
      if (latest == null)
        return alerts.Enabled ? "None since Open Hardware Monitor started." : "Alerts are off.";
      return AlertValueFormat.Time(latest.Time) + "  ·  " + latest.Title;
    }

    // ---- dialogs ----------------------------------------------------------------

    private void ShowAlertRulesDialog() {
      List<AlertSensorChoice> sensors = poller.RunLocked(() => AlertSensorChoice.Collect(computer));
      using (AlertRulesDialog dialog = new AlertRulesDialog(uiTheme, alerts, sensors, unitManager))
        dialog.ShowDialog(this);
      settingsPage?.RefreshValues();
    }

    private void ShowRecentAlerts() {
      using (RecentAlertsDialog dialog = new RecentAlertsDialog(uiTheme, alerts.GetRecentAlerts()))
        dialog.ShowDialog(this);
    }
  }
}
