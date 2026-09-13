/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenHardwareMonitor.Hardware.Diagnostics;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>
  /// Software facts for crash reports and bug reports. Deliberately nothing
  /// personal: no account or computer name, no paths.
  ///
  /// <see cref="Capture"/> is used from a crash handler, so every fact is
  /// read on its own and a failure leaves only that fact unknown. It takes no
  /// locks, so a crash on a thread holding the hardware lock cannot hang it.
  /// </summary>
  public sealed class EnvironmentFacts {

    public const string Unknown = "unknown";

    public string ApplicationVersion { get; set; } = Unknown;

    /// <summary>For example "Windows 11 Home 25H2 (build 26200.6584)".</summary>
    public string Windows { get; set; } = Unknown;

    public string OsDescription { get; set; } = Unknown;

    /// <summary>For example "X64 process on X64".</summary>
    public string Architecture { get; set; } = Unknown;

    public string Runtime { get; set; } = Unknown;

    public TimeSpan? ProcessUptime { get; set; }

    public bool? IsElevated { get; set; }

    /// <summary>"Deep (PawnIO)" or "Base".</summary>
    public string AccessTier { get; set; } = Unknown;

    public static EnvironmentFacts Capture() {
      EnvironmentFacts facts = new EnvironmentFacts();
      try {
        DiagnosticApplicationInfo application = DiagnosticApplicationInfo.Current(null);
        if (!string.IsNullOrEmpty(application.Version))
          facts.ApplicationVersion = application.Version;
        if (application.ProcessStart.HasValue)
          facts.ProcessUptime = DateTimeOffset.Now - application.ProcessStart.Value;
      } catch (Exception) { }

      try {
        DiagnosticSystemInfo system = DiagnosticSystemInfo.Current();
        facts.Windows = DescribeWindows(system.OsProductName,
          system.OsDisplayVersion, system.OsBuild);
        facts.OsDescription = system.OsDescription;
        facts.Architecture = system.ProcessArchitecture + " process on " +
          system.OsArchitecture;
        facts.Runtime = system.DotNetRuntime;
        facts.IsElevated = system.IsElevated;
      } catch (Exception) {
        try {
          facts.OsDescription = RuntimeInformation.OSDescription;
        } catch (Exception) { }
      }

      try {
        facts.AccessTier = HardwareAccess.Tier == OpenHardwareMonitor.Hardware.AccessTier.Deep
          ? "Deep (" + HardwareAccess.BackendName + ")" : "Base";
      } catch (Exception) { }
      return facts;
    }

    internal static string DescribeWindows(string? product, string? displayVersion,
      string? build) {
      string text = string.IsNullOrWhiteSpace(product) ? "Windows" : product.Trim();
      if (!string.IsNullOrWhiteSpace(displayVersion))
        text += " " + displayVersion.Trim();
      if (!string.IsNullOrWhiteSpace(build))
        text += " (build " + build.Trim() + ")";
      return text;
    }

    /// <summary>"Deep (PawnIO), administrator", for a short bug report field.</summary>
    public string DescribeAccess() {
      if (IsElevated == true)
        return AccessTier + ", administrator";
      if (IsElevated == false)
        return AccessTier + ", not administrator";
      return AccessTier;
    }

    /// <summary>"2 d 03:14:05", or "unknown".</summary>
    public static string FormatDuration(TimeSpan? duration) {
      if (!duration.HasValue || duration.Value < TimeSpan.Zero)
        return Unknown;
      TimeSpan d = duration.Value;
      string clock = d.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
      return d.Days > 0
        ? d.Days.ToString(CultureInfo.InvariantCulture) + " d " + clock : clock;
    }
  }
}
