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
using System.IO;
using System.Text;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>
  /// Crash reports in %LOCALAPPDATA%\OpenHardwareMonitor\Crashes. The report
  /// is written before any window is shown, so a crash during an unattended
  /// run is on disk even if nobody is there to see a dialog, or the dialog
  /// itself fails. Only the newest <see cref="DefaultKeep"/> are kept.
  /// </summary>
  public static class CrashReport {

    public const int DefaultKeep = 20;
    public const string FilePrefix = "crash-";
    public const int LogLines = 40;

    private const int MaxChainDepth = 8;
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public static string DefaultDirectory {
      get {
        return Path.Combine(Environment.GetFolderPath(
          Environment.SpecialFolder.LocalApplicationData),
          "OpenHardwareMonitor", "Crashes");
      }
    }

    /// <param name="threadName">The thread that crashed, when known.</param>
    /// <param name="filter">Null means <see cref="PrivacyFilter.Current"/>.</param>
    public static string Format(Exception exception, EnvironmentFacts facts,
      IReadOnlyList<string> logTail, DateTimeOffset time, string? threadName = null,
      PrivacyFilter? filter = null) {
      if (exception == null)
        throw new ArgumentNullException(nameof(exception));
      facts ??= new EnvironmentFacts();

      StringBuilder s = new StringBuilder();
      s.AppendLine("Open Hardware Monitor crash report");
      s.AppendLine("==================================");
      s.AppendLine();
      Fact(s, "Time", time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
      Fact(s, "Version", facts.ApplicationVersion);
      Fact(s, "Windows", facts.Windows);
      Fact(s, "OS description", facts.OsDescription);
      Fact(s, "Architecture", facts.Architecture);
      Fact(s, ".NET runtime", facts.Runtime);
      Fact(s, "Process uptime", EnvironmentFacts.FormatDuration(facts.ProcessUptime));
      Fact(s, "Administrator", facts.IsElevated.HasValue
        ? (facts.IsElevated.Value ? "yes" : "no") : EnvironmentFacts.Unknown);
      Fact(s, "Access tier", facts.AccessTier);
      if (!string.IsNullOrEmpty(threadName))
        Fact(s, "Thread", threadName);
      s.AppendLine();

      s.AppendLine("Exceptions (outermost first)");
      s.AppendLine("----------------------------");
      AppendChain(s, exception, 0);
      s.AppendLine();

      s.AppendLine("Details");
      s.AppendLine("-------");
      string details;
      try {
        details = exception.ToString();
      } catch (Exception ex) {
        details = exception.GetType().FullName + ": " + exception.Message +
          " (details unavailable: " + ex.GetType().Name + ")";
      }
      s.AppendLine(details);
      s.AppendLine();

      s.AppendLine("Last application log entries");
      s.AppendLine("----------------------------");
      if (logTail == null || logTail.Count == 0) {
        s.AppendLine("(none)");
      } else {
        foreach (string line in logTail)
          s.AppendLine(line);
      }

      return (filter ?? PrivacyFilter.Current).Scrub(s.ToString());
    }

    private static void Fact(StringBuilder s, string name, string value) {
      s.Append((name + ":").PadRight(16)).AppendLine(value);
    }

    private static void AppendChain(StringBuilder s, Exception exception, int depth) {
      s.Append(' ', 2 + 2 * depth).Append(exception.GetType().FullName)
        .Append(": ").AppendLine(exception.Message);
      if (depth + 1 >= MaxChainDepth)
        return;
      if (exception is AggregateException aggregate) {
        foreach (Exception inner in aggregate.InnerExceptions)
          AppendChain(s, inner, depth + 1);
      } else if (exception.InnerException != null) {
        AppendChain(s, exception.InnerException, depth + 1);
      }
    }

    /// <summary>
    /// Writes <paramref name="report"/> as crash-yyyyMMdd-HHmmss.txt (with a
    /// numeric suffix if that name exists), flushed to the disk, then deletes
    /// all but the newest <paramref name="keep"/> reports. Returns the path.
    /// Throws IOException or UnauthorizedAccessException when it cannot write.
    /// </summary>
    public static string Write(string directory, string report, DateTimeOffset time,
      int keep = DefaultKeep) {
      Directory.CreateDirectory(directory);
      string baseName = FilePrefix + time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
      byte[] bytes = Utf8NoBom.GetBytes(report ?? "");
      string path;
      for (int attempt = 1; ; attempt++) {
        path = Path.Combine(directory, (attempt == 1 ? baseName
          : baseName + "-" + attempt.ToString(CultureInfo.InvariantCulture)) + ".txt");
        try {
          using (FileStream stream = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read)) {
            stream.Write(bytes, 0, bytes.Length);
            // The process is about to end, possibly abruptly.
            stream.Flush(true);
          }
          break;
        } catch (IOException) when (attempt < 100 && File.Exists(path)) {
          // Another crash in the same second.
        }
      }

      try {
        Prune(directory, keep);
      } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
        // Old reports staying behind is harmless.
      }
      return path;
    }

    internal static void Prune(string directory, int keep) {
      List<string> files = new List<string>(
        Directory.GetFiles(directory, FilePrefix + "*.txt"));
      if (files.Count <= Math.Max(0, keep))
        return;
      // Names sort by time; the suffix of a same-second report sorts after.
      files.Sort((a, b) => {
        int result = string.CompareOrdinal(Stamp(b, out int suffixB), Stamp(a, out int suffixA));
        return result != 0 ? result : suffixB.CompareTo(suffixA);
      });
      for (int i = Math.Max(0, keep); i < files.Count; i++) {
        try {
          File.Delete(files[i]);
        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
          // Open in an editor, for example.
        }
      }
    }

    private static string Stamp(string path, out int suffix) {
      string stem = Path.GetFileNameWithoutExtension(path);
      stem = stem.Length > FilePrefix.Length ? stem.Substring(FilePrefix.Length) : "";
      suffix = 1;
      // yyyyMMdd-HHmmss is 15 characters, optionally followed by -N.
      if (stem.Length > 16 && stem[15] == '-' &&
        int.TryParse(stem.Substring(16), NumberStyles.None, CultureInfo.InvariantCulture,
          out int parsed)) {
        suffix = parsed;
        return stem.Substring(0, 15);
      }
      return stem;
    }
  }
}
