/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using OpenHardwareMonitor.Hardware.Alerts;

namespace OpenHardwareMonitor.Hardware.Diagnostics {

  /// <summary>Tunes what <see cref="DiagnosticSnapshot.Capture"/> records.</summary>
  public sealed class DiagnosticCaptureOptions {

    public const int DefaultMaxRecentSamples = 120;

    /// <summary>Shown as the exporting application. Defaults to the entry assembly.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>Whether to embed the full text report from IComputer.GetReport().</summary>
    public bool IncludeReport { get; set; } = true;

    /// <summary>
    /// How many of the most recent history samples per sensor to keep as raw
    /// time/value pairs (JSON only). Statistics always cover the whole history.
    /// </summary>
    public int MaxRecentSamples { get; set; } = DefaultMaxRecentSamples;

    /// <summary>
    /// The alert log to include, newest first, as returned by
    /// <see cref="AlertEngine.GetRecentAlerts"/>. Null leaves the section out.
    /// </summary>
    public IReadOnlyList<AlertRecord>? RecentAlerts { get; set; }

    // Test seams: fixed clock and environment instead of the live machine.
    internal DateTimeOffset? Now { get; set; }
    internal DiagnosticSystemInfo? SystemInfo { get; set; }
    internal DiagnosticAccessInfo? AccessInfo { get; set; }
    internal DiagnosticApplicationInfo? ApplicationInfo { get; set; }
  }

  /// <summary>
  /// A self-contained copy of everything the sensor tree knew at one moment,
  /// plus the findings of <see cref="DiagnosticAnalyzer"/>.
  ///
  /// Capturing reads the live tree and must happen on the thread that owns it
  /// (the UI thread in the desktop application). Everything after that -
  /// analysis, JSON and Markdown - only touches this copy and may run anywhere.
  /// </summary>
  public sealed class DiagnosticSnapshot {

    /// <summary>Bumped on any breaking change to the JSON shape.</summary>
    public const int SchemaVersion = 1;

    private const int MaxHardwareDepth = 8;

    internal DiagnosticSnapshot() { }

    /// <summary>Local export time, including the UTC offset.</summary>
    public DateTimeOffset ExportTime { get; internal set; }

    public DiagnosticApplicationInfo Application { get; internal set; } =
      new DiagnosticApplicationInfo();

    public DiagnosticSystemInfo System { get; internal set; } =
      new DiagnosticSystemInfo();

    public DiagnosticAccessInfo Access { get; internal set; } =
      new DiagnosticAccessInfo();

    /// <summary>Top-level hardware; sub-hardware is nested inside.</summary>
    public IReadOnlyList<DiagnosticHardware> Hardware { get; internal set; } =
      Array.Empty<DiagnosticHardware>();

    public IReadOnlyList<DiagnosticFinding> Findings { get; internal set; } =
      Array.Empty<DiagnosticFinding>();

    /// <summary>The full text report, or null when not requested.</summary>
    public string? Report { get; internal set; }

    /// <summary>Alerts raised while the application ran, newest first, or null when not provided.</summary>
    public IReadOnlyList<AlertRecord>? RecentAlerts { get; internal set; }

    /// <summary>Problems reading individual nodes; the rest was still captured.</summary>
    public IReadOnlyList<string> CaptureErrors { get; internal set; } =
      Array.Empty<string>();

    /// <summary>Every hardware node, depth first.</summary>
    public IEnumerable<DiagnosticHardware> AllHardware() {
      foreach (DiagnosticHardware hardware in Hardware)
        foreach (DiagnosticHardware node in hardware.SelfAndDescendants())
          yield return node;
    }

    public IEnumerable<DiagnosticSensor> AllSensors() {
      foreach (DiagnosticHardware hardware in AllHardware())
        foreach (DiagnosticSensor sensor in hardware.Sensors)
          yield return sensor;
    }

    public int CountFindings(DiagnosticSeverity severity) {
      int count = 0;
      foreach (DiagnosticFinding finding in Findings)
        if (finding.Severity == severity)
          count++;
      return count;
    }

