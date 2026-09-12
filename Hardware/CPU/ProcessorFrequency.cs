/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Diagnostics;

namespace OpenHardwareMonitor.Hardware.CPU {

  /// <summary>
  /// Live per-core clock speeds without a kernel driver.
  ///
  /// The precise way to do this is APERF/MPERF or IA32_PERF_STATUS, but both
  /// are model specific registers and need ring 0. The alternative that does
  /// not is the "% Processor Performance" counter, which is what Windows' own
  /// Task Manager reports: the kernel's measure of delivered performance
  /// relative to the processor's nominal frequency, so
  ///
  ///     current MHz = nominal MHz * (% Processor Performance / 100)
  ///
  /// This tracks Speed Shift / HWP boost correctly, and it is per logical
  /// processor, so P-cores and E-cores are reported separately and accurately.
  ///
  /// Note that CallNtPowerInformation's CurrentMhz is NOT a usable substitute.
  /// On current Windows with hardware-managed P-states it simply echoes the
  /// nominal frequency - measured on an i7-14700KF it returned a flat
  /// 3400 MHz whether idle or fully loaded.
  /// </summary>
  internal sealed class ProcessorFrequency : IDisposable {

    private readonly PerformanceCounter?[] counters;
    private readonly float nominalMegahertz;
    private bool disposed;

    public ProcessorFrequency(CPUID[][] cpuid) {
      counters = new PerformanceCounter?[cpuid.Length];
      nominalMegahertz = GetNominalMegahertz(cpuid);

      if (nominalMegahertz <= 0)
        return;

      for (int i = 0; i < cpuid.Length; i++) {
        if (cpuid[i].Length == 0)
          continue;

        // Counter instances are named "<group>,<logical processor>".
        string instance = cpuid[i][0].Group + "," + cpuid[i][0].Thread;
        try {
          counters[i] = new PerformanceCounter("Processor Information",
            "% Processor Performance", instance, true);
          // The first sample of a rate counter is meaningless; prime it.
          counters[i]!.NextValue();
        } catch (Exception) {
          // The counter set can be missing, disabled or corrupted. Losing a
          // clock reading is not worth failing hardware detection over.
          counters[i] = null;
        }
      }
    }

    private static float GetNominalMegahertz(CPUID[][] cpuid) {
      uint[]? max = ProcessorPowerInformation.TryGetMaxMegahertz();
      if (max != null && cpuid.Length > 0 && cpuid[0].Length > 0) {
        int logicalProcessor = cpuid[0][0].Group * 64 + cpuid[0][0].Thread;
        if (logicalProcessor >= 0 && logicalProcessor < max.Length &&
          max[logicalProcessor] > 0)
          return max[logicalProcessor];
      }

      if (CpuInstructions.TryGetTimeStampCounterFrequency(out double tsc) &&
        tsc > 0)
        return (float)tsc;

      return 0;
    }

    public bool IsAvailable {
      get {
        if (nominalMegahertz <= 0)
          return false;
        foreach (PerformanceCounter? counter in counters) {
          if (counter != null)
            return true;
        }
        return false;
      }
    }

    /// <summary>
    /// Current clock of the given core in MHz, or null when unavailable.
    /// </summary>
    public float? GetCoreFrequency(int coreIndex) {
      if (disposed || coreIndex < 0 || coreIndex >= counters.Length)
        return null;

      PerformanceCounter? counter = counters[coreIndex];
      if (counter == null || nominalMegahertz <= 0)
        return null;

      try {
        float performance = counter.NextValue();
        if (performance <= 0)
          return null;
        return nominalMegahertz * performance * 0.01f;
      } catch (Exception) {
        return null;
      }
    }

    public void Dispose() {
      if (disposed)
        return;
      disposed = true;
      for (int i = 0; i < counters.Length; i++) {
        try {
          counters[i]?.Dispose();
        } catch (Exception) {
          // Nothing useful to do while tearing down.
        }
        counters[i] = null;
      }
    }
  }
}
