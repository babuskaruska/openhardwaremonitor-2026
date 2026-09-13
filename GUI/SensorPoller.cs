/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Maintenance;

namespace OpenHardwareMonitor.GUI {

  /// <summary>
  /// Reads all sensors on a dedicated background thread.
  ///
  /// Hardware access is slow in places: per-core MSRs through a driver, NVML
  /// samples, SMART queries. On the UI thread that froze the window for up to
  /// a few hundred milliseconds every second, which showed up as stutter when
  /// dragging or scrolling.
  ///
  /// The contract:
  /// <list type="bullet">
  /// <item>Every hardware read and write happens under <see cref="Sync"/>. The
  /// poll thread holds it while updating; UI code that touches hardware (fan
  /// control, enabling sensor groups, reports) uses <see cref="RunLocked"/>.</item>
  /// <item>After each update the UI refresh runs on the UI thread while the
  /// poll thread waits, so sensor values and histories never change while the
  /// interface is reading them.</item>
  /// </list>
  /// </summary>
  internal sealed class SensorPoller : IDisposable {

    private const int RefreshTimeoutMilliseconds = 2000;

    private readonly IComputer computer;
    private readonly Control owner;
    private readonly Action afterUpdate;
    private readonly Action refresh;
    private readonly Action? initialize;
    private readonly UpdateVisitor visitor = new UpdateVisitor();
    private readonly ManualResetEventSlim stop = new ManualResetEventSlim(false);
    private readonly AutoResetEvent refreshed = new AutoResetEvent(false);
    private Thread? thread;
    private volatile int intervalMilliseconds = 1000;

    /// <param name="afterUpdate">Runs on the poll thread after each update,
    /// still under the lock (fan curves, logging).</param>
    /// <param name="refresh">Runs on the UI thread after each update.</param>
    /// <param name="initialize">Runs once on the poll thread, under the lock,
    /// before the first update. Opening the hardware takes from half a second
    /// to several seconds on a cold start, so it happens here instead of
    /// holding up the window.</param>
    public SensorPoller(IComputer computer, Control owner, Action afterUpdate,
      Action refresh, Action? initialize = null) {
      this.computer = computer;
      this.owner = owner;
      this.afterUpdate = afterUpdate;
      this.refresh = refresh;
      this.initialize = initialize;
    }

    /// <summary>Held for every hardware access.</summary>
    public object Sync { get; } = new object();

    public int IntervalMilliseconds {
      get { return intervalMilliseconds; }
      set { intervalMilliseconds = Math.Max(250, value); }
    }

    /// <summary>Duration of the most recent sensor update, in milliseconds.</summary>
    public double LastUpdateMilliseconds { get; private set; }

    /// <summary>The last exception an update threw, for diagnostics.</summary>
    public Exception? LastError { get; private set; }

    public void Start() {
      if (thread != null)
        return;
      thread = new Thread(Run) {
        IsBackground = true,
        Name = "Sensor polling"
      };
      thread.Start();
    }

    public void RunLocked(Action action) {
      lock (Sync)
        action();
    }

    public T RunLocked<T>(Func<T> function) {
      lock (Sync)
        return function();
    }

    // A device that throws on every update is logged once an hour, with a
    // count, rather than every second.
    private readonly RepeatedErrorFilter errorFilter =
      new RepeatedErrorFilter(TimeSpan.FromHours(1));

    /// <summary>Cheap on the sensor thread: the log only queues the entry.</summary>
    private void LogUpdateError(Exception ex) {
      if (errorFilter.ShouldLog(ex, out int suppressed))
        ApplicationLog.Error("A sensor update failed" + (suppressed > 0
          ? " (the same error occurred " + suppressed.ToString(CultureInfo.InvariantCulture) +
            " more times since it was last logged)." : "."), ex);
    }

    private static string DescribeAccess() {
      string? missing = HardwareAccess.UnavailableReason;
      string tier = HardwareAccess.Tier == AccessTier.Deep
        ? "Deep (" + HardwareAccess.BackendName + ")" : "Base";
      return missing == null ? tier : tier + ". " + missing;
    }

    private void Run() {
      if (initialize != null) {
        lock (Sync) {
          Stopwatch opening = Stopwatch.StartNew();
          try {
            initialize();
            ApplicationLog.Info("Hardware opened in " +
              opening.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
              " ms. Sensor access: " + DescribeAccess());
          } catch (Exception ex) {
            LastError = ex;
            ApplicationLog.Error("Opening the hardware failed.", ex);
          }
        }
      }

      Stopwatch clock = Stopwatch.StartNew();
      double next = 0;

      while (!stop.IsSet) {
        double wait = next - clock.Elapsed.TotalMilliseconds;
        if (wait > 0 && stop.Wait(TimeSpan.FromMilliseconds(wait)))
          break;

        double start = clock.Elapsed.TotalMilliseconds;
        lock (Sync) {
          try {
            computer.Accept(visitor);
            afterUpdate();
          } catch (Exception ex) {
            // One failing device must never stop every sensor updating.
            LastError = ex;
            LogUpdateError(ex);
          }
        }
        double end = clock.Elapsed.TotalMilliseconds;
        LastUpdateMilliseconds = end - start;

        // Keep a steady cadence; if an update overran, start the next one
        // after a short breather rather than immediately.
        next = Math.Max(start + intervalMilliseconds, end + 50);

        if (stop.IsSet)
          break;
        if (owner.IsDisposed || !owner.IsHandleCreated)
          continue;
        try {
          owner.BeginInvoke((Action)RefreshOnUiThread);
        } catch (Exception) {
          // The window is going away.
          continue;
        }
        WaitHandle.WaitAny(new WaitHandle[] { refreshed, stop.WaitHandle },
          RefreshTimeoutMilliseconds);
      }
    }

    private void RefreshOnUiThread() {
      try {
        if (!stop.IsSet)
          refresh();
      } finally {
        refreshed.Set();
      }
    }

    /// <summary>Stops polling and waits for an update in progress to finish.</summary>
    public void Stop() {
      stop.Set();
      Thread? running = thread;
      thread = null;
      if (running != null && running != Thread.CurrentThread)
        running.Join(3000);
    }

    public void Dispose() {
      Stop();
      refreshed.Dispose();
      stop.Dispose();
    }
  }
}
