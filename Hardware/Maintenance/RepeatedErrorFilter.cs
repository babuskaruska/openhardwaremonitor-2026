/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace OpenHardwareMonitor.Hardware.Maintenance {

  /// <summary>
  /// Decides whether a recurring error is worth another log entry. A device
  /// that throws on every sensor update would otherwise write 86,400 entries
  /// a day and push everything useful out of the log.
  ///
  /// An error is logged the first time it is seen and then at most once per
  /// interval, with a count of the occurrences in between. Errors are told
  /// apart by exception type and message (inner exceptions included), and a
  /// few are remembered at once, so two devices failing alternately do not
  /// defeat the filter.
  /// </summary>
  public sealed class RepeatedErrorFilter {

    private const int MaxRemembered = 16;

    private sealed class Seen {
      public DateTimeOffset LoggedAt;
      public int Suppressed;
    }

    private readonly TimeSpan interval;
    private readonly Func<DateTimeOffset> clock;
    private readonly object sync = new object();
    private readonly Dictionary<string, Seen> seen =
      new Dictionary<string, Seen>(StringComparer.Ordinal);

    public RepeatedErrorFilter(TimeSpan interval, Func<DateTimeOffset>? clock = null) {
      this.interval = interval;
      this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True when <paramref name="exception"/> should be logged now.
    /// <paramref name="suppressed"/> is how many identical errors were not
    /// logged since it was last logged.
    /// </summary>
    public bool ShouldLog(Exception exception, out int suppressed) {
      string signature = Signature(exception);
      DateTimeOffset now = clock();
      lock (sync) {
        if (seen.TryGetValue(signature, out Seen? entry)) {
          // A clock that went backwards counts as the interval having passed.
          if (now >= entry.LoggedAt && now - entry.LoggedAt < interval) {
            entry.Suppressed++;
            suppressed = 0;
            return false;
          }
          suppressed = entry.Suppressed;
          entry.LoggedAt = now;
          entry.Suppressed = 0;
          return true;
        }

        if (seen.Count >= MaxRemembered)
          ForgetOldest();
        seen[signature] = new Seen { LoggedAt = now };
        suppressed = 0;
        return true;
      }
    }

    private void ForgetOldest() {
      string? oldest = null;
      DateTimeOffset oldestTime = DateTimeOffset.MaxValue;
      foreach (KeyValuePair<string, Seen> pair in seen) {
        if (pair.Value.LoggedAt < oldestTime) {
          oldestTime = pair.Value.LoggedAt;
          oldest = pair.Key;
        }
      }
      if (oldest != null)
        seen.Remove(oldest);
    }

    internal static string Signature(Exception exception) {
      StringBuilder s = new StringBuilder();
      Exception? current = exception;
      for (int depth = 0; current != null && depth < 4; depth++) {
        if (depth > 0)
          s.Append(" <- ");
        s.Append(current.GetType().FullName).Append(": ").Append(current.Message);
        current = current.InnerException;
      }
      return s.ToString();
    }
  }
}
