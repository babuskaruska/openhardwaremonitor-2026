/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Utilities;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Page navigation (Overview, Sensors, Fans, Settings), the Fans page's
  /// connection to the hardware, and every setting in one place. Replaces the
  /// classic menu bar, whose items remain as the backing options.
  /// </summary>
  partial class MainForm : IFanControlHost {

    private static readonly bool isElevated = DetectElevation();

    private NavigationBar navigation = null!;
    private FansPanel? fansPage;
    private SettingsPanel? settingsPage;
    private Control[] pages = Array.Empty<Control>();
    private int currentPage;

    private static bool DetectElevation() {
      try {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
          return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
      } catch (Exception) {
        return false;
      }
    }

    /// <summary>
    /// Called at the end of the constructor, once every option and the web
    /// server exist, so the settings page can bind to all of them.
    /// </summary>
    private void InitializePages() {
      Animator.UserDisabled = !settings.GetValue("animations", true);
      poller.IntervalMilliseconds = settings.GetValue("sensorUpdateInterval", 1000);

      navigation = new NavigationBar {
        Dock = DockStyle.Top,
        Theme = uiTheme,
        Tabs = new[] { "Overview", "Sensors", "Fans", "Settings" }
      };
      navigation.SelectedIndexChanged += delegate { ShowPage(navigation.SelectedIndex); };
      navigation.ExportClicked += delegate { exportDiagnosticsMenuItem_Click(this, EventArgs.Empty); };
      navigation.MoreClicked += delegate(object? sender, Point location) { ShowMoreMenu(location); };

      fansPage = new FansPanel(computer, this) {
        Dock = DockStyle.Fill,
        Theme = uiTheme,
        Visible = false
      };
      settingsPage = new SettingsPanel {
        Dock = DockStyle.Fill,
        Theme = uiTheme,
        Visible = false
      };
      InitializeMaintenance();
      BuildSettings(settingsPage);
      InitializeFanControl();

      SuspendLayout();
      Controls.Add(fansPage);
      Controls.SetChildIndex(fansPage, 0);
      Controls.Add(settingsPage);
      Controls.SetChildIndex(settingsPage, 0);
      // The last child docks first, so the bar sits above every page.
      Controls.Add(navigation);
      Controls.SetChildIndex(navigation, Controls.Count - 1);
      mainMenu.Visible = false;
      ResumeLayout(true);

      // The navigation bar carries these actions now.
      overview.ShowExportButton = false;
      overview.ShowDetailsButton = false;
      detailsHeader.Visible = false;

      pages = new Control[] { overview, splitContainer, fansPage, settingsPage };
      currentPage = splitContainer.Visible ? 1 : 0;
      navigation.SelectedIndex = currentPage;
    }

    private void ShowPage(int index) {
      if (pages.Length == 0 || index < 0 || index >= pages.Length)
        return;
      navigation.SelectedIndex = index;
      if (index == currentPage && pages[index].Visible)
        return;

      Control from = pages[currentPage];
      Control to = pages[index];
      bool forward = index > currentPage;
      currentPage = index;

      if (to == fansPage)
        fansPage.UpdateValues();
      if (to == settingsPage)
        settingsPage.RefreshValues();

      ViewTransition.Switch(from, to, forward);
      overviewMenuItem.Checked = index == 0;
      allSensorsMenuItem.Checked = index == 1;
      if (to == splitContainer)
        treeView.Focus();
    }

    private void ShowMoreMenu(Point screenLocation) {
      ContextMenuStrip menu = new ContextMenuStrip();
      AddUpdateMenuItem(menu);
      menu.Items.Add(new ToolStripMenuItem("Save technical report…", null,
        delegate { saveReportMenuItem_Click(this, EventArgs.Empty); }));
      menu.Items.Add(new ToolStripMenuItem("Reset minimum and maximum values", null,
        delegate { resetMinMaxMenuItem_Click(this, EventArgs.Empty); }));
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add(CheckItem("Show plot", showPlot.Value, value => showPlot.Value = value));
      if (gadget != null)
        menu.Items.Add(CheckItem("Show desktop gadget", showGadget.Value,
          value => showGadget.Value = value));
      menu.Items.Add(CheckItem("Show hidden sensors", showHiddenSensors.Value,
        value => showHiddenSensors.Value = value));
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add(new ToolStripMenuItem("Rescan hardware", null,
        delegate { resetClick(this, EventArgs.Empty); }));
      menu.Items.Add(CreateReportProblemMenuItem());
      menu.Items.Add(new ToolStripMenuItem("About Open Hardware Monitor", null,
        delegate { aboutMenuItem_Click(this, EventArgs.Empty); }));
      menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Close(); }));
      menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
      menu.Show(screenLocation, ToolStripDropDownDirection.BelowLeft);
    }

    private static ToolStripMenuItem CheckItem(string text, bool isChecked, Action<bool> set) {
      ToolStripMenuItem item = new ToolStripMenuItem(text) { Checked = isChecked };
      item.Click += delegate { set(!item.Checked); };
      return item;
    }

    // ---- fans -----------------------------------------------------------------

    string? IFanControlHost.AccessNote {
      get {
        if (!isElevated)
          return "Changing fan speeds needs Open Hardware Monitor to run as administrator (Settings › Hardware access).";
        if (!HardwareAccess.SupportsIoPort)
          return "Motherboard fans need PawnIO. Graphics card fans work without it.";
        return null;
      }
    }

    FanMode IFanControlHost.GetMode(ISensor control) {
      if (fanCurves.HasCurve(control))
        return FanMode.Curve;
      return control.Control != null && control.Control.ControlMode == ControlMode.Software
        ? FanMode.Fixed : FanMode.Automatic;
    }

    string? IFanControlHost.DescribeCurve(ISensor control) {
      FanCurve? curve = fanCurves.GetCurve(control);
      if (curve == null)
        return null;
      string source = FindSensorName(curve.SourceSensorIdentifier) ??
        "a sensor that is no longer available";
      return "Follows " + source + "  ·  " +
        curve.Points.Count.ToString(CultureInfo.CurrentCulture) + " points";
    }

    void IFanControlHost.SetAutomatic(ISensor control) {
      poller.RunLocked(() => {
        fanCurves.RemoveCurve(control);
        control.Control?.SetDefault();
      });
    }

    void IFanControlHost.SetFixed(ISensor control, float percent) {
      poller.RunLocked(() => {
        fanCurves.RemoveCurve(control);
        control.Control?.SetSoftware(fanCurves.LimitDuty(control, percent, false));
      });
    }

    void IFanControlHost.EditCurve(ISensor control) {
      ShowFanCurveForm(control);
    }

    private string? FindSensorName(string identifier) {
      string? name = null;
      computer.Accept(new SensorVisitor(sensor => {
        if (name == null && sensor.Identifier.ToString() == identifier)
          name = sensor.Hardware.Name + " · " + sensor.Name;
      }));
      return name;
    }

    // ---- settings ---------------------------------------------------------------

    private void BuildSettings(SettingsPanel page) {
      SettingsSection general = page.AddSection("General");
      if (startupManager.IsAvailable)
        general.AddToggle("Start with Windows", "Open Hardware Monitor starts when you sign in.",
          () => autoStart.Value, value => autoStart.Value = value);
      general.AddToggle("Start minimized", "Open in the notification area instead of a window.",
        () => startMinimized.Value, value => startMinimized.Value = value);
      general.AddToggle("Minimize to the notification area",
        "Minimizing hides the window; the icon stays next to the clock.",
        () => minimizeToTray.Value, value => minimizeToTray.Value = value);
      general.AddToggle("Keep running when the window is closed",
        "Closing the window keeps sensors, logging and fan control running.",
        () => minimizeOnClose.Value, value => minimizeOnClose.Value = value);
      int[] intervals = { 500, 1000, 2000, 5000 };
      general.AddChoice("Update interval",
        "How often sensors are read. Longer intervals use less power.",
        new[] { "0.5 s", "1 s", "2 s", "5 s" },
        () => Array.IndexOf(intervals, poller.IntervalMilliseconds),
        index => {
          poller.IntervalMilliseconds = intervals[index];
          settings.SetValue("sensorUpdateInterval", intervals[index]);
        });

      SettingsSection appearance = page.AddSection("Appearance");
      appearance.AddChoice("Theme", "Takes effect the next time Open Hardware Monitor starts.",
        new[] { "Follow Windows", "Light", "Dark" },
        () => theme.Value, index => theme.Value = index);
      appearance.AddChoice("Temperature unit", null, new[] { "°C", "°F" },
        () => unitManager.TemperatureUnit == TemperatureUnit.Fahrenheit ? 1 : 0,
        index => {
          if (index == 1)
            fahrenheitMenuItem_Click(this, EventArgs.Empty);
          else
            celsiusMenuItem_Click(this, EventArgs.Empty);
        });
      appearance.AddToggle("Animations",
        "Smooth motion throughout the interface. Also follows the Windows animation effects setting.",
        () => !Animator.UserDisabled,
        value => {
          Animator.UserDisabled = !value;
          settings.SetValue("animations", value);
        });
      if (gadget != null)
        appearance.AddToggle("Desktop gadget", "A small window on the desktop showing chosen sensors.",
          () => showGadget.Value, value => showGadget.Value = value);

      SettingsSection sensors = page.AddSection("Sensors",
        "Turn off hardware you do not need; every update then does less work.");
      sensors.AddToggle("Processor", null, () => readCpuSensors.Value,
        value => readCpuSensors.Value = value);
      sensors.AddToggle("Graphics cards", null, () => readGpuSensors.Value,
        value => readGpuSensors.Value = value);
      sensors.AddToggle("Memory", null, () => readRamSensors.Value,
        value => readRamSensors.Value = value);
      sensors.AddToggle("Motherboard", "Fans, voltages and board temperatures.",
        () => readMainboardSensors.Value, value => readMainboardSensors.Value = value);
      sensors.AddToggle("Storage", "Drive temperatures, health and space.",
        () => readHddSensors.Value, value => readHddSensors.Value = value);
      sensors.AddToggle("External fan controllers", "T-Balancer and Heatmaster devices.",
        () => readFanControllersSensors.Value, value => readFanControllersSensors.Value = value);
      sensors.AddToggle("Show hidden sensors", "Include sensors hidden by default or by you.",
        () => showHiddenSensors.Value, value => showHiddenSensors.Value = value);
      sensors.AddButton("Minimum and maximum values", "Start recording lows and highs again.",
        "Reset", () => resetMinMaxMenuItem_Click(this, EventArgs.Empty));

      SettingsSection logging = page.AddSection("Logging");
      logging.AddToggle("Log sensors to a file", "Writes every sensor to a CSV file in the log folder.",
        () => logSensors.Value, value => logSensors.Value = value);
      int[] logIntervals = { 0, 2, 4, 5, 7, 9 };
      logging.AddChoice("Log interval", null,
        new[] { "1 s", "5 s", "30 s", "1 min", "5 min", "30 min" },
        () => Array.IndexOf(logIntervals, loggingInterval.Value),
        index => loggingInterval.Value = logIntervals[index]);
      logging.AddButton("Log folder", Logger.LogDirectory, "Open",
        () => OpenFolder(Logger.LogDirectory));

      if (!server.PlatformNotSupported) {
        SettingsSection web = page.AddSection("Web server",
          "A live dashboard and JSON API for browsers, Home Assistant, Grafana and similar tools.");
        web.AddToggle("Run the web server", null, () => runWebServer.Value,
          value => runWebServer.Value = value);
        web.AddToggle("Allow connections from other devices",
          "Other devices use a link that contains an access token. Needs administrator rights.",
          () => server.AllowRemoteConnections, SetAllowRemoteConnections);
        web.AddButton("Port and connection link", null, "Change",
          () => serverPortMenuItem_Click(this, EventArgs.Empty));
        web.AddInfo("Address on this PC",
          () => server.GetUrl("localhost", server.ListenerPort, false));
      }

      SettingsSection access = page.AddSection("Hardware access");
      access.AddInfo("Sensor access", () => HardwareAccess.Tier == AccessTier.Deep
        ? "Full access through " + HardwareAccess.BackendName + "." +
          (HardwareAccess.UnavailableReason != null ? " " + HardwareAccess.UnavailableReason : "")
        : "Basic access. " + (HardwareAccess.UnavailableReason ?? ""));
      AddPawnIoSetupRow(access); // Guided PawnIO setup: MainForm.PawnIoSetup.cs
      access.AddInfo("Administrator rights", () => isElevated
        ? "Running as administrator."
        : "Not running as administrator: processor temperatures, motherboard sensors and fan control are unavailable.");
      access.AddInfo("Sensor update time", () => "The last update of all sensors took " +
        poller.LastUpdateMilliseconds.ToString("0", CultureInfo.CurrentCulture) + " ms.");
      access.AddButton("Restart as administrator",
        "Enables processor temperatures, motherboard sensors and fan control.",
        "Restart", RestartAsAdministrator, ButtonKind.Primary, () => !isElevated);
      string modules = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenHardwareMonitor", "PawnIOModules");
      access.AddButton("PawnIO modules", modules, "Open folder", () => OpenFolder(modules));

      BuildUpdateSettings(page);

      SettingsSection about = page.AddSection("Diagnostics and about");
      about.AddButton("Export for AI",
        "Saves every sensor with automatic findings, ready to paste into an AI assistant.",
        "Export", () => exportDiagnosticsMenuItem_Click(this, EventArgs.Empty), ButtonKind.Primary);
      about.AddButton("Report a problem",
        "Exports the diagnostics and opens a new bug report on GitHub. Nothing is sent until you submit it.",
        "Report", ReportProblem);
      about.AddButton("Technical report", "A detailed text report of all detected hardware.",
        "Save", () => saveReportMenuItem_Click(this, EventArgs.Empty));
      about.AddButton("About", "Version " + Application.ProductVersion, "Show",
        () => aboutMenuItem_Click(this, EventArgs.Empty));
    }

    private void SetAllowRemoteConnections(bool allow) {
      if (allow == server.AllowRemoteConnections)
        return;
      if (allow && MessageBox.Show(this,
        "Other devices on your network will be able to read your sensors using a " +
        "link that contains an access token (Settings › Web server › Port and " +
        "connection link).\n\nListening on the network requires running as administrator.",
        "Allow connections from other devices", MessageBoxButtons.OKCancel,
        MessageBoxIcon.Information) != DialogResult.OK)
        return;
      server.AllowRemoteConnections = allow;
      if (runWebServer.Value && !server.IsListening) {
        string? error = server.LastError;
        runWebServer.Value = false;
        if (error != null)
          MessageBox.Show(this, "The web server could not be restarted:\n\n" + error,
            "Web server", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void OpenFolder(string path) {
      try {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
      } catch (Exception ex) {
        MessageBox.Show(this, "The folder could not be opened:\n\n" + ex.Message,
          "Open Hardware Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    /// <summary>
    /// Starts an elevated copy and closes this one. The new copy waits for this
    /// process to exit before claiming the single-instance lock.
    /// </summary>
    private void RestartAsAdministrator() {
      string? executable = Environment.ProcessPath;
      if (executable == null)
        return;
      string arguments = "--wait-for-pid " +
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
      // Development runs go through the dotnet host.
      if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet",
        StringComparison.OrdinalIgnoreCase))
        arguments = "\"" + Path.Combine(AppContext.BaseDirectory, typeof(MainForm).Assembly.GetName().Name + ".dll") + "\" " + arguments;
      try {
        Process.Start(new ProcessStartInfo(executable, arguments) {
          UseShellExecute = true,
          Verb = "runas"
        });
      } catch (Win32Exception) {
        // The administrator prompt was declined.
        return;
      }
      Close();
    }
  }
}
