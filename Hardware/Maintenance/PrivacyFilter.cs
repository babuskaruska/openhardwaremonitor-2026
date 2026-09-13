/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Text.RegularExpressions;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>
  /// Removes personal details from text that may end up in a public bug
  /// report: crash reports, the application log and prefilled issue fields.
  ///
  /// Exception messages and stack traces routinely contain file paths, and a
  /// path under C:\Users carries the account name. Those become
  /// %USERPROFILE%. The account and computer names are also replaced where
  /// they appear as whole words; names shorter than three characters are left
  /// alone, because replacing "PC" would also mangle unrelated text.
  /// </summary>
  public sealed class PrivacyFilter {

    public const string ProfileToken = "%USERPROFILE%";
    public const string UserNameToken = "%USERNAME%";
    public const string ComputerNameToken = "%COMPUTERNAME%";

    private const int MinimumNameLength = 3;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // Any other profile folder, including another account's (an elevated
    // process can run under a different one). Stops at a separator, quote or
    // line end, so a name with spaces is removed completely.
    private static readonly Regex UsersFolder = new Regex(
      @"\b[A-Za-z]:[\\/]Users[\\/](?!(?:Public|Default|All Users)(?:[\\/]|$))[^\\/:*?""<>|'\r\n]+",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    private static PrivacyFilter? current;

    private readonly bool enabled;
    private readonly Regex? profile;
    private readonly Regex? userName;
    private readonly Regex? computerName;

    private PrivacyFilter() {
      enabled = false;
    }

    public PrivacyFilter(string? userProfile, string? userName, string? computerName) {
      enabled = true;
      if (!string.IsNullOrWhiteSpace(userProfile) && userProfile.Trim().Length > 3) {
        string trimmed = userProfile.Trim().TrimEnd('\\', '/');
        string pattern = Regex.Escape(trimmed).Replace(@"\\", @"[\\/]") + @"(?![\w])";
        profile = new Regex(pattern, RegexOptions.IgnoreCase |
          RegexOptions.CultureInvariant, MatchTimeout);
      }
      this.userName = WholeWord(userName);
      this.computerName = WholeWord(computerName);
    }

    /// <summary>The filter for the current account and computer.</summary>
    public static PrivacyFilter Current {
      get { return current ??= CreateCurrent(); }
    }

    /// <summary>Leaves text unchanged.</summary>
    public static PrivacyFilter None { get; } = new PrivacyFilter();

    private static PrivacyFilter CreateCurrent() {
      string? profilePath = null, user = null, machine = null;
      try {
        profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      } catch (Exception) { }
      try {
        user = Environment.UserName;
      } catch (Exception) { }
      try {
        machine = Environment.MachineName;
      } catch (Exception) { }
      return new PrivacyFilter(profilePath, user, machine);
    }

    private static Regex? WholeWord(string? name) {
      if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < MinimumNameLength)
        return null;
      return new Regex(@"(?<![\w%])" + Regex.Escape(name.Trim()) + @"(?![\w%])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
    }

    public string Scrub(string? text) {
      if (string.IsNullOrEmpty(text))
        return "";
      if (!enabled)
        return text;
      try {
        string result = text;
        if (profile != null)
          result = profile.Replace(result, ProfileToken);
        result = UsersFolder.Replace(result, ProfileToken);
        if (userName != null)
          result = userName.Replace(result, UserNameToken);
        if (computerName != null)
          result = computerName.Replace(result, ComputerNameToken);
        return result;
      } catch (RegexMatchTimeoutException) {
        // Pathological input. Dropping the text is the private choice.
        return "(text removed: it could not be checked for personal details)";
      }
    }
  }
}
