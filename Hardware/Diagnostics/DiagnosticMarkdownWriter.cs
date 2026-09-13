/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenHardwareMonitor.Hardware.Alerts;

namespace OpenHardwareMonitor.Hardware.Diagnostics {

  /// <summary>
  /// A Markdown rendering meant to be pasted into an AI chat: an explanation
  /// of how to read it first, then the summary, the findings, one table per
  /// hardware node with history reduced to statistics, and the full text report
  /// as an appendix.
  /// </summary>
  public static class DiagnosticMarkdownWriter {

    /// <summary>Evidence entries shown per finding before "and N more".</summary>
    public const int MaxEvidenceShown = 8;

    public static string ToMarkdown(DiagnosticSnapshot snapshot) {
      if (snapshot == null)
        throw new ArgumentNullException(nameof(snapshot));

      StringBuilder md = new StringBuilder();
      WritePreamble(md, snapshot);
      WriteSummary(md, snapshot);
      WriteFindings(md, snapshot);
      WriteRecentAlerts(md, snapshot);
      WriteHardware(md, snapshot);
      WriteAppendix(md, snapshot);
      return md.ToString();
    }

    private static void Line(StringBuilder md, string text = "") {
      md.Append(text).Append('\n');
    }

    private static void WritePreamble(StringBuilder md, DiagnosticSnapshot snapshot) {
      string runtime = snapshot.Application.Runtime.HasValue
        ? DiagnosticFormat.Duration(snapshot.Application.Runtime.Value)
        : "an unknown time";
      string offset = snapshot.ExportTime.ToString("zzz", CultureInfo.InvariantCulture);

      Line(md, "# Open Hardware Monitor diagnostics snapshot");
      Line(md);
      Line(md, "> **For the AI assistant reading this:** the user exported this " +
        "report from Open Hardware Monitor, a Windows hardware monitoring tool, so " +
        "you can help diagnose a PC problem such as crashes, throttling, noise or " +
        "instability. It is a single point-in-time snapshot, not a live feed.");
      Line(md, ">");
      Line(md, "> How to read it:");
      Line(md, ">");
      Line(md, "> - **Units** are fixed per sensor type: temperature °C, voltage V, " +
        "fan RPM, clock MHz, power W, load/level/control %, data GB, small data MB, " +
        "throughput MB/s, flow L/h.");
      Line(md, "> - **Current** is the reading at export time. **Min** and **Max** " +
        "are the extremes *since the application started* (it had been running for " +
        runtime + ") or since the user last reset them; they are not lifetime values.");
      Line(md, "> - **Avg**, **Peak at** and **History** come from the recorded " +
        "history: one averaged sample about every four updates, kept for up to 24 " +
        "hours and possibly spanning earlier sessions. Times are local (UTC" + offset + ").");
      Line(md, "> - **Findings** come from simple built-in rules with fixed " +
        "thresholds. Treat them as leads to verify against the tables, not as a " +
        "diagnosis; an empty findings list does not prove the system is healthy.");
      Line(md, "> - `" + DiagnosticFormat.NoValue + "` means no value. Whole groups " +
        "of missing sensors are usually explained by the sensor access tier below.");
      Line(md, "> - The appendix holds the application's full technical report " +
        "(SMBIOS, SMART, driver details) for anything the tables do not cover.");
      Line(md, ">");
      Line(md, "> Ask the user what symptoms they see and when (idle, gaming, after " +
        "sleep), then correlate their answer with this data.");
      Line(md);
    }

    private static void WriteSummary(StringBuilder md, DiagnosticSnapshot snapshot) {
      DiagnosticApplicationInfo app = snapshot.Application;
      DiagnosticSystemInfo system = snapshot.System;
      DiagnosticAccessInfo access = snapshot.Access;

      Line(md, "## System summary");
      Line(md);
      Line(md, "| Item | Value |");
      Line(md, "|---|---|");

      Row(md, "Exported", DiagnosticFormat.LocalTimestamp(snapshot.ExportTime) +
        " (" + snapshot.ExportTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss",
          CultureInfo.InvariantCulture) + " UTC)");

      string application = app.Name + " " + app.Version;
      if (app.Runtime.HasValue && app.ProcessStart.HasValue)
        application += ", running for " + DiagnosticFormat.Duration(app.Runtime.Value) +
          " (started " + DiagnosticFormat.LocalTimestamp(app.ProcessStart.Value) + ")";
      Row(md, "Application", application);

