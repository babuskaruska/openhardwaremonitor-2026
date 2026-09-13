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

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>
  /// A semantic version (https://semver.org) as used by release tags and the
  /// application's informational version: "v0.11.0", "0.10.0-dev",
  /// "1.0.0-beta.2+852d066".
  ///
  /// Comparison follows SemVer 2.0 precedence: major, minor and patch compare
  /// numerically (so 0.10.0 is newer than 0.9.0, which a string comparison
  /// gets wrong), a pre-release is older than the same release, pre-release
  /// identifiers compare one by one (numeric ones numerically and before
  /// alphanumeric ones), and build metadata after "+" is ignored.
  ///
  /// Parsing is slightly more lenient than the specification: a leading "v"
  /// is accepted, and a missing patch number means 0 ("1.2" is 1.2.0).
  /// </summary>
  public sealed class SemanticVersion : IComparable<SemanticVersion>,
    IEquatable<SemanticVersion> {

    private const int MaxLength = 128;

    private readonly string[] preRelease;

    private SemanticVersion(int major, int minor, int patch, string[] preRelease,
      string? buildMetadata) {
      Major = major;
      Minor = minor;
      Patch = patch;
      this.preRelease = preRelease;
      BuildMetadata = buildMetadata;
    }

    public SemanticVersion(int major, int minor, int patch)
      : this(major, minor, patch, Array.Empty<string>(), null) {
      if (major < 0 || minor < 0 || patch < 0)
        throw new ArgumentOutOfRangeException(nameof(major),
          "Version numbers cannot be negative.");
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The dot-separated pre-release identifiers; empty for a release.</summary>
    public IReadOnlyList<string> PreRelease {
      get { return preRelease; }
    }

    /// <summary>Everything after "+", or null. Not part of comparisons.</summary>
    public string? BuildMetadata { get; }

    public bool IsPreRelease {
      get { return preRelease.Length > 0; }
    }

    /// <summary>
    /// True for "-dev" versions, which Directory.Build.props gives every
    /// build that is not made from a release tag.
    /// </summary>
    public bool IsDevelopmentBuild {
      get {
        return preRelease.Length > 0 &&
          string.Equals(preRelease[0], "dev", StringComparison.OrdinalIgnoreCase);
      }
    }

    /// <summary>The same version without pre-release and build metadata.</summary>
    public SemanticVersion Core {
      get { return new SemanticVersion(Major, Minor, Patch); }
    }

    public static SemanticVersion Parse(string text) {
      if (!TryParse(text, out SemanticVersion? version))
        throw new FormatException("Not a semantic version: " + text);
      return version!;
    }

    public static bool TryParse(string? text, out SemanticVersion? version) {
      version = null;
      if (string.IsNullOrWhiteSpace(text))
        return false;
      string s = text.Trim();
      if (s.Length > MaxLength)
        return false;
      if (s[0] == 'v' || s[0] == 'V')
        s = s.Substring(1);

      string? metadata = null;
      int plus = s.IndexOf('+');
      if (plus >= 0) {
        metadata = s.Substring(plus + 1);
        s = s.Substring(0, plus);
        if (!AreValidIdentifiers(metadata))
          return false;
      }

      string[] pre = Array.Empty<string>();
      int dash = s.IndexOf('-');
      if (dash >= 0) {
        string preText = s.Substring(dash + 1);
        s = s.Substring(0, dash);
        if (!AreValidIdentifiers(preText))
          return false;
        pre = preText.Split('.');
      }

      string[] parts = s.Split('.');
      if (parts.Length < 2 || parts.Length > 3)
        return false;
      if (!TryParseNumber(parts[0], out int major) ||
        !TryParseNumber(parts[1], out int minor))
        return false;
      int patch = 0;
      if (parts.Length == 3 && !TryParseNumber(parts[2], out patch))
        return false;

      version = new SemanticVersion(major, minor, patch, pre, metadata);
      return true;
    }

    private static bool TryParseNumber(string text, out int value) {
      value = 0;
      if (text.Length == 0 || text.Length > 9)
        return false;
      foreach (char c in text)
        if (c < '0' || c > '9')
          return false;
      return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture,
        out value);
    }

    private static bool AreValidIdentifiers(string text) {
      if (text.Length == 0)
        return false;
      foreach (string identifier in text.Split('.')) {
        if (identifier.Length == 0)
          return false;
        foreach (char c in identifier)
          if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'z') &&
            !(c >= 'A' && c <= 'Z') && c != '-')
            return false;
      }
      return true;
    }

    private static bool IsNumeric(string identifier) {
      foreach (char c in identifier)
        if (c < '0' || c > '9')
          return false;
      return true;
    }

    private static int CompareIdentifiers(string a, string b) {
      bool numericA = IsNumeric(a);
      bool numericB = IsNumeric(b);
      if (numericA && numericB) {
        // Compare by digits rather than parsing, so no identifier overflows.
        string trimmedA = a.TrimStart('0');
        string trimmedB = b.TrimStart('0');
        if (trimmedA.Length != trimmedB.Length)
          return trimmedA.Length.CompareTo(trimmedB.Length);
        return string.CompareOrdinal(trimmedA, trimmedB);
      }
      if (numericA)
        return -1;
      if (numericB)
        return 1;
      return string.CompareOrdinal(a, b);
    }

    public int CompareTo(SemanticVersion? other) {
      if (other is null)
        return 1;
      int result = Major.CompareTo(other.Major);
      if (result != 0)
        return result;
      result = Minor.CompareTo(other.Minor);
      if (result != 0)
        return result;
      result = Patch.CompareTo(other.Patch);
      if (result != 0)
        return result;

      if (preRelease.Length == 0 || other.preRelease.Length == 0)
        return other.preRelease.Length.CompareTo(preRelease.Length);
      int count = Math.Min(preRelease.Length, other.preRelease.Length);
      for (int i = 0; i < count; i++) {
        result = CompareIdentifiers(preRelease[i], other.preRelease[i]);
        if (result != 0)
          return result;
      }
      return preRelease.Length.CompareTo(other.preRelease.Length);
    }

    public bool Equals(SemanticVersion? other) {
      return CompareTo(other) == 0;
    }

    public override bool Equals(object? obj) {
      return obj is SemanticVersion other && Equals(other);
    }

    public override int GetHashCode() {
      HashCode hash = new HashCode();
      hash.Add(Major);
      hash.Add(Minor);
      hash.Add(Patch);
      foreach (string identifier in preRelease)
        hash.Add(IsNumeric(identifier) ? identifier.TrimStart('0') : identifier,
          StringComparer.Ordinal);
      return hash.ToHashCode();
    }

    public static bool operator <(SemanticVersion a, SemanticVersion b) {
      return a.CompareTo(b) < 0;
    }

    public static bool operator >(SemanticVersion a, SemanticVersion b) {
      return a.CompareTo(b) > 0;
    }

    public static bool operator <=(SemanticVersion a, SemanticVersion b) {
      return a.CompareTo(b) <= 0;
    }

    public static bool operator >=(SemanticVersion a, SemanticVersion b) {
      return a.CompareTo(b) >= 0;
    }

    /// <summary>"1.2.3" or "1.2.3-beta.1", without build metadata.</summary>
    public override string ToString() {
      StringBuilder s = new StringBuilder();
      s.Append(Major.ToString(CultureInfo.InvariantCulture)).Append('.')
        .Append(Minor.ToString(CultureInfo.InvariantCulture)).Append('.')
        .Append(Patch.ToString(CultureInfo.InvariantCulture));
      if (preRelease.Length > 0)
        s.Append('-').Append(string.Join(".", preRelease));
      return s.ToString();
    }
  }
}
