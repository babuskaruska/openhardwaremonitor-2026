/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenHardwareMonitor.Hardware.Maintenance;
using Xunit;

namespace OpenHardwareMonitor.Tests {

  /// <summary>Crash reports, environment facts and prefilled bug report links.</summary>
  public sealed class CrashReportTests : IDisposable {

    private static readonly DateTimeOffset Time =
      new DateTimeOffset(2026, 9, 13, 14, 3, 22, TimeSpan.FromHours(2));

    private readonly string directory = Path.Combine(Path.GetTempPath(),
      "ohm-crash-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
      try {
        if (Directory.Exists(directory))
          Directory.Delete(directory, true);
      } catch (IOException) {
      }
    }

    private static EnvironmentFacts Facts() {
      return new EnvironmentFacts {
        ApplicationVersion = "0.10.0-dev+852d066",
        Windows = "Windows 11 Home 25H2 (build 26200.6584)",
        OsDescription = "Microsoft Windows 10.0.26200",
        Architecture = "X64 process on X64",
        Runtime = ".NET 10.0.0",
        ProcessUptime = new TimeSpan(2, 3, 14, 5),
        IsElevated = true,
        AccessTier = "Deep (PawnIO)"
      };
    }

    private static Exception Thrown() {
      try {
        try {
          throw new IOException(@"The device at C:\Users\Jane\AppData\x is gone.");
        } catch (IOException inner) {
          throw new InvalidOperationException("Sensor update failed.", inner);
        }
      } catch (InvalidOperationException ex) {
        return ex;
      }
    }

    // ---- format -----------------------------------------------------------------

    [Fact]
    public void ContainsTheFactsTheExceptionsAndTheLog() {
      string report = CrashReport.Format(Thrown(), Facts(),
        new[] { "2026-09-13 14:03:20.000 +02:00  WARN   Web server could not start" },
        Time, "Sensor polling (7)", new PrivacyFilter(@"C:\Users\Jane", "Jane", "DESKTOP-JANE"));

      Assert.Contains("Time:           2026-09-13 14:03:22 +02:00", report);
      Assert.Contains("Version:        0.10.0-dev+852d066", report);
      Assert.Contains("Windows:        Windows 11 Home 25H2 (build 26200.6584)", report);
      Assert.Contains("Process uptime: 2 d 03:14:05", report);
      Assert.Contains("Administrator:  yes", report);
      Assert.Contains("Access tier:    Deep (PawnIO)", report);
      Assert.Contains("Thread:         Sensor polling (7)", report);
      Assert.Contains("  System.InvalidOperationException: Sensor update failed.", report);
      Assert.Contains("    System.IO.IOException: The device at", report);
      // The full details, with the stack trace.
      Assert.Contains(" ---> System.IO.IOException", report);
      Assert.Contains("CrashReportTests.Thrown", report);
      Assert.Contains("WARN   Web server could not start", report);
      Assert.Contains(@"%USERPROFILE%\AppData\x", report);
      Assert.DoesNotContain("Jane", report);
    }

    [Fact]
    public void ListsEveryExceptionOfAnAggregate() {
      AggregateException aggregate = new AggregateException(
        new TimeoutException("First"), new ArgumentException("Second"));
      string report = CrashReport.Format(aggregate, new EnvironmentFacts(),
        Array.Empty<string>(), Time, null, PrivacyFilter.None);
      Assert.Contains("    System.TimeoutException: First", report);
      Assert.Contains("    System.ArgumentException: Second", report);
      Assert.Contains("Administrator:  unknown", report);
      Assert.Contains("(none)", report);
      Assert.DoesNotContain("Thread:", report);
    }

    // ---- files ------------------------------------------------------------------

    [Fact]
    public void WritesANamedReportAndNeverOverwrites() {
      string first = CrashReport.Write(directory, "First", Time);
      string second = CrashReport.Write(directory, "Second", Time);

      Assert.Equal(Path.Combine(directory, "crash-20260913-140322.txt"), first);
      Assert.Equal(Path.Combine(directory, "crash-20260913-140322-2.txt"), second);
      Assert.Equal("First", File.ReadAllText(first));
      Assert.Equal("Second", File.ReadAllText(second));
    }

