/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2012 Michael Möller <mmoeller@openhardwaremonitor.org>

*/

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Utilities;

namespace OpenHardwareMonitor.GUI {
  public class SystemTray : IDisposable {
    private IComputer computer;
    private PersistentSettings settings;
    private UnitManager unitManager;
    private List<SensorNotifyIcon> list = new List<SensorNotifyIcon>();
    private bool mainIconEnabled = false;

    // The stock NotifyIcon. This used to be NotifyIconAdv, an 800-line
    // reimplementation that reflected into WinForms internals
    // (System.Windows.Forms.Command, ContextMenu.OnPopup and friends) which
    // no longer exist in .NET, so it could not be ported.
    private readonly NotifyIcon mainIcon;
    private readonly ContextMenuStrip contextMenu;

    public SystemTray(IComputer computer, PersistentSettings settings,
      UnitManager unitManager)
    {
      this.computer = computer;
      this.settings = settings;
      this.unitManager = unitManager;
      computer.HardwareAdded += new HardwareEventHandler(HardwareAdded);
      computer.HardwareRemoved += new HardwareEventHandler(HardwareRemoved);

      this.mainIcon = new NotifyIcon();

      contextMenu = new ContextMenuStrip();
      ToolStripMenuItem hideShowItem = new ToolStripMenuItem("Hide/Show");
      hideShowItem.Click += delegate(object obj, EventArgs args) {
        SendHideShowCommand();
      };
      contextMenu.Items.Add(hideShowItem);
      ToolStripMenuItem exportItem = new ToolStripMenuItem("Export for AI");
      exportItem.Click += delegate(object obj, EventArgs args) {
        SendExportDiagnosticsCommand();
      };
      contextMenu.Items.Add(exportItem);
      contextMenu.Items.Add(new ToolStripSeparator());
      ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
      exitItem.Click += delegate(object obj, EventArgs args) {
        SendExitCommand();
      };
      contextMenu.Items.Add(exitItem);
      this.mainIcon.ContextMenuStrip = contextMenu;
      this.mainIcon.DoubleClick += delegate(object obj, EventArgs args) {
        SendHideShowCommand();
      };
      this.mainIcon.Icon = EmbeddedResources.GetIcon("smallicon.ico");
      this.mainIcon.Text = "Open Hardware Monitor";
    }

    private void HardwareRemoved(IHardware hardware) {
      hardware.SensorAdded -= new SensorEventHandler(SensorAdded);
      hardware.SensorRemoved -= new SensorEventHandler(SensorRemoved);
      foreach (ISensor sensor in hardware.Sensors)
        SensorRemoved(sensor);
      foreach (IHardware subHardware in hardware.SubHardware)
        HardwareRemoved(subHardware);
    }

    private void HardwareAdded(IHardware hardware) {
      foreach (ISensor sensor in hardware.Sensors)
        SensorAdded(sensor);
      hardware.SensorAdded += new SensorEventHandler(SensorAdded);
      hardware.SensorRemoved += new SensorEventHandler(SensorRemoved);
      foreach (IHardware subHardware in hardware.SubHardware)
        HardwareAdded(subHardware);
    }

    private void SensorAdded(ISensor sensor) {
      if (UiThread.Redirect(() => SensorAdded(sensor)))
        return;
      if (settings.GetValue(new Identifier(sensor.Identifier,
        "tray").ToString(), false))
        Add(sensor, false);
    }

    private void SensorRemoved(ISensor sensor) {
      if (UiThread.Redirect(() => SensorRemoved(sensor)))
        return;
      if (Contains(sensor))
        Remove(sensor, false);
    }

    public void Dispose() {
      DisposeNotifications();
      foreach (SensorNotifyIcon icon in list)
        icon.Dispose();
      mainIcon.Visible = false;
      mainIcon.Dispose();
      contextMenu.Dispose();
    }

    public void Redraw() {
      foreach (SensorNotifyIcon icon in list)
        icon.Update();
    }

    public bool Contains(ISensor sensor) {
      foreach (SensorNotifyIcon icon in list)
        if (icon.Sensor == sensor)
          return true;
      return false;
    }

    public void Add(ISensor sensor, bool balloonTip) {
      if (Contains(sensor)) {
        return;
      } else {
        list.Add(new SensorNotifyIcon(this, sensor, balloonTip, settings, unitManager));
        UpdateMainIconVisibilty();
        settings.SetValue(new Identifier(sensor.Identifier, "tray").ToString(), true);
      }
    }

    public void Remove(ISensor sensor) {
      Remove(sensor, true);
    }

