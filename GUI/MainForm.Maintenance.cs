/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenHardwareMonitor.GUI.Modern;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Maintenance;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// What keeps a long-running installation understandable and current: the
  /// update check (notify only), "Report a problem", and application log
  /// entries for closing, sleep and sign-out.
  /// </summary>
  partial class MainForm {

    // The first automatic check waits until start-up has settled. After that
    // the timer only asks; UpdateChecker allows one request per 24 hours.
    private const int FirstUpdateCheckDelayMilliseconds = 30 * 1000;
    private const int UpdateTimerIntervalMilliseconds = 60 * 60 * 1000;

    private UpdateChecker updateChecker = null!;
    private System.Windows.Forms.Timer? updateTimer;
    private readonly CancellationTokenSource updateCancellation = new CancellationTokenSource();
    private SemanticVersion? announcedUpdate;

    /// <summary>
    /// Raised on the UI thread the first time in a session that a newer,
    /// not skipped release is known, including one found in an earlier
    /// session. Meant for an unobtrusive notification such as a tray balloon.
    /// </summary>
    internal event EventHandler<ReleaseInfo>? UpdateAvailable;

    /// <summary>The release to offer, or null. Read on the UI thread.</summary>
    internal ReleaseInfo? AvailableUpdate {
      get { return updateChecker?.AvailableUpdate; }
    }

    /// <summary>Called from InitializePages, before the settings page is built.</summary>
    private void InitializeMaintenance() {
      updateChecker = new UpdateChecker(settings, Application.ProductVersion);
      updateChecker.StateChanged += delegate {
        if (!UiThread.Redirect(OnUpdateStateChanged))
          OnUpdateStateChanged();
      };

      updateTimer = new System.Windows.Forms.Timer {
        Interval = FirstUpdateCheckDelayMilliseconds
      };
      updateTimer.Tick += delegate {
        updateTimer.Interval = UpdateTimerIntervalMilliseconds;
        // Never throws; the result arrives through StateChanged.
        _ = updateChecker.CheckIfDueAsync(updateCancellation.Token);
      };
      updateTimer.Start();

      SystemEvents.PowerModeChanged += LogPowerModeChanged;
      SystemEvents.SessionEnded += LogSessionEnded;
      FormClosing += LogClosing;
      FormClosed += delegate {
        updateTimer.Stop();
        updateTimer.Dispose();
        updateCancellation.Cancel();
        // Static events would keep the form alive.
        SystemEvents.PowerModeChanged -= LogPowerModeChanged;
        SystemEvents.SessionEnded -= LogSessionEnded;
      };

      // A newer release is announced once per session in the notification area;
      // clicking the notification opens its release page.
      UpdateAvailable += (sender, update) => systemTray.ShowNotification(
        "Open Hardware Monitor " + update.Tag + " is available",
        "Click to open the release page. Nothing is downloaded automatically.",
        ToolTipIcon.Info, OpenAvailableUpdate);

      // Announce an update found in an earlier session once subscribers exist.
      SynchronizationContext.Current?.Post(_ => OnUpdateStateChanged(), null);
    }

    private void OnUpdateStateChanged() {
      if (IsDisposed)
        return;
      if (settingsPage != null && settingsPage.Visible)
        settingsPage.RefreshValues();
      ReleaseInfo? update = AvailableUpdate;
      if (update != null && !update.Version.Equals(announcedUpdate)) {
        announcedUpdate = update.Version;
        UpdateAvailable?.Invoke(this, update);
      }
    }

    // ---- settings -----------------------------------------------------------------

    private void BuildUpdateSettings(SettingsPanel page) {
      SettingsSection updates = page.AddSection("Updates",
        "Open Hardware Monitor never downloads or installs anything by itself.");
      updates.AddToggle("Check for updates", "Looks for a new release on GitHub once a day.",
        () => updateChecker.AutomaticChecks, value => updateChecker.AutomaticChecks = value);
      updates.AddInfo("Status", DescribeUpdateStatus);
      updates.AddButton("Check for a new version", null, "Check now",
        () => { _ = updateChecker.CheckNowAsync(updateCancellation.Token); });
      updates.AddButton("Get the new version", "Opens the release page in your browser.",
        "Download", OpenAvailableUpdate, ButtonKind.Primary);
      updates.ShowLastRowWhen(() => AvailableUpdate != null);
      updates.AddButton("Skip this version",
        "No more reminders about this version. A later version is still shown.",
        "Skip", SkipAvailableUpdate);
      updates.ShowLastRowWhen(() => AvailableUpdate != null);
    }

    private string DescribeUpdateStatus() {
      UpdateChecker checker = updateChecker;
      if (checker.IsChecking)
        return "Checking…";

      string running = checker.RunningVersion?.ToString() ?? Application.ProductVersion;
      ReleaseInfo? update = checker.AvailableUpdate;
      ReleaseInfo? latest = checker.LatestRelease;
      DateTimeOffset? lastCheck = checker.LastCheck;
      string status;
      if (update != null)
        status = "Version " + update.Version + " is available. You have " + running + ".";
      else if (checker.LastError != null)
        status = "The last check failed. " + checker.LastError;
      else if (latest != null && UpdateChecker.IsNewer(latest.Version, checker.RunningVersion))
        status = "You skipped version " + latest.Version + ". You have " + running + ".";
      else if (latest != null)
        status = "You have the latest version, " + running + ".";
      else if (lastCheck.HasValue)
        status = "No release information yet. You have version " + running + ".";
      else
        status = (checker.AutomaticChecks ? "Not checked yet." : "Automatic checks are off.") +
          " You have version " + running + ".";
      if (lastCheck.HasValue)
        status += " Last checked " + FormatWhen(lastCheck.Value) + ".";
      return status;
    }

    private static string FormatWhen(DateTimeOffset time) {
      DateTime local = time.ToLocalTime().DateTime;
      string clock = local.ToString("t", CultureInfo.CurrentCulture);
      if (local.Date == DateTime.Today)
        return "today at " + clock;
      if (local.Date == DateTime.Today.AddDays(-1))
        return "yesterday at " + clock;
      return local.ToString("d", CultureInfo.CurrentCulture) + " at " + clock;
    }

    private void OpenAvailableUpdate() {
      ReleaseInfo? update = AvailableUpdate;
      if (update == null)
        return;
      if (!ShellLauncher.OpenUrl(update.PageUrl))
        MessageBox.Show(this, "The browser could not be opened. The new version is at:\n\n" +
          update.PageUrl, "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SkipAvailableUpdate() {
      ReleaseInfo? update = AvailableUpdate;
      if (update != null)
        updateChecker.SkipVersion(update);
    }

    // ---- ⋯ menu -----------------------------------------------------------------------

    /// <summary>An available update, at the top of the menu. Adds nothing otherwise.</summary>
    private void AddUpdateMenuItem(ContextMenuStrip menu) {
      ReleaseInfo? update = AvailableUpdate;
      if (update == null)
        return;
      menu.Items.Add(new ToolStripMenuItem("Version " + update.Version + " is available",
        null, delegate { OpenAvailableUpdate(); }) {
        ToolTipText = "Opens the release page in your browser."
      });
      menu.Items.Add(new ToolStripSeparator());
    }

    private ToolStripMenuItem CreateReportProblemMenuItem() {
      return new ToolStripMenuItem("Report a problem…", null, delegate { ReportProblem(); });
    }

    private void ReportProblem() {
      ProblemReporter.Run(Visible ? this : null, computer, poller.Sync,
        alerts.GetRecentAlerts());
    }

    // ---- application log ----------------------------------------------------------

    private void LogClosing(object? sender, FormClosingEventArgs e) {
      string access = HardwareAccess.Tier == AccessTier.Deep
        ? "Deep (" + HardwareAccess.BackendName + ")" : "Base";
      ApplicationLog.Info("Closing (" + e.CloseReason + "). Sensor access: " + access +
        ". Last sensor update took " +
        poller.LastUpdateMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms.");
    }

    private static void LogPowerModeChanged(object? sender, PowerModeChangedEventArgs e) {
      if (e.Mode == PowerModes.Suspend) {
        ApplicationLog.Info("Windows is going to sleep.");
        ApplicationLog.Flush();
      } else if (e.Mode == PowerModes.Resume) {
        ApplicationLog.Info("Windows woke from sleep.");
      }
    }

    private static void LogSessionEnded(object? sender, SessionEndedEventArgs e) {
      ApplicationLog.Info(e.Reason == SessionEndReasons.Logoff
        ? "Windows is signing out." : "Windows is shutting down.");
      ApplicationLog.Flush();
    }
  }
}
