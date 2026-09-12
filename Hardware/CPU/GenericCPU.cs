/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2010-2011 Michael Möller <mmoeller@openhardwaremonitor.org>
	
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OpenHardwareMonitor.Hardware.CPU {
  internal class GenericCPU : Hardware {

    protected readonly CPUID[][] cpuid;
   
    protected readonly uint family;
    protected readonly uint model;
    protected readonly uint stepping;

    protected readonly int processorIndex;
    protected readonly int coreCount;

    private readonly bool hasModelSpecificRegisters;

    private readonly bool hasTimeStampCounter;
    private readonly bool isInvariantTimeStampCounter;

    private double timeStampCounterFrequency;
    private ProcessorFrequency processorFrequency;
    

    private readonly Vendor vendor;

    private readonly CPULoad cpuLoad;
    private readonly Sensor totalLoad;
    private readonly Sensor[] coreLoads;

    private readonly string[] coreLabels;

    protected string CoreString(int i) {
      if (coreCount == 1)
        return "CPU Core";
      if (coreLabels != null && i >= 0 && i < coreLabels.Length)
        return coreLabels[i];
      return "CPU Core #" + (i + 1);
    }

    /// <summary>
    /// Builds per-core sensor labels. On a heterogeneous processor the cores
    /// are named by class and numbered within that class, so an i7-14700KF
    /// reads "P-Core #1".."P-Core #8" and "E-Core #1".."E-Core #12" rather
    /// than one undifferentiated run of twenty. On a homogeneous processor
    /// the traditional naming is kept.
    /// </summary>
    private static string[] BuildCoreLabels(CPUID[][] cpuid) {
      string[] labels = new string[cpuid.Length];

      bool hasPerformance = false;
      bool hasEfficiency = false;
      foreach (CPUID[] core in cpuid) {
        if (core.Length == 0)
          continue;
        if (core[0].CoreType == CoreType.Performance)
          hasPerformance = true;
        else if (core[0].CoreType == CoreType.Efficiency)
          hasEfficiency = true;
      }

      bool hybrid = hasPerformance && hasEfficiency;

      int performanceIndex = 0;
      int efficiencyIndex = 0;
      for (int i = 0; i < cpuid.Length; i++) {
        CoreType type = cpuid[i].Length > 0
          ? cpuid[i][0].CoreType : CoreType.Unknown;

        if (hybrid && type == CoreType.Performance)
          labels[i] = "P-Core #" + (++performanceIndex);
        else if (hybrid && type == CoreType.Efficiency)
          labels[i] = "E-Core #" + (++efficiencyIndex);
        else
          labels[i] = "CPU Core #" + (i + 1);
      }

      return labels;
    }

    public GenericCPU(int processorIndex, CPUID[][] cpuid, ISettings settings)
      : base(cpuid[0][0].Name, CreateIdentifier(cpuid[0][0].Vendor, 
      processorIndex), settings)
    {
      this.cpuid = cpuid;

      this.vendor = cpuid[0][0].Vendor;

      this.family = cpuid[0][0].Family;
      this.model = cpuid[0][0].Model;
      this.stepping = cpuid[0][0].Stepping;

      this.processorIndex = processorIndex;
      this.coreCount = cpuid.Length;
      this.coreLabels = BuildCoreLabels(cpuid);
  
      // check if processor has MSRs
      if (cpuid[0][0].Data.GetLength(0) > 1
        && (cpuid[0][0].Data[1, 3] & 0x20) != 0)
        hasModelSpecificRegisters = true;
      else
        hasModelSpecificRegisters = false;

      // check if processor has a TSC
      if (cpuid[0][0].Data.GetLength(0) > 1
        && (cpuid[0][0].Data[1, 3] & 0x10) != 0)
        hasTimeStampCounter = true;
      else
        hasTimeStampCounter = false;

      // check if processor supports an invariant TSC 
      if (cpuid[0][0].ExtData.GetLength(0) > 7
        && (cpuid[0][0].ExtData[7, 3] & 0x100) != 0)
        isInvariantTimeStampCounter = true;
      else
        isInvariantTimeStampCounter = false;

      if (coreCount > 1)
        totalLoad = new Sensor("CPU Total", 0, SensorType.Load, this, settings);
      else
        totalLoad = null;
      coreLoads = new Sensor[coreCount];
      for (int i = 0; i < coreLoads.Length; i++)
        coreLoads[i] = new Sensor(CoreString(i), i + 1,
          SensorType.Load, this, settings);
      cpuLoad = new CPULoad(cpuid);
      if (cpuLoad.IsAvailable) {
        foreach (Sensor sensor in coreLoads)
          ActivateSensor(sensor);
        if (totalLoad != null)
          ActivateSensor(totalLoad);
      }

      // The processor reports its own nominal TSC frequency, so there is
      // nothing to measure here. See CpuInstructions for why the previous
      // RDTSC sampling loop (up to 125 ms of busy-waiting at startup, then
      // continuous re-estimation) was removed.
      if (!hasTimeStampCounter ||
        !CpuInstructions.TryGetTimeStampCounterFrequency(
          out timeStampCounterFrequency)) {
        timeStampCounterFrequency = 0;
      }
    }

    private static Identifier CreateIdentifier(Vendor vendor,
      int processorIndex) 
    {
      string s;
      switch (vendor) {
        case Vendor.AMD: s = "amdcpu"; break;
        case Vendor.Intel: s = "intelcpu"; break;
        default: s = "genericcpu"; break;
      }
      return new Identifier(s,
        processorIndex.ToString(CultureInfo.InvariantCulture));
    }


    private static void AppendMSRData(StringBuilder r, uint msr, 
      GroupAffinity affinity) 
    {
      uint eax, edx;
      if (Ring0.RdmsrTx(msr, out eax, out edx, affinity)) {
        r.Append(" ");
        r.Append((msr).ToString("X8", CultureInfo.InvariantCulture));
        r.Append("  ");
        r.Append((edx).ToString("X8", CultureInfo.InvariantCulture));
        r.Append("  ");
        r.Append((eax).ToString("X8", CultureInfo.InvariantCulture));
        r.AppendLine();
      }
    }

    protected virtual uint[] GetMSRs() {
      return null;
    }

    /// <summary>
    /// Fills per-core clock sensors from the operating system's power
    /// management data, for use when no low-level backend is available to
    /// read IA32_PERF_STATUS. Less precise than the MSR path, but it needs no
    /// driver, which is the difference between reporting clocks and reporting
    /// nothing.
    /// </summary>
    protected bool TryUpdateClocksFromOperatingSystem(Sensor[] coreClocks) {
      if (coreClocks == null)
        return false;

      processorFrequency ??= new ProcessorFrequency(cpuid);
      if (!processorFrequency.IsAvailable)
        return false;

      bool any = false;
      for (int i = 0; i < coreClocks.Length && i < cpuid.Length; i++) {
        float? megahertz = processorFrequency.GetCoreFrequency(i);
        coreClocks[i].Value = megahertz;
        if (megahertz.HasValue)
          any = true;
      }
      return any;
    }

    public override void Close() {
      processorFrequency?.Dispose();
      processorFrequency = null;
      base.Close();
    }

    public override string GetReport() {
      StringBuilder r = new StringBuilder();

      switch (vendor) {
        case Vendor.AMD: r.AppendLine("AMD CPU"); break;
        case Vendor.Intel: r.AppendLine("Intel CPU"); break;
        default: r.AppendLine("Generic CPU"); break;
      }

      r.AppendLine();
      r.AppendFormat("Name: {0}{1}", name, Environment.NewLine);
      r.AppendFormat("Number of Cores: {0}{1}", coreCount,
        Environment.NewLine);
      r.AppendFormat("Threads per Core: {0}{1}", cpuid[0].Length,
        Environment.NewLine);
      r.AppendLine(string.Format(CultureInfo.InvariantCulture,
        "Timer Frequency: {0} MHz", Stopwatch.Frequency * 1e-6));
      r.AppendLine("Time Stamp Counter: " + (hasTimeStampCounter ? (
        isInvariantTimeStampCounter ? "Invariant" : "Not Invariant") : "None"));
      r.AppendLine(string.Format(CultureInfo.InvariantCulture,
        "Time Stamp Counter Frequency: {0} MHz",
        Math.Round(timeStampCounterFrequency * 100) * 0.01));
      r.AppendLine();

      uint[] msrArray = GetMSRs();
      if (msrArray != null && msrArray.Length > 0) {
        for (int i = 0; i < cpuid.Length; i++) {
          r.AppendLine("MSR Core #" + (i + 1));
          r.AppendLine();
          r.AppendLine(" MSR       EDX       EAX");
          foreach (uint msr in msrArray)
            AppendMSRData(r, msr, cpuid[i][0].Affinity);
          r.AppendLine();
        }
      }

      return r.ToString();
    }

    public override HardwareType HardwareType {
      get { return HardwareType.CPU; }
    }

    public bool HasModelSpecificRegisters {
      get { return hasModelSpecificRegisters; }
    }

    public bool HasTimeStampCounter {
      get { return hasTimeStampCounter; }
    }

    public double TimeStampCounterFrequency {
      get { return timeStampCounterFrequency; }
    }

    public override void Update() {
      // The TSC frequency is nominal and constant on any processor with an
      // invariant TSC, and is read once in the constructor. It used to be
      // re-estimated on every tick by sampling RDTSC against Stopwatch, which
      // pinned thread affinity and measured a constant with added noise.

      if (cpuLoad.IsAvailable) {
        cpuLoad.Update();
        for (int i = 0; i < coreLoads.Length; i++)
          coreLoads[i].Value = cpuLoad.GetCoreLoad(i);
        if (totalLoad != null)
          totalLoad.Value = cpuLoad.GetTotalLoad();
      }
    }
  }
}
