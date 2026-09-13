/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Text;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>
  /// Addresses of prefilled GitHub issues. The query parameter names are the
  /// field ids in .github/ISSUE_TEMPLATE/bug_report.yml; GitHub fills a form
  /// field whose id matches a parameter. Only short software facts go into
  /// the address. The diagnostics report is attached by the user.
  /// </summary>
  public static class IssueLink {

    public const string NewIssueUrl = "https://github.com/" + UpdateChecker.Repository +
      "/issues/new";
    public const string BugReportTemplate = "bug_report.yml";

    public const string VersionField = "version";
    public const string WindowsField = "windows";
    public const string AccessField = "access";

    internal const int MaxValueLength = 80;
    private const int ShortCommitLength = 7;

    public static string BugReport(EnvironmentFacts facts) {
      if (facts == null)
        throw new ArgumentNullException(nameof(facts));
      return BugReport(facts.ApplicationVersion, facts.Windows, facts.DescribeAccess());
    }

    public static string BugReport(string? version, string? windows, string? access) {
      StringBuilder url = new StringBuilder(NewIssueUrl)
        .Append("?template=").Append(BugReportTemplate);
      Append(url, VersionField, ShortenVersion(version));
      Append(url, WindowsField, windows);
      Append(url, AccessField, access);
      return url.ToString();
    }

    /// <summary>"0.10.0-dev+852d066c…" becomes "0.10.0-dev+852d066".</summary>
    internal static string ShortenVersion(string? version) {
      if (string.IsNullOrEmpty(version))
        return "";
      int plus = version.IndexOf('+');
      if (plus < 0 || version.Length - plus - 1 <= ShortCommitLength)
        return version;
      return version.Substring(0, plus + 1 + ShortCommitLength);
    }

    private static void Append(StringBuilder url, string field, string? value) {
      if (string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, EnvironmentFacts.Unknown, StringComparison.Ordinal))
        return;
      StringBuilder clean = new StringBuilder();
      foreach (char c in value.Trim()) {
        if (clean.Length >= MaxValueLength)
          break;
        clean.Append(char.IsControl(c) ? ' ' : c);
      }
      url.Append('&').Append(field).Append('=')
        .Append(Uri.EscapeDataString(clean.ToString().Trim()));
    }
  }
}