    /// <summary>
    /// Copies the sensor tree and environment and runs the analyzer. Call on
    /// the thread that updates the sensors. Never throws for a misbehaving
    /// hardware node; such failures are listed in <see cref="CaptureErrors"/>.
    /// </summary>
    public static DiagnosticSnapshot Capture(IComputer computer,
      DiagnosticCaptureOptions? options = null) {
      if (computer == null)
        throw new ArgumentNullException(nameof(computer));
      options ??= new DiagnosticCaptureOptions();

      DiagnosticSnapshot snapshot = new DiagnosticSnapshot();
      List<string> errors = new List<string>();

      snapshot.ExportTime = options.Now ?? DateTimeOffset.Now;
      snapshot.Application = options.ApplicationInfo ??
        DiagnosticApplicationInfo.Current(options.ApplicationName);
      snapshot.Application.Runtime = snapshot.Application.ProcessStart.HasValue
        ? snapshot.ExportTime - snapshot.Application.ProcessStart.Value
        : (TimeSpan?)null;
      snapshot.System = options.SystemInfo ?? DiagnosticSystemInfo.Current();
      snapshot.Access = options.AccessInfo ?? DiagnosticAccessInfo.Current();

      List<DiagnosticHardware> hardwareList = new List<DiagnosticHardware>();
      IHardware[] roots;
      try {
        roots = computer.Hardware ?? Array.Empty<IHardware>();
      } catch (Exception ex) {
        errors.Add("Hardware list: " + Describe(ex));
        roots = Array.Empty<IHardware>();
      }
      foreach (IHardware hardware in roots) {
        DiagnosticHardware? node = CaptureHardware(hardware, null, 0,
          Math.Max(0, options.MaxRecentSamples), errors);
        if (node != null)
          hardwareList.Add(node);
      }
      snapshot.Hardware = hardwareList;

      if (options.IncludeReport) {
        try {
          snapshot.Report = computer.GetReport();
        } catch (Exception ex) {
          errors.Add("Text report: " + Describe(ex));
          snapshot.Report = null;
        }
      }

      snapshot.CaptureErrors = errors;
      snapshot.RecentAlerts = options.RecentAlerts == null
        ? null : new List<AlertRecord>(options.RecentAlerts);
      snapshot.Findings = DiagnosticAnalyzer.Analyze(snapshot);
      return snapshot;
    }

    private static DiagnosticHardware? CaptureHardware(IHardware hardware,
      DiagnosticHardware? parent, int depth, int maxSamples,
      List<string> errors) {
      if (hardware == null)
        return null;

      DiagnosticHardware node;
      try {
        node = new DiagnosticHardware {
          Name = hardware.Name ?? "",
          Type = hardware.HardwareType,
          Identifier = hardware.Identifier?.ToString() ?? "",
          Parent = parent
        };
      } catch (Exception ex) {
        errors.Add("Hardware node: " + Describe(ex));
        return null;
      }

      List<DiagnosticSensor> sensors = new List<DiagnosticSensor>();
      ISensor[] source;
      try {
        source = hardware.Sensors ?? Array.Empty<ISensor>();
      } catch (Exception ex) {
        errors.Add(node.Identifier + ": sensor list: " + Describe(ex));
        source = Array.Empty<ISensor>();
      }
      foreach (ISensor sensor in source) {
        if (sensor == null)
          continue;
        try {
          sensors.Add(CaptureSensor(sensor, node, maxSamples));
        } catch (Exception ex) {
          string id;
          try { id = sensor.Identifier.ToString(); } catch (Exception) { id = "?"; }
          errors.Add(id + ": " + Describe(ex));
        }
      }
      sensors.Sort(CompareSensors);
      node.Sensors = sensors;

      List<DiagnosticHardware> children = new List<DiagnosticHardware>();
      if (depth < MaxHardwareDepth) {
        IHardware[] subHardware;
        try {
          subHardware = hardware.SubHardware ?? Array.Empty<IHardware>();
        } catch (Exception ex) {
          errors.Add(node.Identifier + ": sub-hardware list: " + Describe(ex));
          subHardware = Array.Empty<IHardware>();
        }
        foreach (IHardware sub in subHardware) {
          DiagnosticHardware? child = CaptureHardware(sub, node, depth + 1,
            maxSamples, errors);
          if (child != null)
            children.Add(child);
        }
      }
      node.SubHardware = children;
      return node;
    }