      StringBuilder windows = new StringBuilder(system.OsProductName);
      if (!string.IsNullOrEmpty(system.OsDisplayVersion))
        windows.Append(' ').Append(system.OsDisplayVersion);
      if (!string.IsNullOrEmpty(system.OsBuild))
        windows.Append(", build ").Append(system.OsBuild);
      if (!string.IsNullOrEmpty(system.OsArchitecture))
        windows.Append(" (").Append(system.OsArchitecture).Append(')');
      Row(md, "Windows", windows.ToString());

      Row(md, "System uptime", DiagnosticFormat.Duration(system.Uptime) +
        " (includes sleep; Fast Startup shutdowns do not reset it)");
      Row(md, "Logical processors",
        system.LogicalProcessors.ToString(CultureInfo.InvariantCulture));
      Row(md, "Running as administrator", system.IsElevated.HasValue
        ? (system.IsElevated.Value ? "yes" : "no") : "unknown");
      Row(md, ".NET runtime", system.DotNetRuntime +
        (string.IsNullOrEmpty(system.ProcessArchitecture)
          ? "" : " (" + system.ProcessArchitecture + " process)"));

      Row(md, "Sensor access", access.Tier + " tier, backend " +
        (string.IsNullOrEmpty(access.BackendName) ? "none" : access.BackendName) +
        " (MSR " + YesNo(access.SupportsModelSpecificRegisters) +
        ", I/O ports " + YesNo(access.SupportsIoPort) +
        ", PCI config " + YesNo(access.SupportsPciConfig) + ")");
      if (!string.IsNullOrWhiteSpace(access.UnavailableReason))
        Row(md, "Unavailable sensors", access.UnavailableReason);

      List<string> hardware = new List<string>();
      int sensors = 0;
      int withoutValue = 0;
      foreach (DiagnosticHardware node in snapshot.AllHardware()) {
        hardware.Add(node.Type + ": " + node.Name);
        foreach (DiagnosticSensor sensor in node.Sensors) {
          sensors++;
          if (!sensor.Value.HasValue)
            withoutValue++;
        }
      }
      Row(md, "Hardware", hardware.Count == 0 ? "none detected" : string.Join("; ", hardware));
      Row(md, "Sensors", sensors.ToString(CultureInfo.InvariantCulture) + " (" +
        withoutValue.ToString(CultureInfo.InvariantCulture) + " without a value)");
      Row(md, "Findings",
        Count(snapshot, DiagnosticSeverity.Critical) + " critical, " +
        Count(snapshot, DiagnosticSeverity.Warning) + " warning, " +
        Count(snapshot, DiagnosticSeverity.Info) + " info");
      Line(md);
    }

    private static void WriteFindings(StringBuilder md, DiagnosticSnapshot snapshot) {
      Line(md, "## Findings");
      Line(md);
      if (snapshot.Findings.Count == 0) {
        Line(md, "No rule matched. That does not rule out a problem; check the " +
          "tables below.");
        Line(md);
        return;
      }

      Line(md, "| # | Severity | Rule | Finding | Hardware | Evidence |");
      Line(md, "|---|---|---|---|---|---|");
      int number = 1;
      foreach (DiagnosticFinding finding in snapshot.Findings) {
        Line(md, "| " + number.ToString(CultureInfo.InvariantCulture) +
          " | " + SeverityLabel(finding.Severity) +
          " | `" + Code(finding.RuleId) + "`" +
          " | " + Cell(finding.Title) +
          " | " + Cell(finding.HardwareName ?? "") +
          " | " + EvidenceCell(finding) + " |");
        number++;
      }
      Line(md);

      Line(md, "### Finding details");
      Line(md);
      number = 1;
      foreach (DiagnosticFinding finding in snapshot.Findings) {
        Line(md, number.ToString(CultureInfo.InvariantCulture) + ". **" +
          Inline(finding.Title) + "** (" +
          JsonSeverity(finding.Severity) + ", `" + Code(finding.RuleId) + "`" +
          (finding.HardwareName != null ? ", " + Inline(finding.HardwareName) : "") +
          "): " + Inline(finding.Explanation));
        number++;
      }
      Line(md);
    }

    private static string EvidenceCell(DiagnosticFinding finding) {
      List<string> parts = new List<string>();
      int shown = 0;
      foreach (DiagnosticEvidence evidence in finding.Evidence) {
        if (shown == MaxEvidenceShown) {
          parts.Add("and " + (finding.Evidence.Count - shown).ToString(
            CultureInfo.InvariantCulture) + " more (see JSON)");
          break;
        }
        parts.Add(DescribeEvidence(evidence));
        shown++;
      }
      return Cell(string.Join("; ", parts));
    }