    [Fact]
    public void KeepsOnlyTheNewestReports() {
      Directory.CreateDirectory(directory);
      for (int day = 1; day <= 25; day++)
        File.WriteAllText(Path.Combine(directory, "crash-202608" +
          day.ToString("00", CultureInfo.InvariantCulture) + "-120000.txt"), "old");
      File.WriteAllText(Path.Combine(directory, "notes.txt"), "not a report");

      string newest = CrashReport.Write(directory, "new", Time);

      string[] reports = Directory.GetFiles(directory, "crash-*.txt")
        .Select(name => Path.GetFileName(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray()!;
      Assert.Equal(CrashReport.DefaultKeep, reports.Length);
      Assert.Contains(Path.GetFileName(newest), reports);
      Assert.Equal("crash-20260807-120000.txt", reports[0]);
      Assert.True(File.Exists(Path.Combine(directory, "notes.txt")));
    }

    [Fact]
    public void PruningRanksASameSecondReportAsNewer() {
      Directory.CreateDirectory(directory);
      File.WriteAllText(Path.Combine(directory, "crash-20260913-140322.txt"), "a");
      File.WriteAllText(Path.Combine(directory, "crash-20260913-140322-2.txt"), "b");
      File.WriteAllText(Path.Combine(directory, "crash-20260912-080000.txt"), "c");

      CrashReport.Prune(directory, 1);

      Assert.Equal(new[] { "crash-20260913-140322-2.txt" },
        Directory.GetFiles(directory).Select(name => Path.GetFileName(name)).ToArray());
    }

    // ---- facts ------------------------------------------------------------------

    [Fact]
    public void CapturesFactsWithoutThrowing() {
      EnvironmentFacts facts = EnvironmentFacts.Capture();
      Assert.NotEqual(EnvironmentFacts.Unknown, facts.OsDescription);
      Assert.StartsWith("Windows", facts.Windows);
      Assert.False(string.IsNullOrEmpty(facts.AccessTier));
      Assert.NotNull(facts.ProcessUptime);
    }

    [Theory]
    [InlineData(59, "00:00:59")]
    [InlineData(3 * 3600 + 14 * 60 + 5, "03:14:05")]
    [InlineData(2 * 86400 + 3 * 3600 + 14 * 60 + 5, "2 d 03:14:05")]
    public void FormatsDurations(int seconds, string expected) {
      Assert.Equal(expected, EnvironmentFacts.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void DescribesAccessBriefly() {
      EnvironmentFacts facts = Facts();
      Assert.Equal("Deep (PawnIO), administrator", facts.DescribeAccess());
      facts.IsElevated = false;
      facts.AccessTier = "Base";
      Assert.Equal("Base, not administrator", facts.DescribeAccess());
    }

    // ---- issue links -------------------------------------------------------------

    [Fact]
    public void BuildsAShortPrefilledBugReportLink() {
      string url = IssueLink.BugReport(Facts());
      Assert.Equal("https://github.com/babuskaruska/openhardwaremonitor-2026/issues/new" +
        "?template=bug_report.yml" +
        "&version=0.10.0-dev%2B852d066" +
        "&windows=Windows%2011%20Home%2025H2%20%28build%2026200.6584%29" +
        "&access=Deep%20%28PawnIO%29%2C%20administrator", url);
    }

    [Fact]
    public void ShortensCommitHashesAndLongValues() {
      string url = IssueLink.BugReport("0.10.0-dev+852d066c0ffee852d066c0ffee",
        new string('W', 500), EnvironmentFacts.Unknown);
      Assert.Contains("&version=0.10.0-dev%2B852d066&", url);
      Assert.Contains("&windows=" + new string('W', IssueLink.MaxValueLength), url);
      Assert.DoesNotContain(new string('W', IssueLink.MaxValueLength + 1), url);
      Assert.DoesNotContain("access=", url);
      Assert.True(url.Length < 300);
    }

    [Fact]
    public void LinkFieldsMatchTheIssueTemplate() {
      string? root = AppContext.BaseDirectory;
      while (root != null && !File.Exists(Path.Combine(root, "OpenHardwareMonitor.slnx")))
        root = Path.GetDirectoryName(root);
      Assert.NotNull(root);

      string templates = Path.Combine(root!, ".github", "ISSUE_TEMPLATE");
      string bugReport = File.ReadAllText(Path.Combine(templates, IssueLink.BugReportTemplate));
      foreach (string field in new[] {
        IssueLink.VersionField, IssueLink.WindowsField, IssueLink.AccessField })
        Assert.Contains("id: " + field + "\n", bugReport.Replace("\r\n", "\n"));
      Assert.True(File.Exists(Path.Combine(templates, "hardware_support.yml")));
      Assert.True(File.Exists(Path.Combine(templates, "config.yml")));
    }
  }
}
