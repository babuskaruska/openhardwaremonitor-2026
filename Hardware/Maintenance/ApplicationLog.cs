/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>
  /// The application log, %LOCALAPPDATA%\OpenHardwareMonitor\Logs\app.log:
  /// start and stop, problems that do not stop the application (a sensor
  /// update that throws, a web server that cannot start, a fan curve that
  /// fails safe) and update checks. It exists so a problem during a long
  /// unattended run can be understood afterwards.
  ///
  /// Not to be confused with Utilities.Logger, which writes sensor values to
  /// CSV files in the same folder.
  ///
  /// Every method is safe to call from any thread, before <see cref="Open"/>
  /// (the message is then discarded) and in library code that runs without
  /// the application, such as tests and SensorDump.
  /// </summary>
  public static class ApplicationLog {

    public const string FileName = "app.log";

    private static readonly object sync = new object();
    private static volatile RollingLogFile? file;

    public static string DefaultDirectory {
      get {
        return Path.Combine(Environment.GetFolderPath(
          Environment.SpecialFolder.LocalApplicationData),
          "OpenHardwareMonitor", "Logs");
      }
    }

    /// <summary>The open log file, or null.</summary>
    public static string? FilePath {
      get { return file?.FilePath; }
    }

    /// <summary>Starts logging to <paramref name="path"/> (default: app.log). Does nothing when already open.</summary>
    public static void Open(string? path = null) {
      lock (sync) {
        if (file != null)
          return;
        try {
          file = new RollingLogFile(path ?? Path.Combine(DefaultDirectory, FileName));
        } catch (Exception) {
          // No usable path. The application runs the same without a log.
        }
      }
    }

    /// <summary>Writes what is queued and stops logging.</summary>
    public static void Close() {
      RollingLogFile? closing;
      lock (sync) {
        closing = file;
        file = null;
      }
      closing?.Dispose();
    }

    public static void Info(string message) {
      file?.Write(LogLevel.Info, message);
    }

    public static void Warning(string message, Exception? exception = null) {
      file?.Write(LogLevel.Warning, message, exception);
    }

    public static void Error(string message, Exception? exception = null) {
      file?.Write(LogLevel.Error, message, exception);
    }

    /// <summary>The most recent entries, oldest first.</summary>
    public static IReadOnlyList<string> Tail(int count) {
      return file?.GetTail(count) ?? Array.Empty<string>();
    }

    /// <summary>Writes queued entries now, for example before the process ends.</summary>
    public static void Flush() {
      try {
        file?.Flush();
      } catch (Exception) {
        // Best effort.
      }
    }
  }
}
