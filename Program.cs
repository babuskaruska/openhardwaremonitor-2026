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
using OpenHardwareMonitor.Utilities;

namespace OpenHardwareMonitor {
  public static class Program {

    // Per-session. Two copies would fight over the same settings file, the
    // same tray icons and the same web server port, and autostart plus a
    // manual launch is an easy way to end up with two.
    private const string SingleInstanceMutexName =
      @"Local\OpenHardwareMonitor.SingleInstance";

    [STAThread]
    public static void Main() {
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
        if (!createdNew)
          return;

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

    private static void ReportException(Exception e) {
      using (CrashForm form = new CrashForm()) {
        form.Exception = e;
        form.ShowDialog();
      }
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
