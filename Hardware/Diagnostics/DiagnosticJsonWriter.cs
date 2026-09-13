/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace OpenHardwareMonitor.Hardware.Diagnostics {

  /// <summary>
  /// The complete, machine-readable form of a snapshot. Property names are
  /// camelCase and stable; numbers are plain JSON numbers in canonical units;
  /// times are ISO 8601. Breaking changes bump
  /// <see cref="DiagnosticSnapshot.SchemaVersion"/>.
  /// </summary>
  public static class DiagnosticJsonWriter {

    public static byte[] ToUtf8Bytes(DiagnosticSnapshot snapshot) {
      using (MemoryStream stream = new MemoryStream()) {
        Write(stream, snapshot);
        return stream.ToArray();
      }
    }

    public static string ToJson(DiagnosticSnapshot snapshot) {
      return Encoding.UTF8.GetString(ToUtf8Bytes(snapshot));
    }

    public static void Write(Stream stream, DiagnosticSnapshot snapshot) {
      if (stream == null)
        throw new ArgumentNullException(nameof(stream));
      if (snapshot == null)
        throw new ArgumentNullException(nameof(snapshot));

      using (Utf8JsonWriter w = new Utf8JsonWriter(stream,
        new JsonWriterOptions { Indented = true })) {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", DiagnosticSnapshot.SchemaVersion);
        w.WriteString("kind", "openHardwareMonitor.diagnostics");

        w.WriteStartObject("exportTime");
        w.WriteString("local", DiagnosticFormat.IsoLocal(snapshot.ExportTime));
        w.WriteString("utc", DiagnosticFormat.IsoUtc(snapshot.ExportTime));
        w.WriteEndObject();

        WriteApplication(w, snapshot);
        WriteSystem(w, snapshot.System);
        WriteAccess(w, snapshot.Access);
        WriteNotes(w);
        WriteSummary(w, snapshot);

        w.WriteStartArray("findings");
        foreach (DiagnosticFinding finding in snapshot.Findings)
          WriteFinding(w, finding);
        w.WriteEndArray();

        w.WriteStartArray("hardware");
        foreach (DiagnosticHardware hardware in snapshot.Hardware)
          WriteHardware(w, hardware);
        w.WriteEndArray();

        w.WriteStartArray("captureErrors");
        foreach (string error in snapshot.CaptureErrors)
          w.WriteStringValue(error);
        w.WriteEndArray();

        WriteString(w, "report", snapshot.Report);
        w.WriteEndObject();
        w.Flush();
      }
    }

    private static void WriteApplication(Utf8JsonWriter w,
      DiagnosticSnapshot snapshot) {
      DiagnosticApplicationInfo app = snapshot.Application;
      w.WriteStartObject("application");
      w.WriteString("name", app.Name);
      w.WriteString("version", app.Version);
      if (app.ProcessStart.HasValue) {
        w.WriteString("processStartLocal", DiagnosticFormat.IsoLocal(app.ProcessStart.Value));
        w.WriteString("processStartUtc", DiagnosticFormat.IsoUtc(app.ProcessStart.Value));
      } else {
        w.WriteNull("processStartLocal");
        w.WriteNull("processStartUtc");
      }
      WriteSeconds(w, "runtimeSeconds", app.Runtime);
      w.WriteEndObject();
    }

    private static void WriteSystem(Utf8JsonWriter w, DiagnosticSystemInfo system) {
      w.WriteStartObject("system");
      w.WriteString("osProductName", system.OsProductName);
      WriteString(w, "osDisplayVersion", system.OsDisplayVersion);
      WriteString(w, "osBuild", system.OsBuild);
      w.WriteString("osDescription", system.OsDescription);
      w.WriteString("osArchitecture", system.OsArchitecture);
      w.WriteString("processArchitecture", system.ProcessArchitecture);
      WriteSeconds(w, "uptimeSeconds", system.Uptime);
      w.WriteNumber("logicalProcessors", system.LogicalProcessors);
      if (system.IsElevated.HasValue)
        w.WriteBoolean("elevated", system.IsElevated.Value);
      else
        w.WriteNull("elevated");
      w.WriteString("dotnetRuntime", system.DotNetRuntime);
      w.WriteEndObject();
    }

    private static void WriteAccess(Utf8JsonWriter w, DiagnosticAccessInfo access) {
      w.WriteStartObject("access");
      w.WriteString("tier", access.Tier.ToString());
      w.WriteString("backend", access.BackendName);
      w.WriteBoolean("supportsModelSpecificRegisters",
        access.SupportsModelSpecificRegisters);
      w.WriteBoolean("supportsIoPort", access.SupportsIoPort);
      w.WriteBoolean("supportsPciConfig", access.SupportsPciConfig);
      WriteString(w, "unavailableReason", access.UnavailableReason);
      w.WriteEndObject();
    }

    private static void WriteNotes(Utf8JsonWriter w) {
      w.WriteStartObject("notes");
      w.WriteString("minMax",
        "minSinceAppStart and maxSinceAppStart cover the time since the " +
        "application started or the user last reset min/max; see " +
        "application.runtimeSeconds.");
      w.WriteString("history",
        "Statistics over the sensor's recorded history: one averaged sample per " +
        "four updates, kept for up to 24 hours, possibly spanning earlier sessions.");
      w.WriteString("recentSamples",
        "[unixTimeMilliseconds, value] pairs, oldest first; only the newest " +
        "samples are included.");
      w.WriteString("units", "Canonical units per sensor type; see each sensor's unit.");
      w.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter w, DiagnosticSnapshot snapshot) {
      int hardwareCount = 0;
      foreach (DiagnosticHardware unused in snapshot.AllHardware())
        hardwareCount++;
      int sensors = 0;
      int withoutValue = 0;
      foreach (DiagnosticSensor sensor in snapshot.AllSensors()) {
        sensors++;
        if (!sensor.Value.HasValue)
          withoutValue++;
      }

      w.WriteStartObject("summary");
      w.WriteNumber("hardwareCount", hardwareCount);
      w.WriteNumber("sensorCount", sensors);
      w.WriteNumber("sensorsWithoutValue", withoutValue);
      w.WriteStartObject("findings");
      w.WriteNumber("critical", snapshot.CountFindings(DiagnosticSeverity.Critical));
      w.WriteNumber("warning", snapshot.CountFindings(DiagnosticSeverity.Warning));
      w.WriteNumber("info", snapshot.CountFindings(DiagnosticSeverity.Info));
      w.WriteEndObject();
      w.WriteEndObject();
    }

    internal static string SeverityName(DiagnosticSeverity severity) {
      switch (severity) {
        case DiagnosticSeverity.Critical: return "critical";
        case DiagnosticSeverity.Warning: return "warning";
        default: return "info";
      }
    }

    private static void WriteFinding(Utf8JsonWriter w, DiagnosticFinding finding) {
      w.WriteStartObject();
      w.WriteString("severity", SeverityName(finding.Severity));
      w.WriteString("ruleId", finding.RuleId);
      w.WriteString("title", finding.Title);
      w.WriteString("explanation", finding.Explanation);
      WriteString(w, "hardwareName", finding.HardwareName);
      WriteString(w, "hardwareId", finding.HardwareIdentifier);
      w.WriteStartArray("evidence");
      foreach (DiagnosticEvidence evidence in finding.Evidence) {
        w.WriteStartObject();
        WriteString(w, "sensorId", evidence.SensorIdentifier);
        WriteString(w, "sensorName", evidence.SensorName);
        WriteString(w, "hardwareName", evidence.HardwareName);
        WriteString(w, "sensorType", evidence.Type?.ToString());
        WriteString(w, "unit", evidence.Type.HasValue ? evidence.Unit : null);
        WriteNumber(w, "value", evidence.Value);
        WriteNumber(w, "minSinceAppStart", evidence.Min);
        WriteNumber(w, "maxSinceAppStart", evidence.Max);
        WriteNumber(w, "threshold", evidence.Threshold);
        WriteString(w, "note", evidence.Note);
        w.WriteEndObject();
      }
      w.WriteEndArray();
      w.WriteEndObject();
    }

    private static void WriteHardware(Utf8JsonWriter w, DiagnosticHardware hardware) {
      w.WriteStartObject();
      w.WriteString("id", hardware.Identifier);
      w.WriteString("name", hardware.Name);
      w.WriteString("type", hardware.Type.ToString());
      WriteString(w, "parentId", hardware.Parent?.Identifier);

      w.WriteStartArray("sensors");
      foreach (DiagnosticSensor sensor in hardware.Sensors)
        WriteSensor(w, sensor);
      w.WriteEndArray();

      w.WriteStartArray("subHardware");
      foreach (DiagnosticHardware sub in hardware.SubHardware)
        WriteHardware(w, sub);
      w.WriteEndArray();
      w.WriteEndObject();
    }

    private static void WriteSensor(Utf8JsonWriter w, DiagnosticSensor sensor) {
      w.WriteStartObject();
      w.WriteString("id", sensor.Identifier);
      w.WriteString("name", sensor.Name);
      w.WriteString("type", sensor.Type.ToString());
      w.WriteString("unit", sensor.Unit);
      w.WriteNumber("index", sensor.Index);
      w.WriteBoolean("hiddenByDefault", sensor.IsDefaultHidden);
      WriteNumber(w, "value", sensor.Value);
      WriteNumber(w, "minSinceAppStart", sensor.Min);
      WriteNumber(w, "maxSinceAppStart", sensor.Max);

      DiagnosticHistory? history = sensor.History;
      if (history == null) {
        w.WriteNull("history");
      } else {
        w.WriteStartObject("history");
        w.WriteNumber("sampleCount", history.SampleCount);
        w.WriteString("windowStartUtc", DiagnosticFormat.IsoUtc(history.WindowStartUtc));
        w.WriteString("windowEndUtc", DiagnosticFormat.IsoUtc(history.WindowEndUtc));
        WriteNumber(w, "average", history.Average);
        WriteNumber(w, "min", history.Minimum);
        WriteNumber(w, "max", history.Maximum);
        w.WriteString("maxTimeUtc", DiagnosticFormat.IsoUtc(history.MaximumTimeUtc));
        w.WritePropertyName("recentSamples");
        // One line for the whole array; indenting every pair would make the
        // file tens of thousands of lines long.
        w.WriteRawValue(RecentSamplesJson(history), skipInputValidation: false);
        w.WriteEndObject();
      }

      w.WriteStartArray("parameters");
      foreach (DiagnosticParameter parameter in sensor.Parameters) {
        w.WriteStartObject();
        w.WriteString("name", parameter.Name);
        WriteNumber(w, "value", parameter.Value);
        WriteNumber(w, "defaultValue", parameter.DefaultValue);
        w.WriteBoolean("isDefault", parameter.IsDefault);
        w.WriteEndObject();
      }
      w.WriteEndArray();

      DiagnosticControl? control = sensor.Control;
      if (control == null) {
        w.WriteNull("control");
      } else {
        w.WriteStartObject("control");
        w.WriteString("mode", control.Mode.ToString());
        WriteNumber(w, "softwareValue", control.SoftwareValue);
        WriteNumber(w, "minSoftwareValue", control.MinSoftwareValue);
        WriteNumber(w, "maxSoftwareValue", control.MaxSoftwareValue);
        w.WriteEndObject();
      }
      w.WriteEndObject();
    }

    private static string RecentSamplesJson(DiagnosticHistory history) {
      StringBuilder b = new StringBuilder("[");
      bool first = true;
      foreach (SensorValue sample in history.RecentSamples) {
        if (!float.IsFinite(sample.Value))
          continue;
        if (!first)
          b.Append(',');
        first = false;
        long ms = new DateTimeOffset(DateTime.SpecifyKind(sample.Time,
          DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        b.Append('[')
          .Append(ms.ToString(CultureInfo.InvariantCulture))
          .Append(',')
          .Append(Math.Round((double)sample.Value, 3).ToString("R", CultureInfo.InvariantCulture))
          .Append(']');
      }
      return b.Append(']').ToString();
    }

    private static void WriteSeconds(Utf8JsonWriter w, string name, TimeSpan? span) {
      if (span.HasValue)
        w.WriteNumber(name, Math.Round(span.Value.TotalSeconds, 1));
      else
        w.WriteNull(name);
    }

    private static void WriteNumber(Utf8JsonWriter w, string name, double? value) {
      // JSON has no NaN or infinity.
      if (value.HasValue && double.IsFinite(value.Value))
        w.WriteNumber(name, Math.Round(value.Value, 3));
      else
        w.WriteNull(name);
    }

    private static void WriteString(Utf8JsonWriter w, string name, string? value) {
      if (value != null)
        w.WriteString(name, value);
      else
        w.WriteNull(name);
    }
  }
}
