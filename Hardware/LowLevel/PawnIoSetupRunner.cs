/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenHardwareMonitor.Hardware.LowLevel {

  /// <summary>Progress text for one step.</summary>
  public readonly struct PawnIoSetupProgress {
    public PawnIoSetupProgress(PawnIoSetupStep step, string message) {
      Step = step;
      Message = message;
    }

    public PawnIoSetupStep Step { get; }
    public string Message { get; }
  }

  public sealed class PawnIoSetupRunResult {
    internal PawnIoSetupRunResult(PawnIoSetupStatus status,
      PawnIoSetupStep? failedStep, string? error) {
      Status = status;
      FailedStep = failedStep;
      Error = error;
    }

    /// <summary>The state after running.</summary>
    public PawnIoSetupStatus Status { get; }

    /// <summary>The step that stopped the run, or null.</summary>
    public PawnIoSetupStep? FailedStep { get; }

    /// <summary>What went wrong and what to do next, or null.</summary>
    public string? Error { get; }
  }

  /// <summary>
  /// Runs the install steps that are still needed, in order, stopping at the
  /// first one that fails. The restart is left to the caller, which owns the
  /// application window.
  /// </summary>
  public sealed class PawnIoSetupRunner {

    private readonly Func<PawnIoSetupStatus> captureStatus;
    private readonly Func<IProgress<string>?, CancellationToken,
      Task<PawnIoDriverInstallResult>> installDriver;
    private readonly Func<string, IProgress<string>?, CancellationToken,
      Task<PawnIoModuleInstallResult>> installModules;

    public PawnIoSetupRunner()
      : this(PawnIoSetupStatus.Capture,
          (progress, token) => new PawnIoDriverInstaller().InstallAsync(progress, token),
          InstallModulesAsync) {
    }

    /// <summary>For tests: status and both installers are injected.</summary>
    internal PawnIoSetupRunner(Func<PawnIoSetupStatus> captureStatus,
      Func<IProgress<string>?, CancellationToken, Task<PawnIoDriverInstallResult>> installDriver,
      Func<string, IProgress<string>?, CancellationToken, Task<PawnIoModuleInstallResult>> installModules) {
      this.captureStatus = captureStatus;
      this.installDriver = installDriver;
      this.installModules = installModules;
    }

    private static async Task<PawnIoModuleInstallResult> InstallModulesAsync(
      string directory, IProgress<string>? progress, CancellationToken token) {
      using (PawnIoModuleInstaller installer = new PawnIoModuleInstaller())
        return await installer.InstallLatestAsync(directory, progress, token)
          .ConfigureAwait(false);
    }

    /// <exception cref="OperationCanceledException">Cancelled by the caller.</exception>
    public async Task<PawnIoSetupRunResult> RunAsync(
      IProgress<PawnIoSetupProgress>? progress, CancellationToken cancellationToken) {

      PawnIoSetupStatus status = captureStatus();

      if (!status.DriverInstalled) {
        PawnIoDriverInstallResult driver = await installDriver(
          ForStep(progress, PawnIoSetupStep.InstallDriver), cancellationToken)
          .ConfigureAwait(false);
        status = captureStatus();
        if (!status.DriverInstalled)
          return new PawnIoSetupRunResult(status, PawnIoSetupStep.InstallDriver,
            driver.Succeeded
              ? "PawnIO was installed but cannot be used yet. Restart Windows, then try again."
              : driver.Message);
      }

      if (!status.ModulesInstalled) {
        try {
          await installModules(status.ModulesDirectory,
            ForStep(progress, PawnIoSetupStep.InstallModules), cancellationToken)
            .ConfigureAwait(false);
        } catch (PawnIoSetupException ex) {
          return new PawnIoSetupRunResult(captureStatus(), PawnIoSetupStep.InstallModules,
            ex.Message);
        }
        status = captureStatus();
        if (!status.ModulesInstalled)
          return new PawnIoSetupRunResult(status, PawnIoSetupStep.InstallModules,
            "The latest PawnIO modules release does not include " +
            string.Join(" and ", status.MissingModules) + ". Try again later.");
      }

      return new PawnIoSetupRunResult(status, null, null);
    }

    private static IProgress<string>? ForStep(IProgress<PawnIoSetupProgress>? progress,
      PawnIoSetupStep step) {
      return progress == null ? null : new StepProgress(progress, step);
    }

    // Forwards synchronously; the caller's IProgress decides the thread.
    private sealed class StepProgress : IProgress<string> {
      private readonly IProgress<PawnIoSetupProgress> target;
      private readonly PawnIoSetupStep step;

      public StepProgress(IProgress<PawnIoSetupProgress> target, PawnIoSetupStep step) {
        this.target = target;
        this.step = step;
      }

      public void Report(string value) {
        target.Report(new PawnIoSetupProgress(step, value));
      }
    }
  }
}
