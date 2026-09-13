/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

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
  ///
  /// The counter is read through PDH with one wildcard query, not with one
  /// System.Diagnostics.PerformanceCounter per logical processor. The managed
  /// class locates its category by reading the performance data of every
  /// provider on the system, which took 0.4 s when warm and about 5 s after a
  /// cold start on a 28-thread processor, all while hardware detection waited.
  /// PDH asks only the Processor Information provider, and a single query
  /// returns every processor at once.
  /// </summary>
  internal sealed class ProcessorFrequency : IDisposable {

    private const string CounterPath =
      @"\Processor Information(*)\% Processor Performance";

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    // Boosted cores deliver more than 100 % of nominal performance.
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private const uint PDH_CSTATUS_VALID_DATA = 0;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    // Reads closer together than this belong to the same sensor update, so
    // every core of one update shares one sample.
    private static readonly long SampleInterval = Stopwatch.Frequency / 10;

    private readonly int[] logicalProcessors;
    private readonly Dictionary<int, float> performance = new Dictionary<int, float>();
    private readonly float nominalMegahertz;
    private IntPtr query;
    private IntPtr counter;
    private IntPtr buffer;
    private uint bufferSize;
    private long lastSample;

    public ProcessorFrequency(CPUID[][] cpuid) {
      logicalProcessors = new int[cpuid.Length];
      for (int i = 0; i < cpuid.Length; i++)
        logicalProcessors[i] = cpuid[i].Length == 0 ? -1 :
          LogicalProcessor(cpuid[i][0].Group, cpuid[i][0].Thread);

      nominalMegahertz = GetNominalMegahertz(cpuid);
      if (nominalMegahertz <= 0)
        return;

      try {
        if (NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) {
          query = IntPtr.Zero;
          return;
        }
        // The first collection of a rate counter only establishes a baseline.
        if (NativeMethods.PdhAddEnglishCounter(query, CounterPath, IntPtr.Zero,
          out counter) != 0 || NativeMethods.PdhCollectQueryData(query) != 0) {
          // The counter set can be missing, disabled or corrupted. Losing a
          // clock reading is not worth failing hardware detection over.
          Close();
          return;
        }
        lastSample = Stopwatch.GetTimestamp();
      } catch (DllNotFoundException) {
        Close();
      } catch (EntryPointNotFoundException) {
        Close();
      }
    }

    private static int LogicalProcessor(int group, int thread) {
      return group * 64 + thread;
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
      get { return counter != IntPtr.Zero && nominalMegahertz > 0; }
    }

    /// <summary>
    /// Current clock of the given core in MHz, or null when unavailable.
    /// </summary>
    public float? GetCoreFrequency(int coreIndex) {
      if (!IsAvailable || coreIndex < 0 || coreIndex >= logicalProcessors.Length)
        return null;

      long now = Stopwatch.GetTimestamp();
      if (now - lastSample >= SampleInterval) {
        lastSample = now;
        Sample();
      }

      if (performance.TryGetValue(logicalProcessors[coreIndex], out float percent) &&
        percent > 0)
        return nominalMegahertz * percent * 0.01f;
      return null;
    }

    private void Sample() {
      performance.Clear();
      if (NativeMethods.PdhCollectQueryData(query) != 0)
        return;

      const uint Format = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;
      uint size = bufferSize;
      int status = NativeMethods.PdhGetFormattedCounterArray(counter, Format,
        ref size, out uint count, buffer);
      if (status == PDH_MORE_DATA) {
        if (buffer != IntPtr.Zero)
          Marshal.FreeHGlobal(buffer);
        buffer = Marshal.AllocHGlobal((int)size);
        bufferSize = size;
        status = NativeMethods.PdhGetFormattedCounterArray(counter, Format,
          ref size, out count, buffer);
      }
      if (status != 0)
        return;

      int itemSize = Marshal.SizeOf<CounterValueItem>();
      for (int i = 0; i < count; i++) {
        CounterValueItem item =
          Marshal.PtrToStructure<CounterValueItem>(IntPtr.Add(buffer, i * itemSize));
        if (item.Status != PDH_CSTATUS_VALID_DATA &&
          item.Status != PDH_CSTATUS_NEW_DATA)
          continue;
        if (TryParseInstance(Marshal.PtrToStringUni(item.Name), out int logical))
          performance[logical] = (float)item.Value;
      }
    }

    /// <summary>
    /// Instances are named "&lt;group&gt;,&lt;logical processor&gt;", next to
    /// totals such as "_Total" and "0,_Total", which are skipped.
    /// </summary>
    private static bool TryParseInstance(string? name, out int logical) {
      logical = -1;
      if (name == null)
        return false;
      int comma = name.IndexOf(',');
      if (comma <= 0 ||
        !int.TryParse(name.AsSpan(0, comma), NumberStyles.None,
          CultureInfo.InvariantCulture, out int group) ||
        !int.TryParse(name.AsSpan(comma + 1), NumberStyles.None,
          CultureInfo.InvariantCulture, out int thread))
        return false;
      logical = LogicalProcessor(group, thread);
      return true;
    }

    private void Close() {
      if (query != IntPtr.Zero) {
        // Closing the query also closes its counter.
        NativeMethods.PdhCloseQuery(query);
        query = IntPtr.Zero;
      }
      counter = IntPtr.Zero;
      if (buffer != IntPtr.Zero) {
        Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        bufferSize = 0;
      }
      performance.Clear();
    }

    public void Dispose() {
      Close();
    }

    // PDH_FMT_COUNTERVALUE_ITEM_W with a double value: the instance name, then
    // PDH_FMT_COUNTERVALUE (a status and, aligned to 8 bytes, the value).
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem {
      public IntPtr Name;
      public uint Status;
      public double Value;
    }

    private static class NativeMethods {
      private const string PDH = "pdh.dll";

      [DllImport(PDH, CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
      public static extern int PdhOpenQuery(string? dataSource, IntPtr userData,
        out IntPtr query);

      [DllImport(PDH, CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
      public static extern int PdhAddEnglishCounter(IntPtr query, string counterPath,
        IntPtr userData, out IntPtr counter);

      [DllImport(PDH)]
      public static extern int PdhCollectQueryData(IntPtr query);

      [DllImport(PDH, CharSet = CharSet.Unicode,
        EntryPoint = "PdhGetFormattedCounterArrayW")]
      public static extern int PdhGetFormattedCounterArray(IntPtr counter,
        uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

      [DllImport(PDH)]
      public static extern int PdhCloseQuery(IntPtr query);
    }
  }
}