    private void Remove(ISensor sensor, bool deleteConfig) {
      if (deleteConfig) {
        settings.Remove(
          new Identifier(sensor.Identifier, "tray").ToString());
        settings.Remove(
          new Identifier(sensor.Identifier, "traycolor").ToString());
      }
      SensorNotifyIcon instance = null;
      foreach (SensorNotifyIcon icon in list)
        if (icon.Sensor == sensor)
          instance = icon;
      if (instance != null) {
        list.Remove(instance);
        UpdateMainIconVisibilty();
        instance.Dispose();
      }
    }

    public event EventHandler HideShowCommand;

    public void SendHideShowCommand() {
      if (HideShowCommand != null)
        HideShowCommand(this, null);
    }

    public event EventHandler ExportDiagnosticsCommand;

    public void SendExportDiagnosticsCommand() {
      if (ExportDiagnosticsCommand != null)
        ExportDiagnosticsCommand(this, EventArgs.Empty);
    }

    public event EventHandler ExitCommand;

    public void SendExitCommand() {
      if (ExitCommand != null)
        ExitCommand(this, null);
    }

    private void UpdateMainIconVisibilty() {
      if (mainIconEnabled) {
        mainIcon.Visible = list.Count == 0;
      } else {
        mainIcon.Visible = false;
      }
    }

    public bool IsMainIconEnabled {
      get { return mainIconEnabled; }
      set {
        if (mainIconEnabled != value) {
          mainIconEnabled = value;
          UpdateMainIconVisibilty();
        }
      }
    }

    // ---- notifications (alerts) --------------------------------------------------

#nullable enable

    private const int NotificationTitleLength = 63;
    private const int NotificationTextLength = 255;

    // If the close event never arrives, the temporary icon goes after this.
    private const int NotificationIconFallbackMilliseconds = 5 * 60 * 1000;

    private Action? notificationClicked;
    private bool notificationEventsHooked;
    private bool notificationIconShown;
    private bool notificationsDisposed;
    private DateTime notificationShownUtc;
    private System.Windows.Forms.Timer? notificationIconTimer;

    /// <summary>
    /// Shows a notification from the main tray icon; Windows 10 and 11 present
    /// it as a toast. <paramref name="clicked"/> runs on the UI thread when the
    /// user clicks it. Safe to call from any thread.
    /// </summary>
    public void ShowNotification(string title, string text, ToolTipIcon icon,
      Action? clicked) {
      if (UiThread.Redirect(() => ShowNotification(title, text, icon, clicked)))
        return;
      if (notificationsDisposed)
        return;

      if (!notificationEventsHooked) {
        notificationEventsHooked = true;
        mainIcon.BalloonTipClicked += delegate {
          Action? action = notificationClicked;
          HideNotificationIcon();
          action?.Invoke();
        };
        mainIcon.BalloonTipClosed += delegate {
          // Showing a notification closes the previous one; that close must
          // not take the icon away from under the new one.
          if (DateTime.UtcNow - notificationShownUtc > TimeSpan.FromSeconds(2))
            HideNotificationIcon();
        };
      }

      // A notification needs a visible icon. When the main icon is off, or
      // replaced by sensor icons, it is shown just for the notification.
      // Hiding it again also removes the notification from the notification
      // centre, so that waits until the notification has closed.
      if (!mainIcon.Visible) {
        mainIcon.Visible = true;
        notificationIconShown = true;
      }
      notificationClicked = clicked;
      notificationShownUtc = DateTime.UtcNow;
      mainIcon.ShowBalloonTip(10000, Truncate(title, NotificationTitleLength),
        Truncate(string.IsNullOrEmpty(text) ? title : text, NotificationTextLength), icon);

      if (notificationIconShown) {
        if (notificationIconTimer == null) {
          notificationIconTimer = new System.Windows.Forms.Timer {
            Interval = NotificationIconFallbackMilliseconds
          };
          notificationIconTimer.Tick += delegate { HideNotificationIcon(); };
        }
        notificationIconTimer.Stop();
        notificationIconTimer.Start();
      }
    }

    private void HideNotificationIcon() {
      notificationIconTimer?.Stop();
      if (!notificationIconShown || notificationsDisposed)
        return;
      notificationIconShown = false;
      UpdateMainIconVisibilty();
    }

    private void DisposeNotifications() {
      notificationsDisposed = true;
      notificationIconTimer?.Dispose();
      notificationIconTimer = null;
    }

    private static string Truncate(string text, int length) {
      return text.Length <= length ? text : text.Substring(0, length - 1) + "…";
    }

#nullable restore
  }
}