    internal static string DescribeEvidence(DiagnosticEvidence evidence) {
      StringBuilder b = new StringBuilder();
      if (evidence.SensorName != null) {
        b.Append(evidence.SensorName);
        if (evidence.Type.HasValue) {
          SensorType type = evidence.Type.Value;
          b.Append(": ").Append(DiagnosticFormat.WithUnit(evidence.Value, type));
          if (evidence.Max.HasValue && evidence.Max != evidence.Value)
            b.Append(", max ").Append(DiagnosticFormat.WithUnit(evidence.Max, type));
          if (evidence.Threshold.HasValue)
            b.Append(", limit ").Append(DiagnosticFormat.WithUnit(evidence.Threshold, type));
        }
      }
      if (!string.IsNullOrEmpty(evidence.Note)) {
        if (b.Length > 0)
          b.Append(" (").Append(evidence.Note).Append(')');
        else
          b.Append(evidence.Note);
      }
      if (evidence.SensorIdentifier != null)
        b.Append(" [").Append(evidence.SensorIdentifier).Append(']');
      return b.ToString();
    }

    private static void WriteRecentAlerts(StringBuilder md, DiagnosticSnapshot snapshot) {
      if (snapshot.RecentAlerts == null)
        return;

      Line(md, "## Recent alerts");
      Line(md);
      Line(md, "Alerts raised while the application was running, newest first (at most " +
        AlertEngine.MaxRecentAlerts.ToString(CultureInfo.InvariantCulture) + "). Unlike " +
        "the findings, these were watched over time: each condition held for its rule's " +
        "duration before the alert fired, and \"recovered\" marks when it cleared. " +
        "Messages use the units chosen in the application, which may be °F.");
      Line(md);
      if (snapshot.RecentAlerts.Count == 0) {
        Line(md, "None since the application started.");
        Line(md);
        return;
      }

      Line(md, "| Time | Severity | Event | Alert | Details |");
      Line(md, "|---|---|---|---|---|");
      foreach (AlertRecord alert in snapshot.RecentAlerts) {
        Line(md, "| " + DiagnosticFormat.LocalTimestamp(alert.Time) +
          " | " + SeverityLabel(alert.Severity) +
          " | " + DiagnosticJsonWriter.AlertKindName(alert.Kind) +
          " | " + Cell(alert.Title) +
          " | " + Cell(alert.Message) +
          (alert.SensorIdentifier != null ? " `" + Code(alert.SensorIdentifier) + "`" : "") +
          " |");
      }
      Line(md);
    }

    private static void WriteHardware(StringBuilder md, DiagnosticSnapshot snapshot) {
      Line(md, "## Hardware and sensors");
      Line(md);
      Line(md, "Min and Max: since application start. Avg, Peak at and History: " +
        "recorded history window. Sensors marked *hidden* are hidden by default in " +
        "the application's tree.");
      Line(md);

      foreach (DiagnosticHardware hardware in snapshot.AllHardware()) {
        Line(md, "### " + Inline(hardware.Type + ": " + hardware.Path));
        Line(md);
        Line(md, "Identifier: `" + Code(hardware.Identifier) + "`");
        Line(md);
        if (hardware.Sensors.Count == 0) {
          Line(md, "No sensors.");
          Line(md);
          continue;
        }

        Line(md, "| Sensor | Type | Current | Min | Max | Avg | Peak at | History | Notes | Identifier |");
        Line(md, "|---|---|---|---|---|---|---|---|---|---|");
        foreach (DiagnosticSensor sensor in hardware.Sensors)
          WriteSensorRow(md, snapshot, sensor);
        Line(md);
      }
    }

    private static void WriteSensorRow(StringBuilder md, DiagnosticSnapshot snapshot,
      DiagnosticSensor sensor) {
      DiagnosticHistory? history = sensor.History;
      string average = history != null
        ? DiagnosticFormat.WithUnit(history.Average, sensor.Type) : DiagnosticFormat.NoValue;
      string peakAt = history != null
        ? PeakTime(snapshot, history) : DiagnosticFormat.NoValue;
      string window = history == null ? "none"
        : history.SampleCount == 1 ? "1 sample"
        : history.SampleCount.ToString(CultureInfo.InvariantCulture) + " over " +
          DiagnosticFormat.Duration(history.Window);

      Line(md, "| " + Cell(sensor.Name) +
        " | " + sensor.Type +
        " | " + DiagnosticFormat.WithUnit(sensor.Value, sensor.Type) +
        " | " + DiagnosticFormat.WithUnit(sensor.Min, sensor.Type) +
        " | " + DiagnosticFormat.WithUnit(sensor.Max, sensor.Type) +
        " | " + average +
        " | " + peakAt +
        " | " + window +
        " | " + Cell(SensorNotes(sensor)) +
        " | `" + Code(sensor.Identifier) + "` |");
    }

