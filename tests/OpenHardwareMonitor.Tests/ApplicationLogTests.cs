/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenHardwareMonitor.Hardware.Maintenance;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>The application log file, the repeated-error filter and the privacy filter.</summary>
  public sealed class ApplicationLogTests : IDisposable {

    private static readonly DateTimeOffset Time =
      new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    // Long enough that only an explicit Flush writes during a test.
    private static readonly TimeSpan NoTimer = TimeSpan.FromHours(1);

    private readonly string directory = Path.Combine(Path.GetTempPath(),
      "ohm-log-tests-" + Guid.NewGuid().ToString("N"));

    public ApplicationLogTests() {
      Directory.CreateDirectory(directory);
    }

    public void Dispose() {
      try {
        Directory.Delete(directory, true);
      } catch (IOException) {
      }
    }

    private RollingLogFile Log(long maxBytes = RollingLogFile.DefaultMaxBytes,
      PrivacyFilter? filter = null, TimeSpan? flushDelay = null) {
      return new RollingLogFile(Path.Combine(directory, "app.log"), maxBytes,
        filter ?? PrivacyFilter.None, () => Time, flushDelay ?? NoTimer);
    }

    private static int CountEntries(string path) {
      return File.Exists(path)
        ? File.ReadAllLines(path).Count(line => line.StartsWith("2026-", StringComparison.Ordinal))
        : 0;
    }

    // ---- log file ---------------------------------------------------------------

    [Fact]
    public void WritesEntriesOnlyWhenFlushed() {
      using (RollingLogFile log = Log()) {
        log.Write(LogLevel.Info, "Started");
        log.Write(LogLevel.Warning, "Web server could not start");
        log.Write(LogLevel.Error, "Update failed",
          new InvalidOperationException("Outer", new TimeoutException("Inner")));
        Assert.False(File.Exists(log.FilePath));

        log.Flush();

        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.Equal("2026-09-13 12:00:00.000 +00:00  INFO   Started", lines[0]);
        Assert.Equal("2026-09-13 12:00:00.000 +00:00  WARN   Web server could not start", lines[1]);
        Assert.StartsWith("2026-09-13 12:00:00.000 +00:00  ERROR  Update failed", lines[2]);
        string rest = string.Join("\n", lines.Skip(3));
        Assert.Contains("System.InvalidOperationException: Outer", rest);
        Assert.Contains("System.TimeoutException: Inner", rest);
        // Continuation lines are indented, so every entry starts with a time.
        Assert.All(lines.Skip(3), line => Assert.StartsWith("    ", line));
      }
    }

    [Fact]
    public void FlushesOnItsOwnShortlyAfterAWrite() {
      using (RollingLogFile log = Log(flushDelay: TimeSpan.FromMilliseconds(20))) {
        log.Write(LogLevel.Info, "Queued");
        SpinWait.SpinUntil(() => CountEntries(log.FilePath) == 1, TimeSpan.FromSeconds(10));
        Assert.Equal(1, CountEntries(log.FilePath));
      }
    }

    [Fact]
    public void TailIncludesEntriesNotYetWritten() {
      using (RollingLogFile log = Log()) {
        for (int i = 0; i < 5; i++)
          log.Write(LogLevel.Info, "Entry " + i);
        IReadOnlyList<string> tail = log.GetTail(2);
        Assert.Equal(2, tail.Count);
        Assert.EndsWith("Entry 3", tail[0]);
        Assert.EndsWith("Entry 4", tail[1]);
        Assert.Equal(5, log.GetTail(100).Count);
      }
    }

    [Fact]
    public void StaysUnderTheSizeLimitWithOneRollover() {
      const long limit = 4096;
      using (RollingLogFile log = Log(limit)) {
        for (int i = 0; i < 300; i++) {
          log.Write(LogLevel.Info, "Entry " + i.ToString("000") + " " + new string('x', 80));
          log.Flush();
        }
        Assert.True(File.Exists(log.PreviousFilePath));
        Assert.True(new FileInfo(log.FilePath).Length <= limit);
        Assert.True(new FileInfo(log.PreviousFilePath).Length <= limit);
        Assert.EndsWith(new string('x', 80), File.ReadAllLines(log.FilePath).Last());
        Assert.Contains("Entry 299", File.ReadAllText(log.FilePath));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
      }
    }

    [Fact]
    public void KeepsEveryEntryFromManyThreads() {
      using (RollingLogFile log = Log()) {
        Parallel.For(0, 8, thread => {
          for (int i = 0; i < 250; i++) {
            log.Write(LogLevel.Info, "Thread " + thread + " entry " + i);
            if (i % 50 == 0)
              log.Flush();
          }
        });
        log.Flush();
        Assert.Equal(2000, CountEntries(log.FilePath));
      }
    }

    [Fact]
    public void DropsEntriesBeyondTheQueueLimitAndSaysSo() {
      using (RollingLogFile log = Log()) {
        for (int i = 0; i < RollingLogFile.MaxPendingEntries + 5; i++)
          log.Write(LogLevel.Info, "Entry");
        log.Flush();
        string[] lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(RollingLogFile.MaxPendingEntries + 1, lines.Length);
        Assert.Contains("5 log entries were dropped", lines.Last());
      }
    }

    [Fact]
    public void NeverThrowsWhenTheDiskRefuses() {
      string blocker = Path.Combine(directory, "blocker");
      File.WriteAllText(blocker, "a file where a folder should be");
      using (RollingLogFile log = new RollingLogFile(Path.Combine(blocker, "app.log"),
        filter: PrivacyFilter.None, flushDelay: NoTimer)) {
        log.Write(LogLevel.Error, "Nowhere to go");
        log.Flush();
        Assert.Equal(1, log.WriteFailures);
        Assert.Single(log.GetTail(10));
      }
    }

    [Fact]
    public void DisposeWritesWhatIsQueued() {
      RollingLogFile log = Log();
      log.Write(LogLevel.Info, "Stopped");
      log.Dispose();
      Assert.Equal(1, CountEntries(log.FilePath));
      log.Write(LogLevel.Info, "After dispose");
      Assert.Equal(1, CountEntries(log.FilePath));
    }

    [Fact]
    public void RemovesPersonalDetailsBeforeWriting() {
      PrivacyFilter filter = new PrivacyFilter(@"C:\Users\Jane Doe", "Jane Doe", "JANES-DESKTOP");
      using (RollingLogFile log = Log(filter: filter)) {
        log.Write(LogLevel.Warning, @"Access to 'C:\Users\Jane Doe\AppData\Local\x.txt' is denied on JANES-DESKTOP.");
        log.Flush();
        string text = File.ReadAllText(log.FilePath);
        Assert.Contains(@"'%USERPROFILE%\AppData\Local\x.txt'", text);
        Assert.Contains("on %COMPUTERNAME%.", text);
        Assert.DoesNotContain("Jane", text);
      }
    }

    [Fact]
    public void TheStaticLogIsSilentUntilOpened() {
      // Library code logs whether or not an application opened the log.
      ApplicationLog.Info("Nobody is listening");
      ApplicationLog.Flush();
    }

    // ---- repeated errors ----------------------------------------------------------

    [Fact]
    public void LogsARepeatedErrorOncePerInterval() {
      DateTimeOffset now = Time;
      RepeatedErrorFilter filter = new RepeatedErrorFilter(TimeSpan.FromHours(1), () => now);
      InvalidOperationException error = new InvalidOperationException("Device gone");

      Assert.True(filter.ShouldLog(error, out int suppressed));
      Assert.Equal(0, suppressed);
      for (int i = 0; i < 3; i++) {
        now = now.AddSeconds(1);
        Assert.False(filter.ShouldLog(new InvalidOperationException("Device gone"), out _));
      }

      now = now.AddHours(1);
      Assert.True(filter.ShouldLog(error, out suppressed));
      Assert.Equal(3, suppressed);
    }

    [Fact]
    public void LogsADifferentErrorAtOnce() {
      RepeatedErrorFilter filter = new RepeatedErrorFilter(TimeSpan.FromHours(1), () => Time);
      Assert.True(filter.ShouldLog(new InvalidOperationException("A"), out _));
      Assert.True(filter.ShouldLog(new InvalidOperationException("B"), out _));
      Assert.True(filter.ShouldLog(new IOException("A"), out _));
      Assert.True(filter.ShouldLog(new InvalidOperationException("A",
        new TimeoutException("Inner")), out _));
      // Alternating errors are both remembered.
      Assert.False(filter.ShouldLog(new InvalidOperationException("A"), out _));
      Assert.False(filter.ShouldLog(new InvalidOperationException("B"), out _));
    }

    [Fact]
    public void LogsAgainWhenTheClockGoesBack() {
      DateTimeOffset now = Time;
      RepeatedErrorFilter filter = new RepeatedErrorFilter(TimeSpan.FromHours(1), () => now);
      Assert.True(filter.ShouldLog(new InvalidOperationException("A"), out _));
      now = now.AddMinutes(-5);
      Assert.True(filter.ShouldLog(new InvalidOperationException("A"), out _));
    }

    // ---- privacy ----------------------------------------------------------------

    [Theory]
    [InlineData(@"Could not find C:\Users\Jane\Documents\a.md", @"Could not find %USERPROFILE%\Documents\a.md")]
    [InlineData(@"c:\users\JANE\AppData", @"%USERPROFILE%\AppData")]
    [InlineData("C:/Users/Jane/AppData", "%USERPROFILE%/AppData")]
    [InlineData(@"Denied: C:\Users\Other Person\x", @"Denied: %USERPROFILE%\x")]
    [InlineData(@"C:\Users\Public\Documents\a.txt", @"C:\Users\Public\Documents\a.txt")]
    [InlineData(@"C:\Users\Janet\x", @"%USERPROFILE%\x")]
    [InlineData("Signed in as Jane.", "Signed in as %USERNAME%.")]
    [InlineData("Janet and Jane", "Janet and %USERNAME%")]
    [InlineData(@"D:\Games\Jane-Doe", @"D:\Games\%USERNAME%-Doe")]
    [InlineData("Built on WORKSTATION-7.", "Built on %COMPUTERNAME%.")]
    public void ReplacesProfilePathsAndNames(string text, string expected) {
      PrivacyFilter filter = new PrivacyFilter(@"C:\Users\Jane", "Jane", "WORKSTATION-7");
      Assert.Equal(expected, filter.Scrub(text));
    }

    [Fact]
    public void LeavesShortNamesAlone() {
      PrivacyFilter filter = new PrivacyFilter(@"C:\Users\PC", "PC", "PC");
      Assert.Equal("PCI Express on PC hardware", filter.Scrub("PCI Express on PC hardware"));
      Assert.Equal(@"%USERPROFILE%\AppData", filter.Scrub(@"C:\Users\PC\AppData"));
    }

    [Fact]
    public void NoneLeavesTextUnchanged() {
      Assert.Equal(@"C:\Users\Jane\x", PrivacyFilter.None.Scrub(@"C:\Users\Jane\x"));
      Assert.Equal("", PrivacyFilter.None.Scrub(null));
    }
  }
}
