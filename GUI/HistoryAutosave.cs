/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Threading.Tasks;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Saves the recorded sensor history, with the rest of the settings, every
  /// ten minutes. Sensors otherwise write their history only when the
  /// application closes, so a crash or power cut during a multi-day run lost
  /// all of it; now at most the last ten minutes.
  ///
  /// The work runs on a pool thread: each sensor is copied under the hardware
  /// lock (a memory copy) and compressed outside it, and the settings file is
  /// written outside every lock. For about 200 busy sensors a full day of
  /// history compresses in roughly 150 ms and makes a settings file of about
  /// 5-7 MB.
  /// </summary>
  internal sealed class HistoryAutosave : IDisposable {

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly Computer computer;
    private readonly object sync;
    private readonly PersistentSettings settings;
    private readonly string fileName;
    private readonly System.Windows.Forms.Timer timer;
    private Task? pending;

    public HistoryAutosave(Computer computer, object sync, PersistentSettings settings,
      string fileName) {
      this.computer = computer;
      this.sync = sync;
      this.settings = settings;
      this.fileName = fileName;
      timer = new System.Windows.Forms.Timer { Interval = (int)Interval.TotalMilliseconds };
      timer.Tick += delegate { SaveInBackground(); };
      timer.Start();
    }

    /// <summary>Starts a save unless one is still running. Call on the UI thread.</summary>
    public void SaveInBackground() {
      if (pending != null && !pending.IsCompleted)
        return;
      pending = Task.Run(Save);
    }

    private void Save() {
      try {
        computer.SaveSensorHistory(sync);
        settings.Save(fileName);
      } catch (Exception) {
        // A full disk or a locked file: try again next time. Closing the
        // application still saves, and reports a failure then.
      }
    }

    /// <summary>
    /// Waits for a save in progress, so the final save on exit is not
    /// followed by an older one.
    /// </summary>
    public void WaitForPendingSave() {
      try {
        pending?.Wait(TimeSpan.FromSeconds(10));
      } catch (AggregateException) {
      }
    }

    public void Dispose() {
      timer.Dispose();
      WaitForPendingSave();
    }
  }
}