    private static int CompareSensors(DiagnosticSensor a, DiagnosticSensor b) {
      int c = a.Type.CompareTo(b.Type);
      if (c != 0)
        return c;
      c = a.Index.CompareTo(b.Index);
      return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    }

    private static DiagnosticSensor CaptureSensor(ISensor sensor,
      DiagnosticHardware hardware, int maxSamples) {
      DiagnosticSensor result = new DiagnosticSensor {
        Hardware = hardware,
        Name = sensor.Name ?? "",
        Type = sensor.SensorType,
        Identifier = sensor.Identifier?.ToString() ?? "",
        Index = sensor.Index,
        IsDefaultHidden = sensor.IsDefaultHidden,
        Value = Finite(sensor.Value),
        Min = Finite(sensor.Min),
        Max = Finite(sensor.Max),
        History = CaptureHistory(sensor.Values, maxSamples)
      };

      List<DiagnosticParameter> parameters = new List<DiagnosticParameter>();
      IEnumerable<IParameter>? sourceParameters = sensor.Parameters;
      if (sourceParameters != null) {
        foreach (IParameter parameter in sourceParameters) {
          if (parameter == null)
            continue;
          parameters.Add(new DiagnosticParameter {
            Name = parameter.Name ?? "",
            Description = parameter.Description ?? "",
            Value = parameter.Value,
            DefaultValue = parameter.DefaultValue,
            IsDefault = parameter.IsDefault
          });
        }
      }
      result.Parameters = parameters;

      IControl? control = sensor.Control;
      if (control != null) {
        result.Control = new DiagnosticControl {
          Mode = control.ControlMode,
          SoftwareValue = control.SoftwareValue,
          MinSoftwareValue = control.MinSoftwareValue,
          MaxSoftwareValue = control.MaxSoftwareValue
        };
      }
      return result;
    }

    private static float? Finite(float? value) {
      return value.HasValue && float.IsFinite(value.Value) ? value : null;
    }

    /// <summary>
    /// Statistics over the whole history, skipping the NaN markers the sensor
    /// inserts at gaps (for example between application sessions), plus the
    /// last few samples verbatim.
    /// </summary>
    internal static DiagnosticHistory? CaptureHistory(
      IEnumerable<SensorValue>? values, int maxSamples) {
      if (values == null)
        return null;

      int count = 0;
      double sum = 0;
      float min = float.PositiveInfinity;
      float max = float.NegativeInfinity;
      DateTime start = default;
      DateTime end = default;
      DateTime maxTime = default;
      SensorValue[] recent = new SensorValue[maxSamples];
      int recentNext = 0;
      int recentCount = 0;

      foreach (SensorValue sample in values) {
        if (!float.IsFinite(sample.Value))
          continue;
        DateTime time = ToUtc(sample.Time);
        if (count == 0)
          start = time;
        end = time;
        count++;
        sum += sample.Value;
        if (sample.Value < min)
          min = sample.Value;
        if (sample.Value > max) {
          max = sample.Value;
          maxTime = time;
        }
        if (maxSamples > 0) {
          recent[recentNext] = new SensorValue(sample.Value, time);
          recentNext = (recentNext + 1) % maxSamples;
          if (recentCount < maxSamples)
            recentCount++;
        }
      }

      if (count == 0)
        return null;

      SensorValue[] ordered = new SensorValue[recentCount];
      int first = recentCount < maxSamples ? 0 : recentNext;
      for (int i = 0; i < recentCount; i++)
        ordered[i] = recent[(first + i) % Math.Max(1, maxSamples)];

      return new DiagnosticHistory {
        SampleCount = count,
        WindowStartUtc = start,
        WindowEndUtc = end,
        Average = sum / count,
        Minimum = min,
        Maximum = max,
        MaximumTimeUtc = maxTime,
        RecentSamples = ordered
      };
    }

    private static DateTime ToUtc(DateTime time) {
      switch (time.Kind) {
        case DateTimeKind.Utc: return time;
        case DateTimeKind.Local: return time.ToUniversalTime();
        default: return DateTime.SpecifyKind(time, DateTimeKind.Utc);
      }
    }

