/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Runtime.InteropServices;

namespace OpenHardwareMonitor.Hardware.CPU {

  /// <summary>
  /// Per-logical-processor clock speeds from the power management subsystem.
  ///
  /// This is the driver-free clock source. The MSR path (IA32_PERF_STATUS
  /// scaled by the bus clock) is more precise and is still preferred when a
  /// low-level backend is available, but it needs ring 0. CallNtPowerInformation
  /// needs nothing at all and works for both Intel and AMD, so without a
  /// backend the application reports real clocks instead of no clocks.
  ///
  /// The values come from the kernel's own performance state accounting, so
  /// they are averaged over a short interval rather than instantaneous.
  /// </summary>
  internal static class ProcessorPowerInformation {

    private const int ProcessorInformation = 11;
    private const uint StatusSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSOR_POWER_INFORMATION {
      public uint Number;
      public uint MaxMhz;
      public uint CurrentMhz;
      public uint MhzLimit;
      public uint MaxIdleState;
      public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
      int informationLevel, IntPtr inputBuffer, uint inputBufferSize,
      IntPtr outputBuffer, uint outputBufferSize);

    /// <summary>
    /// Current clock in MHz for every logical processor, indexed by the
    /// system's logical processor number, or null if unavailable.
    /// </summary>
    public static uint[]? TryGetCurrentMegahertz() {
      return TryQuery(info => info.CurrentMhz);
    }

    /// <summary>
    /// Maximum rated clock in MHz for every logical processor, or null.
    /// </summary>
    public static uint[]? TryGetMaxMegahertz() {
      return TryQuery(info => info.MaxMhz);
    }

    private static uint[]? TryQuery(Func<PROCESSOR_POWER_INFORMATION, uint> select) {
      int count = Environment.ProcessorCount;
      int entrySize = Marshal.SizeOf<PROCESSOR_POWER_INFORMATION>();
      IntPtr buffer = IntPtr.Zero;

      try {
        buffer = Marshal.AllocHGlobal(entrySize * count);
        uint status = CallNtPowerInformation(ProcessorInformation,
          IntPtr.Zero, 0, buffer, (uint)(entrySize * count));
        if (status != StatusSuccess)
          return null;

        uint[] result = new uint[count];
        for (int i = 0; i < count; i++) {
          PROCESSOR_POWER_INFORMATION info =
            Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(
              buffer + i * entrySize);
          result[i] = select(info);
        }
        return result;
      } catch (Exception) {
        // powrprof.dll is Windows-only and the call can fail inside a
        // container or a restricted session; no clocks is an acceptable
        // outcome, a crash is not.
        return null;
      } finally {
        if (buffer != IntPtr.Zero)
          Marshal.FreeHGlobal(buffer);
      }
    }
  }
}
