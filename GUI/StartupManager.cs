/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;
using OpenHardwareMonitor.TaskScheduler;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// "Run On Windows Startup".
  ///
  /// When running elevated, a Task Scheduler logon task is registered with the
  /// highest run level, so the application starts elevated at logon without a
  /// UAC prompt. Otherwise the per-user Run key is used, which starts it
  /// unelevated - still fully functional in the Base tier.
  /// </summary>
  public class StartupManager {

    private const string RegistryRun =
      @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "OpenHardwareMonitor";
    private const string TaskFolderName = "Open Hardware Monitor";
    private const string TaskName = "Startup";

    // Logon triggers fire before Explorer has finished starting, and a
    // Shell_NotifyIcon call made before the taskbar exists is silently lost -
    // the application would be running with no tray icon. A short delay lets
    // the shell come up first.
    private const string LogonDelay = "PT15S";

    // Task Scheduler's default priority is 7, which is below-normal CPU *and*
    // very low I/O priority. 5 is normal.
    private const int NormalTaskPriority = 5;

    private TaskSchedulerClass scheduler;
    private bool startup;
    private bool isAvailable;

    /// <summary>
    /// Path of the running executable. Environment.ProcessPath is correct for
    /// both the apphost and single-file publishing.
    /// </summary>
    private static string ExecutablePath {
      get { return Environment.ProcessPath ?? Application.ExecutablePath; }
    }

    private static bool PathsEqual(string a, string b) {
      if (a == null || b == null)
        return false;
      return string.Equals(a.Trim().Trim('"'), b.Trim().Trim('"'),
        StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdministrator() {
      try {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) {
          return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
        }
      } catch {
        return false;
      }
    }

    public StartupManager() {
      if (Hardware.OperatingSystem.IsUnix) {
        scheduler = null;
        isAvailable = false;
        return;
      }

      if (IsAdministrator()) {
        try {
          scheduler = new TaskSchedulerClass();
          scheduler.Connect(null, null, null, null);
        } catch {
          scheduler = null;
        }

        if (scheduler != null) {
          try {
            try {
              // check if the taskscheduler is running
              IRunningTaskCollection collection = scheduler.GetRunningTasks(0);
            } catch (ArgumentException) { }

            ITaskFolder folder = scheduler.GetFolder("\\" + TaskFolderName);
            IRegisteredTask task = folder.GetTask(TaskName);
            // Task Scheduler collections are 1-based.
            startup = (task != null) &&
              (task.Definition.Triggers.Count > 0) &&
              (task.Definition.Triggers[1].Type ==
                TASK_TRIGGER_TYPE2.TASK_TRIGGER_LOGON) &&
              (task.Definition.Actions.Count > 0) &&
              (task.Definition.Actions[1].Type ==
                TASK_ACTION_TYPE.TASK_ACTION_EXEC) &&
              (task.Definition.Actions[1] as IExecAction != null) &&
              PathsEqual((task.Definition.Actions[1] as IExecAction).Path,
                ExecutablePath);

          } catch (IOException) {
            startup = false;
          } catch (UnauthorizedAccessException) {
            scheduler = null;
          } catch (COMException) {
            scheduler = null;
          } catch (NotImplementedException) {
            scheduler = null;
          }
        }
      } else {
        scheduler = null;
      }

      if (scheduler == null) {
        try {
          using (RegistryKey key =
            Registry.CurrentUser.OpenSubKey(RegistryRun)) {
            startup = false;
            if (key != null) {
              string value = key.GetValue(RegistryValueName) as string;
              startup = PathsEqual(value, ExecutablePath);
            }
          }
          isAvailable = true;
        } catch (SecurityException) {
          isAvailable = false;
        }
      } else {
        isAvailable = true;
      }
    }

    private void CreateSchedulerTask() {
      ITaskDefinition definition = scheduler.NewTask(0);
      definition.RegistrationInfo.Description =
        "Starts Open Hardware Monitor when you sign in.";
      definition.Principal.RunLevel =
        TASK_RUNLEVEL.TASK_RUNLEVEL_HIGHEST;
      definition.Settings.DisallowStartIfOnBatteries = false;
      definition.Settings.StopIfGoingOnBatteries = false;
      definition.Settings.ExecutionTimeLimit = "PT0S";
      definition.Settings.Priority = NormalTaskPriority;

      ILogonTrigger trigger = (ILogonTrigger)definition.Triggers.Create(
        TASK_TRIGGER_TYPE2.TASK_TRIGGER_LOGON);
      trigger.Delay = LogonDelay;
      // Without a user the trigger fires on *anyone's* logon, but the task
      // runs with the registering user's interactive token and fails for
      // everybody else.
      try {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
          trigger.UserId = identity.Name;
      } catch {
      }

      IExecAction action = (IExecAction)definition.Actions.Create(
        TASK_ACTION_TYPE.TASK_ACTION_EXEC);
      action.Path = ExecutablePath;
      action.WorkingDirectory = Path.GetDirectoryName(ExecutablePath);

      ITaskFolder root = scheduler.GetFolder("\\");
      ITaskFolder folder;
      try {
        folder = root.GetFolder(TaskFolderName);
      } catch (IOException) {
        folder = root.CreateFolder(TaskFolderName, "");
      }
      folder.RegisterTaskDefinition(TaskName, definition,
        (int)TASK_CREATION.TASK_CREATE_OR_UPDATE, null, null,
        TASK_LOGON_TYPE.TASK_LOGON_INTERACTIVE_TOKEN, "");
    }

    private void DeleteSchedulerTask() {
      ITaskFolder root = scheduler.GetFolder("\\");
      try {
        ITaskFolder folder = root.GetFolder(TaskFolderName);
        folder.DeleteTask(TaskName, 0);
      } catch (IOException) { }
      try {
        root.DeleteFolder(TaskFolderName, 0);
      } catch (IOException) { }
    }

    private void CreateRegistryRun() {
      using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryRun)) {
        // Quoted. An unquoted path containing spaces is split at the first
        // space by the shell, which both breaks startup and lets a binary
        // planted at the truncated path run instead.
        key.SetValue(RegistryValueName, "\"" + ExecutablePath + "\"");
      }
    }

    private void DeleteRegistryRun() {
      using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryRun)) {
        key.DeleteValue(RegistryValueName, false);
      }
    }

    public bool IsAvailable {
      get { return isAvailable; }
    }

    public bool Startup {
      get {
        return startup;
      }
      set {
        if (startup != value) {
          if (isAvailable) {
            if (scheduler != null) {
              if (value)
                CreateSchedulerTask();
              else
                DeleteSchedulerTask();
              startup = value;
            } else {
              try {
                if (value)
                  CreateRegistryRun();
                else
                  DeleteRegistryRun();
                startup = value;
              } catch (UnauthorizedAccessException) {
                throw new InvalidOperationException();
              } catch (SecurityException) {
                throw new InvalidOperationException();
              }
            }
          } else {
            throw new InvalidOperationException();
          }
        }
      }
    }
  }

}
