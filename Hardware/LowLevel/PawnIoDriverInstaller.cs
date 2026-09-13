/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  public enum PawnIoDriverInstallOutcome {
    /// <summary>PawnIO is installed now.</summary>
    Installed,
    /// <summary>winget is not available; the PawnIO website was opened.</summary>
    WingetMissing,
    /// <summary>The Windows permission prompt was declined.</summary>
    Declined,
    Failed
  }

  public sealed class PawnIoDriverInstallResult {

    internal PawnIoDriverInstallResult(PawnIoDriverInstallOutcome outcome,
      string message, int? exitCode = null) {
      Outcome = outcome;
      Message = message;
      ExitCode = exitCode;
    }

    public PawnIoDriverInstallOutcome Outcome { get; }

    /// <summary>What happened and what to do next, for the user.</summary>
    public string Message { get; }

    public int? ExitCode { get; }

    public bool Succeeded {
      get { return Outcome == PawnIoDriverInstallOutcome.Installed; }
    }
  }

  /// <summary>
  /// Installs the PawnIO driver with winget. PawnIO is deliberately not
  /// bundled (see <see cref="PawnIOLib"/>), so the package comes from the
  /// winget community repository, and its installer raises the Windows
  /// permission prompt itself. The user interface must say before starting
  /// that this installs a signed kernel driver and accepts the winget source
  /// and package agreements on the user's behalf.
  /// </summary>
  public sealed class PawnIoDriverInstaller {

    public const string PackageId = "namazso.PawnIO";
    public const string WebsiteUrl = "https://pawnio.eu";

    internal static readonly string[] WingetArguments = {
      "install", "-e", "--id", PackageId,
      "--accept-source-agreements", "--accept-package-agreements"
    };

    // ERROR_CANCELLED, as a plain exit code and as an HRESULT, which is what
    // an installer returns when its elevation prompt is declined.
    private const int ErrorCancelled = 1223;
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);

    private readonly Func<string?> findWinget;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runProcess;
    private readonly Action<string> openUrl;
    private readonly Func<bool> isInstalled;

    public PawnIoDriverInstaller()
      : this(FindWinget, RunProcessAsync, OpenUrl, () => PawnIOLib.IsInstalled) {
    }

    /// <summary>For tests: every side effect is injected.</summary>
    internal PawnIoDriverInstaller(Func<string?> findWinget,
      Func<ProcessStartInfo, CancellationToken, Task<int>> runProcess,
      Action<string> openUrl, Func<bool> isInstalled) {
      this.findWinget = findWinget;
      this.runProcess = runProcess;
      this.openUrl = openUrl;
      this.isInstalled = isInstalled;
    }

    /// <summary>
    /// Runs winget and waits for it. Cancelling stops the wait but leaves
    /// winget running: killing an installer halfway through is worse than
    /// letting it finish.
    /// </summary>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<PawnIoDriverInstallResult> InstallAsync(
      IProgress<string>? progress, CancellationToken cancellationToken) {

      if (isInstalled())
        return Installed();

      string? winget = findWinget();
      if (winget == null)
        return OpenWebsite();

      progress?.Report("Installing PawnIO with winget. Windows asks for permission.");
      int exitCode;
      try {
        exitCode = await runProcess(CreateStartInfo(winget), cancellationToken)
          .ConfigureAwait(false);
      } catch (Win32Exception) {
        // Found but not startable, for example an App Installer alias whose
        // package was removed.
        return OpenWebsite();
      }

      // winget reports "already installed" and similar as failures, so the
      // result is judged by whether PawnIO can be loaded now.
      if (isInstalled())
        return Installed();

      if (exitCode == ErrorCancelled || exitCode == ErrorCancelledHResult)
        return new PawnIoDriverInstallResult(PawnIoDriverInstallOutcome.Declined,
          "PawnIO was not installed because the Windows permission prompt was " +
          "declined. Choose Install again and allow it.", exitCode);

      string code = "0x" + exitCode.ToString("X8", CultureInfo.InvariantCulture);
      return new PawnIoDriverInstallResult(PawnIoDriverInstallOutcome.Failed,
        exitCode == 0
          ? "winget finished, but PawnIO cannot be found yet. If Windows asks " +
            "for a restart, restart and try again."
          : "winget could not install PawnIO (error " + code + "). Check your " +
            "internet connection and try again, or install PawnIO from " +
            WebsiteUrl + ".",
        exitCode);
    }

    internal static ProcessStartInfo CreateStartInfo(string winget) {
      ProcessStartInfo info = new ProcessStartInfo(winget) {
        UseShellExecute = false,
        CreateNoWindow = true
      };
      foreach (string argument in WingetArguments)
        info.ArgumentList.Add(argument);
      return info;
    }

    private PawnIoDriverInstallResult Installed() {
      return new PawnIoDriverInstallResult(PawnIoDriverInstallOutcome.Installed,
        "PawnIO is installed.");
    }

    private PawnIoDriverInstallResult OpenWebsite() {
      try {
        openUrl(WebsiteUrl);
        return new PawnIoDriverInstallResult(PawnIoDriverInstallOutcome.WingetMissing,
          "winget is not available on this PC, so the PawnIO website was " +
          "opened. Download and run the PawnIO installer there, then choose " +
          "Try again.");
      } catch (Exception) {
        return new PawnIoDriverInstallResult(PawnIoDriverInstallOutcome.WingetMissing,
          "winget is not available on this PC. Install PawnIO from " +
          WebsiteUrl + ", then choose Try again.");
      }
    }

    /// <summary>
    /// winget is an App Installer execution alias in the user's WindowsApps
    /// folder, which is normally on PATH. Relative PATH entries are skipped so
    /// the current directory can never supply it.
    /// </summary>
    private static string? FindWinget() {
      return FindWinget(Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists);
    }

    internal static string? FindWinget(string? pathVariable, string? localAppData,
      Func<string, bool> fileExists) {
      if (!string.IsNullOrEmpty(pathVariable)) {
        foreach (string entry in pathVariable.Split(Path.PathSeparator)) {
          string directory = entry.Trim().Trim('"');
          if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
            continue;
          string candidate = Path.Combine(directory, "winget.exe");
          if (fileExists(candidate))
            return candidate;
        }
      }
      if (!string.IsNullOrEmpty(localAppData)) {
        string alias = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
        if (fileExists(alias))
          return alias;
      }
      return null;
    }

    private static async Task<int> RunProcessAsync(ProcessStartInfo info,
      CancellationToken cancellationToken) {
      using (Process process = Process.Start(info) ??
        throw new Win32Exception("winget could not be started.")) {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
      }
    }

    private static void OpenUrl(string url) {
      Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }
  }
}
