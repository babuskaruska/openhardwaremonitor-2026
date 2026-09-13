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

namespace OpenHardwareMonitor.Hardware.Diagnostics {

  public sealed class DiagnosticExportResult {

    internal DiagnosticExportResult(DiagnosticSnapshot snapshot,
      string markdownPath, string jsonPath, string markdown) {
      Snapshot = snapshot;
      MarkdownPath = markdownPath;
      JsonPath = jsonPath;
      Markdown = markdown;
    }

    public DiagnosticSnapshot Snapshot { get; }
    public string MarkdownPath { get; }
    public string JsonPath { get; }

    /// <summary>The Markdown that was written, for the clipboard.</summary>
    public string Markdown { get; }
  }

  /// <summary>Writes a snapshot as a pair of .md and .json files.</summary>
  public static class DiagnosticExporter {

    public const string FilePrefix = "OHM-diagnostics-";

    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>Documents\OpenHardwareMonitor\Diagnostics for the current user.</summary>
    public static string DefaultDirectory {
      get {
        string documents = Environment.GetFolderPath(
          Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
          documents = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(documents, "OpenHardwareMonitor", "Diagnostics");
      }
    }

    /// <summary>"OHM-diagnostics-yyyyMMdd-HHmmss", from the local export time.</summary>
    public static string GetBaseFileName(DateTimeOffset exportTime) {
      return FilePrefix + exportTime.ToString("yyyyMMdd-HHmmss",
        CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Creates the directory if needed and writes both files. A second export
    /// within the same second gets a numeric suffix instead of overwriting.
    /// Throws IOException or UnauthorizedAccessException on failure.
    /// </summary>
    public static DiagnosticExportResult WriteFiles(DiagnosticSnapshot snapshot,
      string directory) {
      if (snapshot == null)
        throw new ArgumentNullException(nameof(snapshot));
      if (string.IsNullOrWhiteSpace(directory))
        throw new ArgumentException("A directory is required.", nameof(directory));

      string markdown = DiagnosticMarkdownWriter.ToMarkdown(snapshot);
      byte[] json = DiagnosticJsonWriter.ToUtf8Bytes(snapshot);

      string fullDirectory = Path.GetFullPath(directory);
      Directory.CreateDirectory(fullDirectory);

      string baseName = GetBaseFileName(snapshot.ExportTime);
      string markdownPath;
      string jsonPath;
      int attempt = 1;
      while (true) {
        string name = attempt == 1
          ? baseName : baseName + "-" + attempt.ToString(CultureInfo.InvariantCulture);
        markdownPath = Path.Combine(fullDirectory, name + ".md");
        jsonPath = Path.Combine(fullDirectory, name + ".json");
        if (!File.Exists(markdownPath) && !File.Exists(jsonPath))
          break;
        attempt++;
      }

      File.WriteAllText(markdownPath, markdown, Utf8NoBom);
      File.WriteAllBytes(jsonPath, json);
      return new DiagnosticExportResult(snapshot, markdownPath, jsonPath, markdown);
    }
  }
}
