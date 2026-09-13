/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Threading;
using System.Windows.Forms;
using OpenHardwareMonitor.GUI;
using OpenHardwareMonitor.Hardware.Maintenance;
using OpenHardwareMonitor.Utilities;

namespace OpenHardwareMonitor {
  public static class Program {

    // Per-session. Two copies would fight over the same settings file, the
    // same tray icons and the same web server port, and autostart plus a
    // manual launch is an easy way to end up with two.
    private const string SingleInstanceMutexName =
      @"Local\OpenHardwareMonitor.SingleInstance";

    [STAThread]
    public static void Main(string[] args) {
      // Before anything else, so that a problem during start-up leaves a trace.
      ApplicationLog.Open();
      try {
        Run(args);
      } finally {
        ApplicationLog.Close();
      }
    }

    private static void Run(string[] args) {
      #if !DEBUG
        Application.ThreadException +=
          new ThreadExceptionEventHandler(Application_ThreadException);
        Application.SetUnhandledExceptionMode(
          UnhandledExceptionMode.CatchException);

        AppDomain.CurrentDomain.UnhandledException +=
          new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
      #endif

      // The former startup checks - that the .NET Framework 4.5 was installed
      // and that the dependency DLLs sat next to the executable - are gone.
      // Neither is meaningful on .NET 10, and the second one broke single-file
      // publishing, where those DLLs are bundled into the executable.

      using (Mutex mutex = new Mutex(true, SingleInstanceMutexName,
        out bool createdNew)) {
        // A restart (for example as administrator) waits for the previous
        // instance to finish closing instead of giving up straight away.
        if (!createdNew && !WaitForPreviousInstance(args, mutex)) {
          ApplicationLog.Info("Another instance is already running, so this one exits.");
          return;
        }
        LogStart();

        // Both of these must precede creating any window. DPI awareness is
        // declared here rather than in the manifest so the WinForms runtime
        // can scale correctly per monitor.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetColorMode(ReadColorMode());
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using (MainForm form = new MainForm()) {
          form.FormClosed += delegate(object sender, FormClosedEventArgs e) {
            Application.Exit();
          };
          Application.Run();
        }

        GC.KeepAlive(mutex);
      }
    }

    /// <summary>
    /// With --wait-for-pid N, waits for that process to exit and then for
    /// the single-instance lock. Returns false when it cannot be taken.
    /// </summary>
    private static bool WaitForPreviousInstance(string[] args, Mutex mutex) {
      int index = Array.IndexOf(args, "--wait-for-pid");
      if (index < 0)
        return false;
      if (index + 1 < args.Length && int.TryParse(args[index + 1], out int pid)) {
        try {
          using (System.Diagnostics.Process previous =
            System.Diagnostics.Process.GetProcessById(pid))
            previous.WaitForExit(15000);
        } catch (ArgumentException) {
          // Already gone.
        }
      }
      try {
        return mutex.WaitOne(15000);
      } catch (AbandonedMutexException) {
        return true;
      }
    }

    /// <summary>
    /// The theme chosen under Options > Theme. It has to be read here, before
    /// MainForm exists, because WinForms applies the colour mode to windows as
    /// they are created; that is also why a change takes effect on restart.
    /// 0 = follow Windows (the default), 1 = light, 2 = dark.
    /// </summary>
    private static SystemColorMode ReadColorMode() {
      try {
        PersistentSettings settings = new PersistentSettings();
        settings.Load(MainForm.GetConfigurationPath());
        switch (settings.GetValue("theme", 0)) {
          case 1: return SystemColorMode.Classic;
          case 2: return SystemColorMode.Dark;
        }
      } catch (Exception) {
        // An unreadable settings file must not prevent startup.
      }
      return SystemColorMode.System;
    }

    // From the process start time, not a static Stopwatch: without a static
    // constructor the runtime may create static fields on first use, which
    // was only at exit, so every run logged an uptime of zero.
    private static TimeSpan ProcessUptime() {
      using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
        return DateTime.Now - process.StartTime;
    }

    /// <summary>Sensor access is logged once the hardware is open (see SensorPoller).</summary>
    private static void LogStart() {
      EnvironmentFacts facts = EnvironmentFacts.Capture();
      ApplicationLog.Info("Open Hardware Monitor " + facts.ApplicationVersion + " started on " +
        facts.Windows + ", " + facts.Architecture + ", " +
        (facts.IsElevated == true ? "as administrator." : "not as administrator."));
      Application.ApplicationExit += delegate {
        ApplicationLog.Info("Open Hardware Monitor stopped after " +
          EnvironmentFacts.FormatDuration(ProcessUptime()) + ".");
      };
    }

    private static void ReportException(Exception e) {
      // Writes the report to disk before anything is shown.
      CrashReporter.Report(e);
    }

    public static void Application_ThreadException(object sender,
      ThreadExceptionEventArgs e)
    {
      try {
        ReportException(e.Exception);
      } catch {
      } finally {
        Application.Exit();
      }
    }

    public static void CurrentDomain_UnhandledException(object sender,
      UnhandledExceptionEventArgs args)
    {
      try {
        Exception e = args.ExceptionObject as Exception;
        if (e != null)
          ReportException(e);
      } catch {
      } finally {
        Environment.Exit(0);
      }
    }
  }
}