    internal static string Describe(Exception ex) {
      return ex.GetType().Name + ": " + ex.Message;
    }
  }

  public sealed class DiagnosticHardware {

    internal DiagnosticHardware() { }

    public string Name { get; internal set; } = "";
    public HardwareType Type { get; internal set; }
    public string Identifier { get; internal set; } = "";

    /// <summary>The containing node, or null for top-level hardware.</summary>
    public DiagnosticHardware? Parent { get; internal set; }

    public IReadOnlyList<DiagnosticSensor> Sensors { get; internal set; } =
      Array.Empty<DiagnosticSensor>();

    public IReadOnlyList<DiagnosticHardware> SubHardware { get; internal set; } =
      Array.Empty<DiagnosticHardware>();

    /// <summary>"Mainboard name / Super I/O name" style display path.</summary>
    public string Path {
      get { return Parent == null ? Name : Parent.Path + " / " + Name; }
    }

    public IEnumerable<DiagnosticHardware> SelfAndDescendants() {
      yield return this;
      foreach (DiagnosticHardware child in SubHardware)
        foreach (DiagnosticHardware node in child.SelfAndDescendants())
          yield return node;
    }
  }

  public sealed class DiagnosticSensor {

    internal DiagnosticSensor() { }

    public DiagnosticHardware Hardware { get; internal set; } = null!;
    public string Name { get; internal set; } = "";
    public SensorType Type { get; internal set; }
    public string Unit { get { return DiagnosticFormat.Unit(Type); } }
    public string Identifier { get; internal set; } = "";
    public int Index { get; internal set; }
    public bool IsDefaultHidden { get; internal set; }

    /// <summary>Current reading; null when the sensor reports nothing.</summary>
    public float? Value { get; internal set; }

    /// <summary>Lowest reading since the application started or min/max was reset.</summary>
    public float? Min { get; internal set; }

    /// <summary>Highest reading since the application started or min/max was reset.</summary>
    public float? Max { get; internal set; }

    /// <summary>Statistics over the recorded history, or null when there is none.</summary>
    public DiagnosticHistory? History { get; internal set; }

    public IReadOnlyList<DiagnosticParameter> Parameters { get; internal set; } =
      Array.Empty<DiagnosticParameter>();

    /// <summary>Control state for controllable sensors (fan outputs), else null.</summary>
    public DiagnosticControl? Control { get; internal set; }

    /// <summary>A parameter whose name starts with the given prefix, ignoring case.</summary>
    public DiagnosticParameter? FindParameter(string prefix) {
      foreach (DiagnosticParameter parameter in Parameters)
        if (parameter.Name.TrimStart().StartsWith(prefix,
          StringComparison.OrdinalIgnoreCase))
          return parameter;
      return null;
    }
  }

  /// <summary>
  /// Statistics over ISensor.Values. The sensor records one averaged sample
  /// per four updates and keeps up to 24 hours, so the window may predate the
  /// current application session.
  /// </summary>
  public sealed class DiagnosticHistory {

    internal DiagnosticHistory() { }

    public int SampleCount { get; internal set; }
    public DateTime WindowStartUtc { get; internal set; }
    public DateTime WindowEndUtc { get; internal set; }
    public double Average { get; internal set; }
    public float Minimum { get; internal set; }
    public float Maximum { get; internal set; }
    public DateTime MaximumTimeUtc { get; internal set; }

    /// <summary>The newest samples, oldest first, in UTC.</summary>
    public IReadOnlyList<SensorValue> RecentSamples { get; internal set; } =
      Array.Empty<SensorValue>();

    public TimeSpan Window { get { return WindowEndUtc - WindowStartUtc; } }
  }

  public sealed class DiagnosticParameter {

    internal DiagnosticParameter() { }

    public string Name { get; internal set; } = "";
    public string Description { get; internal set; } = "";
    public float Value { get; internal set; }
    public float DefaultValue { get; internal set; }
    public bool IsDefault { get; internal set; }
  }

  public sealed class DiagnosticControl {

    internal DiagnosticControl() { }

    public ControlMode Mode { get; internal set; }
    public float SoftwareValue { get; internal set; }
    public float MinSoftwareValue { get; internal set; }
    public float MaxSoftwareValue { get; internal set; }
  }
}
