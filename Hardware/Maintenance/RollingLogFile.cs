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
using System.Threading;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  public enum LogLevel {
    Info,
    Warning,
    Error
  }

  /// <summary>
  /// A small, thread-safe text log that stays under a size limit: when the
  /// file would grow past it, the file becomes "name.1.ext" (replacing the
  /// previous one) and a new file starts. At most about twice the limit is
  /// ever on disk.
  ///
  /// Callers include the sensor thread while it holds the hardware lock, so
  /// <see cref="Write"/> never touches the disk: it formats the line, queues
  /// it and arms a timer. The timer writes the queue on a thread-pool thread
  /// shortly afterwards. If the disk stalls, the queue is capped and the
  /// number of dropped lines is written once it recovers.
  ///
  /// The most recent entries are also kept in memory, so a crash report can
  /// include them even when the disk was the problem.
  /// </summary>
  public sealed class RollingLogFile : IDisposable {

    public const long DefaultMaxBytes = 1024 * 1024;
    public static readonly TimeSpan DefaultFlushDelay = TimeSpan.FromMilliseconds(750);

    internal const int MaxPendingEntries = 10000;
    internal const int MaxMessageLength = 16000;
    private const int TailCapacity = 200;

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly object sync = new object();
    private readonly object fileSync = new object();
    private readonly long maxBytes;
    private readonly PrivacyFilter filter;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan flushDelay;
    private readonly Timer timer;
    private readonly Queue<string> tail = new Queue<string>();
    private List<string> pending = new List<string>();
    private bool flushScheduled;
    private int dropped;
    private bool disposed;
    private int writeFailures;

    /// <param name="filter">Applied to every message; null means
    /// <see cref="PrivacyFilter.Current"/>.</param>
    public RollingLogFile(string path, long maxBytes = DefaultMaxBytes,
      PrivacyFilter? filter = null, Func<DateTimeOffset>? clock = null,
      TimeSpan? flushDelay = null) {
      if (string.IsNullOrWhiteSpace(path))
        throw new ArgumentException("A path is required.", nameof(path));
      if (maxBytes < 1024)
        throw new ArgumentOutOfRangeException(nameof(maxBytes));

      FilePath = Path.GetFullPath(path);
      string directory = Path.GetDirectoryName(FilePath) ?? "";
      PreviousFilePath = Path.Combine(directory,
        Path.GetFileNameWithoutExtension(FilePath) + ".1" + Path.GetExtension(FilePath));
      this.maxBytes = maxBytes;
      this.filter = filter ?? PrivacyFilter.Current;
      this.clock = clock ?? (() => DateTimeOffset.Now);
      this.flushDelay = flushDelay ?? DefaultFlushDelay;
      timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath { get; }

    /// <summary>Where the file goes when it reaches the size limit.</summary>
    public string PreviousFilePath { get; }

    /// <summary>Flushes that could not write to the disk.</summary>
    public int WriteFailures {
      get { return Volatile.Read(ref writeFailures); }
    }

    /// <summary>Queues one entry. Never blocks on the disk and never throws.</summary>
    public void Write(LogLevel level, string? message, Exception? exception = null) {
      string entry;
      try {
        string text = message ?? "";
        if (exception != null)
          text = text.Length == 0 ? exception.ToString()
            : text + Environment.NewLine + exception;
        if (text.Length > MaxMessageLength)
          text = text.Substring(0, MaxMessageLength) + " (truncated)";
        entry = Format(clock(), level, filter.Scrub(text));
      } catch (Exception) {
        // A throwing ToString override or clock; logging must not fail its caller.
        return;
      }

      lock (sync) {
        tail.Enqueue(entry);
        while (tail.Count > TailCapacity)
          tail.Dequeue();
        if (disposed)
          return;
        if (pending.Count >= MaxPendingEntries) {
          dropped++;
          return;
        }
        pending.Add(entry);
        if (!flushScheduled) {
          flushScheduled = true;
          timer.Change(flushDelay, Timeout.InfiniteTimeSpan);
        }
      }
    }

    /// <summary>The most recent entries, oldest first, including unwritten ones.</summary>
    public IReadOnlyList<string> GetTail(int count) {
      lock (sync) {
        string[] all = tail.ToArray();
        if (count >= all.Length)
          return all;
        if (count <= 0)
          return Array.Empty<string>();
        string[] result = new string[count];
        Array.Copy(all, all.Length - count, result, 0, count);
        return result;
      }
    }

    internal static string Format(DateTimeOffset time, LogLevel level, string text) {
      string label;
      switch (level) {
        case LogLevel.Warning: label = "WARN "; break;
        case LogLevel.Error: label = "ERROR"; break;
        default: label = "INFO "; break;
      }
      StringBuilder s = new StringBuilder(text.Length + 48);
      s.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
        .Append("  ").Append(label).Append("  ");
      string[] lines = text.Replace("\r\n", "\n").Split('\n');
      s.Append(lines[0].TrimEnd('\r'));
      // Continuation lines are indented so every entry starts with a time.
      for (int i = 1; i < lines.Length; i++)
        s.Append(Environment.NewLine).Append("    ").Append(lines[i].TrimEnd('\r'));
      return s.ToString();
    }

    private void OnTimer(object? state) {
      try {
        Flush();
      } catch (Exception) {
        // Flush already handles disk errors; nothing else may escape a timer.
      }
    }

    /// <summary>Writes every queued entry now. Safe from any thread.</summary>
    public void Flush() {
      lock (fileSync) {
        List<string> batch;
        int lost;
        lock (sync) {
          batch = pending;
          pending = new List<string>();
          lost = dropped;
          dropped = 0;
          flushScheduled = false;
        }
        if (batch.Count == 0 && lost == 0)
          return;

        StringBuilder text = new StringBuilder();
        foreach (string entry in batch)
          text.Append(entry).Append(Environment.NewLine);
        if (lost > 0)
          text.Append(Format(clock(), LogLevel.Warning,
            lost.ToString(CultureInfo.InvariantCulture) +
            " log entries were dropped because the log could not keep up."))
            .Append(Environment.NewLine);
        byte[] bytes = Utf8NoBom.GetBytes(text.ToString());

        try {
          string? directory = Path.GetDirectoryName(FilePath);
          if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
          RollOverIfNeeded(bytes.Length);
          // Shared for reading and deleting, so the log can be opened, copied
          // or removed while the application runs.
          using (FileStream stream = new FileStream(FilePath, FileMode.Append,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            stream.Write(bytes, 0, bytes.Length);
        } catch (Exception ex) when (ex is IOException ||
          ex is UnauthorizedAccessException || ex is NotSupportedException ||
          ex is System.Security.SecurityException) {
          Interlocked.Increment(ref writeFailures);
        }
      }
    }

    private void RollOverIfNeeded(int incoming) {
      FileInfo info = new FileInfo(FilePath);
      if (!info.Exists || info.Length == 0 || info.Length + incoming <= maxBytes)
        return;
      try {
        File.Move(FilePath, PreviousFilePath, true);
      } catch (Exception ex) when (ex is IOException ||
        ex is UnauthorizedAccessException) {
        // Something holds the file without allowing it to be moved. Start
        // over rather than grow without limit.
        if (info.Length >= maxBytes * 2)
          using (new FileStream(FilePath, FileMode.Truncate, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete)) { }
      }
    }

    /// <summary>Writes what is queued and stops the timer. Later writes only reach the tail.</summary>
    public void Dispose() {
      lock (sync) {
        if (disposed)
          return;
        disposed = true;
      }
      timer.Dispose();
      try {
        Flush();
      } catch (Exception) {
        // Shutting down; the log is best effort.
      }
    }
  }
}