    private static string PeakTime(DiagnosticSnapshot snapshot, DiagnosticHistory history) {
      DateTimeOffset local = new DateTimeOffset(DateTime.SpecifyKind(
        history.MaximumTimeUtc, DateTimeKind.Utc)).ToOffset(snapshot.ExportTime.Offset);
      string format = local.Date == snapshot.ExportTime.Date ? "HH:mm:ss" : "MM-dd HH:mm";
      return local.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string SensorNotes(DiagnosticSensor sensor) {
      List<string> notes = new List<string>();
      if (sensor.IsDefaultHidden)
        notes.Add("hidden");
      foreach (DiagnosticParameter parameter in sensor.Parameters) {
        bool tjMax = parameter.Name.TrimStart().StartsWith("TjMax",
          StringComparison.OrdinalIgnoreCase);
        if (tjMax || !parameter.IsDefault)
          notes.Add(parameter.Name + " = " + DiagnosticFormat.Plain(parameter.Value) +
            (parameter.IsDefault ? "" : " (user-set)"));
      }
      if (sensor.Control != null) {
        DiagnosticControl control = sensor.Control;
        string mode = control.Mode == ControlMode.Software
          ? "manual " + DiagnosticFormat.Plain(control.SoftwareValue) + " %"
          : control.Mode == ControlMode.Default ? "automatic (hardware default)" : "undefined";
        notes.Add("control: " + mode + ", range " +
          DiagnosticFormat.Plain(control.MinSoftwareValue) + "-" +
          DiagnosticFormat.Plain(control.MaxSoftwareValue) + " %");
      }
      return string.Join("; ", notes);
    }

    private static void WriteAppendix(StringBuilder md, DiagnosticSnapshot snapshot) {
      Line(md, "## Appendix: full Open Hardware Monitor report");
      Line(md);
      if (string.IsNullOrEmpty(snapshot.Report)) {
        Line(md, "Not included.");
        return;
      }

      string report = snapshot.Report.Replace("\r\n", "\n").Trim('\n');
      string fence = new string('`', Math.Max(3, LongestRun(report, '`') + 1));
      Line(md, fence + "text");
      Line(md, report);
      Line(md, fence);
    }

    // ---- helpers --------------------------------------------------------------

    private static void Row(StringBuilder md, string item, string value) {
      Line(md, "| " + Cell(item) + " | " + Cell(value) + " |");
    }

    private static string Count(DiagnosticSnapshot snapshot, DiagnosticSeverity severity) {
      return snapshot.CountFindings(severity).ToString(CultureInfo.InvariantCulture);
    }

    private static string SeverityLabel(DiagnosticSeverity severity) {
      switch (severity) {
        case DiagnosticSeverity.Critical: return "**CRITICAL**";
        case DiagnosticSeverity.Warning: return "**WARNING**";
        default: return "info";
      }
    }

    private static string JsonSeverity(DiagnosticSeverity severity) {
      return DiagnosticJsonWriter.SeverityName(severity);
    }

    private static string YesNo(bool value) {
      return value ? "yes" : "no";
    }

    private static int LongestRun(string text, char ch) {
      int longest = 0;
      int run = 0;
      foreach (char c in text) {
        run = c == ch ? run + 1 : 0;
        if (run > longest)
          longest = run;
      }
      return longest;
    }

    /// <summary>Text for a table cell: no line breaks, pipes escaped.</summary>
    internal static string Cell(string text) {
      return Inline(text).Replace("|", "\\|");
    }

    /// <summary>Single-line text; sensor names are user-editable.</summary>
    internal static string Inline(string text) {
      if (string.IsNullOrEmpty(text))
        return "";
      return text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ')
        .Replace('\t', ' ');
    }

    /// <summary>Text inside a code span, which cannot contain a backtick.</summary>
    private static string Code(string text) {
      return Inline(text).Replace('`', '\'').Replace("|", "\\|");
    }
  }
}
