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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace OpenHardwareMonitor.Hardware.Diagnostics {

  /// <summary>The exporting application and how long it has been running.</summary>
  public sealed class DiagnosticApplicationInfo {

    internal DiagnosticApplicationInfo() { }

    public string Name { get; internal set; } = "Open Hardware Monitor";
    public string Version { get; internal set; } = "";

    /// <summary>Local process start time, when it could be read.</summary>
    public DateTimeOffset? ProcessStart { get; internal set; }

    /// <summary>
    /// Process start to export. Min/Max readings cover this span (unless the
    /// user reset them in between).
    /// </summary>
    public TimeSpan? Runtime { get; internal set; }

    internal static DiagnosticApplicationInfo Current(string? name) {
      DiagnosticApplicationInfo info = new DiagnosticApplicationInfo();
      Assembly? entry = Assembly.GetEntryAssembly();
      if (!string.IsNullOrWhiteSpace(name))
        info.Name = name;
      else if (entry?.GetName().Name is string entryName)
        info.Name = entryName;

      info.Version = GetVersion(entry) ?? GetVersion(typeof(Computer).Assembly)
        ?? "";

      try {
        using (Process process = Process.GetCurrentProcess())
          info.ProcessStart = new DateTimeOffset(process.StartTime);
      } catch (Exception) {
        info.ProcessStart = null;
      }
      return info;
    }

    private static string? GetVersion(Assembly? assembly) {
      if (assembly == null)
        return null;
      string? informational = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
      if (!string.IsNullOrEmpty(informational))
        return informational;
      return assembly.GetName().Version?.ToString();
    }
  }

  /// <summary>Operating system and process facts relevant to a diagnosis.</summary>
  public sealed class DiagnosticSystemInfo {

    internal DiagnosticSystemInfo() { }

    /// <summary>For example "Windows 11 Pro".</summary>
    public string OsProductName { get; internal set; } = "";

    /// <summary>Feature update, for example "24H2"; null when unknown.</summary>
    public string? OsDisplayVersion { get; internal set; }

    /// <summary>Build and revision, for example "26100.4061"; null when unknown.</summary>
    public string? OsBuild { get; internal set; }

    public string OsDescription { get; internal set; } = "";
    public string OsArchitecture { get; internal set; } = "";
    public string ProcessArchitecture { get; internal set; } = "";

    /// <summary>Time since boot from Environment.TickCount64 (includes sleep).</summary>
    public TimeSpan Uptime { get; internal set; }

    public int LogicalProcessors { get; internal set; }

    /// <summary>Whether the process runs as administrator; null when unknown.</summary>
    public bool? IsElevated { get; internal set; }

    public string DotNetRuntime { get; internal set; } = "";

    private const int FirstWindows11Build = 22000;

    internal static DiagnosticSystemInfo Current() {
      DiagnosticSystemInfo info = new DiagnosticSystemInfo {
        OsDescription = RuntimeInformation.OSDescription,
        OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        LogicalProcessors = Environment.ProcessorCount,
        DotNetRuntime = RuntimeInformation.FrameworkDescription,
        IsElevated = GetElevation()
      };
      ReadWindowsVersion(info);
      if (string.IsNullOrEmpty(info.OsProductName))
        info.OsProductName = info.OsDescription;
      return info;
    }

    private static bool? GetElevation() {
      try {
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
          return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
      } catch (Exception) {
        return null;
      }
    }

    private static void ReadWindowsVersion(DiagnosticSystemInfo info) {
      try {
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(
          @"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) {
          if (key == null)
            return;

          string? product = key.GetValue("ProductName") as string;
          string? display = key.GetValue("DisplayVersion") as string ??
            key.GetValue("ReleaseId") as string;
          string? build = key.GetValue("CurrentBuildNumber") as string ??
            key.GetValue("CurrentBuild") as string;
          object? ubr = key.GetValue("UBR");

          // Windows 11 still reports "Windows 10" as its product name; the
          // build number is what tells them apart.
          if (product != null && build != null &&
            int.TryParse(build, NumberStyles.Integer,
              CultureInfo.InvariantCulture, out int buildNumber) &&
            buildNumber >= FirstWindows11Build &&
            product.StartsWith("Windows 10", StringComparison.Ordinal))
            product = "Windows 11" + product.Substring("Windows 10".Length);

          info.OsProductName = product ?? "";
          info.OsDisplayVersion = string.IsNullOrEmpty(display) ? null : display;
          if (!string.IsNullOrEmpty(build))
            info.OsBuild = ubr is int revision
              ? build + "." + revision.ToString(CultureInfo.InvariantCulture)
              : build;
        }
      } catch (Exception) {
        // Registry access is best effort; OsDescription still identifies the OS.
      }
    }
  }

  /// <summary>Copy of <see cref="HardwareAccess"/> at capture time.</summary>
  public sealed class DiagnosticAccessInfo {

    internal DiagnosticAccessInfo() { }

    public AccessTier Tier { get; internal set; }
    public string BackendName { get; internal set; } = "";
    public bool SupportsModelSpecificRegisters { get; internal set; }
    public bool SupportsIoPort { get; internal set; }
    public bool SupportsPciConfig { get; internal set; }

    /// <summary>Why some sensors are unavailable; null when nothing is missing.</summary>
    public string? UnavailableReason { get; internal set; }

    internal static DiagnosticAccessInfo Current() {
      DiagnosticAccessInfo info = new DiagnosticAccessInfo();
      try {
        info.Tier = HardwareAccess.Tier;
        info.BackendName = HardwareAccess.BackendName ?? "";
        info.SupportsModelSpecificRegisters =
          HardwareAccess.SupportsModelSpecificRegisters;
        info.SupportsIoPort = HardwareAccess.SupportsIoPort;
        info.SupportsPciConfig = HardwareAccess.SupportsPciConfig;
        info.UnavailableReason = HardwareAccess.UnavailableReason;
      } catch (Exception ex) {
        info.UnavailableReason = "Access state could not be read (" +
          DiagnosticSnapshot.Describe(ex) + ").";
      }
      return info;
    }
  }
}
