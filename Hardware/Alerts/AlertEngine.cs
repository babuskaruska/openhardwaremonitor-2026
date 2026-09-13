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
using OpenHardwareMonitor.Hardware.Diagnostics;

namespace OpenHardwareMonitor.Hardware.Alerts {

  /// <summary>
  /// Watches sensors after every update and raises alerts for the built-in
  /// rules (the analyzer's thresholds) and for user-defined rules.
  ///
  /// Per rule and sensor, or per device for grouped rules:
  /// <list type="bullet">
  /// <item>Debounce: the condition must hold on every update for the rule's
  /// duration; one update without it starts the wait over.</item>
  /// <item>Hysteresis: an active alert clears only once the reading is back
  /// past its limit by a margin, so a value hovering at the limit does not
  /// flap between raised and recovered.</item>
  /// <item>Cooldown: after a notification the same rule and sensor stays
  /// quiet for <see cref="Cooldown"/>; repeats are still logged. Getting worse
  /// (warning to critical) always notifies.</item>
  /// <item>Counters such as NVMe media errors alert when the count rises
  /// above the count last seen, which is kept in the settings.</item>
  /// </list>
  ///
  /// Threading: <see cref="Update()"/> runs on the sensor thread under the
  /// hardware lock after every update, so watches are built once and rebuilt
  /// only when hardware, sensors or custom rules change. All other members may
  /// be called from any thread. <see cref="AlertRaised"/> is raised on the
  /// thread calling <see cref="Update()"/>, after the engine's lock is released.
  /// </summary>
  public sealed class AlertEngine {

    public const int MaxRecentAlerts = 100;

    /// <summary>The rule id of user-defined alerts.</summary>
    public const string CustomRuleId = "custom";

    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(15);

    private const string EnabledKey = "alerts.enabled";
    private const string CustomCountKey = "alerts.custom.count";
    private const string CustomKeyPrefix = "alerts.custom.";
    private const int MaxCustomRules = 1000;

    private readonly IComputer computer;
    private readonly ISettings settings;
    private readonly object sync = new object();
    private readonly Dictionary<string, RuleSwitch> switches =
      new Dictionary<string, RuleSwitch>(StringComparer.Ordinal);
    private readonly RuleSwitch customSwitch = new RuleSwitch(true);
    private readonly List<CustomAlertRule> customRules = new List<CustomAlertRule>();
    private readonly List<AlertWatch> watches = new List<AlertWatch>();
    private Dictionary<string, AlertState> states =
      new Dictionary<string, AlertState>(StringComparer.Ordinal);
    private readonly HashSet<IHardware> subscribed = new HashSet<IHardware>();
    private readonly Queue<AlertRecord> log = new Queue<AlertRecord>();
    private readonly List<AlertRecord> pending = new List<AlertRecord>();
    private AlertRecord? latest;
    private Func<float, SensorType, string> formatValue = DefaultFormat;
    private TimeSpan cooldown = DefaultCooldown;
    private bool enabled;

    // Set from hardware events on the sensor thread and from settings changes
    // on the UI thread; read at the start of every update.
    private volatile bool dirty = true;

    public AlertEngine(IComputer computer, ISettings settings) {
      this.computer = computer ?? throw new ArgumentNullException(nameof(computer));
      this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

      enabled = settings.GetValue(EnabledKey, "true") != "false";
      foreach (AlertRuleInfo rule in AlertBuiltInRules.All)
        switches[rule.Id] = new RuleSwitch(settings.GetValue(RuleKey(rule.Id), "true") != "false");
      LoadCustomRules();

      computer.HardwareAdded += OnHardwareChanged;
      computer.HardwareRemoved += OnHardwareChanged;
    }

    /// <summary>Raised for every logged alert; see <see cref="AlertRecord.Notify"/>.</summary>
    public event EventHandler<AlertRecord>? AlertRaised;

    public static IReadOnlyList<AlertRuleInfo> BuiltInRules {
      get { return AlertBuiltInRules.All; }
    }

    /// <summary>The master switch. Off clears every active alert.</summary>
    public bool Enabled {
      get {
        lock (sync)
          return enabled;
      }
      set {
        lock (sync) {
          if (enabled == value)
            return;
          enabled = value;
          settings.SetValue(EnabledKey, value ? "true" : "false");
          foreach (AlertState state in states.Values)
            state.Reset();
        }
      }
    }

    public TimeSpan Cooldown {
      get {
        lock (sync)
          return cooldown;
      }
      set {
        if (value < TimeSpan.Zero)
          throw new ArgumentOutOfRangeException(nameof(value));
        lock (sync)
          cooldown = value;
      }
    }

    /// <summary>
    /// Formats readings and limits for alert texts. The default uses canonical
    /// units; the application substitutes the units the user chose. Called on
    /// the sensor thread.
    /// </summary>
    public Func<float, SensorType, string> FormatValue {
      get {
        lock (sync)
          return formatValue;
      }
      set {
        if (value == null)
          throw new ArgumentNullException(nameof(value));
        lock (sync)
          formatValue = value;
      }
    }

    private static string DefaultFormat(float value, SensorType type) {
      return DiagnosticFormat.WithUnit(value, type);
    }

    private static string RuleKey(string ruleId) {
      return "alerts." + ruleId + ".enabled";
    }

    // ---- rules ------------------------------------------------------------------

    public bool IsRuleEnabled(string ruleId) {
      lock (sync)
        return switches.TryGetValue(ruleId, out RuleSwitch? rule) && rule.Enabled;
    }

    /// <summary>Turns a built-in rule on or off. Off clears its active alerts.</summary>
    public void SetRuleEnabled(string ruleId, bool value) {
      lock (sync) {
        if (!switches.TryGetValue(ruleId, out RuleSwitch? rule))
          throw new ArgumentException("Not a built-in alert rule.", nameof(ruleId));
        if (rule.Enabled == value)
          return;
        rule.Enabled = value;
        settings.SetValue(RuleKey(ruleId), value ? "true" : "false");
        if (!value)
          foreach (AlertWatch watch in watches)
            if (watch.RuleId == ruleId)
              watch.State.Reset();
      }
    }

    public IReadOnlyList<CustomAlertRule> CustomRules {
      get {
        lock (sync)
          return customRules.ToArray();
      }
    }

    public void AddCustomRule(CustomAlertRule rule) {
      if (rule == null)
        throw new ArgumentNullException(nameof(rule));
      lock (sync) {
        if (FindCustomRule(rule.Id) >= 0)
          throw new ArgumentException("A rule with this id already exists.", nameof(rule));
        customRules.Add(rule);
        SaveCustomRules();
        dirty = true;
      }
    }

    public bool RemoveCustomRule(string id) {
      lock (sync) {
        int index = FindCustomRule(id);
        if (index < 0)
          return false;
        customRules.RemoveAt(index);
        SaveCustomRules();
        dirty = true;
        return true;
      }
    }

    private int FindCustomRule(string id) {
      for (int i = 0; i < customRules.Count; i++)
        if (customRules[i].Id == id)
          return i;
      return -1;
    }

    private static string CustomKey(int index) {
      return CustomKeyPrefix + index.ToString(CultureInfo.InvariantCulture);
    }

    private int SavedCustomCount() {
      return int.TryParse(settings.GetValue(CustomCountKey, "0"), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out int count)
        ? Math.Max(0, Math.Min(MaxCustomRules, count)) : 0;
    }

    private void LoadCustomRules() {
      int count = SavedCustomCount();
      for (int i = 0; i < count; i++) {
        if (CustomAlertRule.TryParse(settings.GetValue(CustomKey(i), ""),
          out CustomAlertRule? rule) && rule != null && FindCustomRule(rule.Id) < 0)
          customRules.Add(rule);
      }
    }

    private void SaveCustomRules() {
      int previous = SavedCustomCount();
      for (int i = 0; i < customRules.Count; i++)
        settings.SetValue(CustomKey(i), customRules[i].Serialize());
      for (int i = customRules.Count; i < previous; i++)
        settings.Remove(CustomKey(i));
      if (customRules.Count == 0)
        settings.Remove(CustomCountKey);
      else
        settings.SetValue(CustomCountKey,
          customRules.Count.ToString(CultureInfo.InvariantCulture));
    }

    // ---- state and log ------------------------------------------------------------

    /// <summary>Alerts currently active, for rules that are on.</summary>
    public int ActiveCount {
      get {
        lock (sync) {
          if (!enabled)
            return 0;
          int count = 0;
          foreach (AlertWatch watch in watches)
            if (watch.Switch.Enabled && watch.State.Active)
              count++;
          return count;
        }
      }
    }

    /// <summary>The worst severity among active alerts, or null when none are active.</summary>
    public DiagnosticSeverity? HighestActiveSeverity {
      get {
        lock (sync) {
          if (!enabled)
            return null;
          DiagnosticSeverity? worst = null;
          foreach (AlertWatch watch in watches)
            if (watch.Switch.Enabled && watch.State.Active &&
              (!worst.HasValue || watch.State.Severity > worst.Value))
              worst = watch.State.Severity;
          return worst;
        }
      }
    }

    public AlertRecord? LatestAlert {
      get {
        lock (sync)
          return latest;
      }
    }

    /// <summary>Up to <see cref="MaxRecentAlerts"/> entries, newest first.</summary>
    public IReadOnlyList<AlertRecord> GetRecentAlerts() {
      lock (sync) {
        AlertRecord[] records = log.ToArray();
        Array.Reverse(records);
        return records;
      }
    }

    /// <summary>Rebuilds the sensor lookups before the next update.</summary>
    public void Invalidate() {
      dirty = true;
    }

    // ---- evaluation ---------------------------------------------------------------

    /// <summary>Call after the sensors have been updated.</summary>
    public void Update() {
      Update(DateTime.UtcNow);
    }

    internal void Update(DateTime utcNow) {
      AlertRecord[]? raised = null;
      lock (sync) {
        if (!enabled)
          return;
        if (dirty)
          Rebuild();

        foreach (AlertWatch watch in watches) {
          if (!watch.Switch.Enabled)
            continue;
          try {
            if (watch is CounterWatch counter)
              EvaluateCounter(counter, utcNow);
            else
              EvaluateCondition(watch, utcNow);
          } catch (Exception) {
            // One misbehaving sensor must not stop the other alerts.
          }
        }

        if (pending.Count > 0) {
          raised = pending.ToArray();
          pending.Clear();
        }
      }

      EventHandler<AlertRecord>? handler = AlertRaised;
      if (raised != null && handler != null)
        foreach (AlertRecord record in raised)
          handler(this, record);
    }

    private void EvaluateCondition(AlertWatch watch, DateTime now) {
      AlertState state = watch.State;
      AlertCondition condition = watch.Check(state.Active);

      if (condition == AlertCondition.Unknown || condition == AlertCondition.Clear) {
        state.Pending = false;
        state.Escalating = false;
        // A missing reading neither confirms nor clears an alert.
        if (condition == AlertCondition.Clear && state.Active) {
          state.Active = false;
          Record(watch, AlertKind.Recovered, DiagnosticSeverity.Info, condition, false, now);
        }
        return;
      }

      DiagnosticSeverity severity = condition == AlertCondition.Critical
        ? DiagnosticSeverity.Critical : DiagnosticSeverity.Warning;

      if (!state.Active) {
        if (!state.Pending) {
          state.Pending = true;
          state.PendingSince = now;
        }
        if (now - state.PendingSince < watch.Duration)
          return;
        state.Pending = false;
        state.Active = true;
        state.Severity = severity;
        Record(watch, AlertKind.Raised, severity, condition,
          TakeNotification(state, now, false), now);
        return;
      }

      // Already active: only a worse severity, held for the same duration, is news.
      if (severity <= state.Severity) {
        state.Escalating = false;
        return;
      }
      if (!state.Escalating) {
        state.Escalating = true;
        state.EscalatingSince = now;
      }
      if (now - state.EscalatingSince < watch.Duration)
        return;
      state.Escalating = false;
      state.Severity = severity;
      Record(watch, AlertKind.Escalated, severity, condition,
        TakeNotification(state, now, true), now);
    }

    private void EvaluateCounter(CounterWatch watch, DateTime now) {
      AlertState state = watch.State;
      // Nothing clears a counter, so a rise counts as active for one cooldown.
      if (state.Active && now >= state.ActiveUntil)
        state.Active = false;
      if (!watch.Poll())
        return;
      state.Active = true;
      state.Severity = DiagnosticSeverity.Warning;
      state.ActiveUntil = now + cooldown;
      Record(watch, AlertKind.Raised, DiagnosticSeverity.Warning, AlertCondition.Warning,
        TakeNotification(state, now, false), now);
    }

    private bool TakeNotification(AlertState state, DateTime now, bool force) {
      if (!force && state.HasNotified && now - state.LastNotified < cooldown)
        return false;
      state.HasNotified = true;
      state.LastNotified = now;
      return true;
    }

    private void Record(AlertWatch watch, AlertKind kind, DiagnosticSeverity severity,
      AlertCondition condition, bool notify, DateTime now) {
      AlertText text = watch.Describe(condition, formatValue);
      AlertRecord record = new AlertRecord {
        Time = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToLocalTime(),
        Kind = kind,
        Severity = severity,
        RuleId = watch.RuleId,
        Title = text.Title,
        Message = text.Message,
        HardwareName = text.HardwareName,
        SensorIdentifier = text.SensorIdentifier,
        Notify = notify
      };
      log.Enqueue(record);
      while (log.Count > MaxRecentAlerts)
        log.Dequeue();
      latest = record;
      pending.Add(record);
    }

    // ---- building watches -----------------------------------------------------------

    private void OnHardwareChanged(IHardware hardware) {
      dirty = true;
    }

    private void OnSensorChanged(ISensor sensor) {
      dirty = true;
    }

    private static void Collect(IHardware hardware, List<IHardware> all) {
      all.Add(hardware);
      foreach (IHardware sub in hardware.SubHardware)
        Collect(sub, all);
    }

    /// <summary>
    /// Matches sensors to rules once, so an update is a loop over prepared
    /// watches. Active alerts, pending waits and cooldowns carry over by key.
    /// </summary>
    private void Rebuild() {
      dirty = false;

      List<IHardware> all = new List<IHardware>();
      foreach (IHardware hardware in computer.Hardware)
        Collect(hardware, all);

      // Sensors appear after the first reads (NVMe health, fans a board
      // configuration enables), so every device is watched for them.
      HashSet<IHardware> present = new HashSet<IHardware>(all);
      foreach (IHardware hardware in all) {
        if (subscribed.Add(hardware)) {
          hardware.SensorAdded += OnSensorChanged;
          hardware.SensorRemoved += OnSensorChanged;
        }
      }
      subscribed.RemoveWhere(hardware => {
        if (present.Contains(hardware))
          return false;
        hardware.SensorAdded -= OnSensorChanged;
        hardware.SensorRemoved -= OnSensorChanged;
        return true;
      });

      List<AlertWatch> built = new List<AlertWatch>();
      Dictionary<string, ISensor>? byIdentifier = customRules.Count > 0
        ? new Dictionary<string, ISensor>(StringComparer.Ordinal) : null;
      foreach (IHardware hardware in all) {
        AlertWatchFactory.AddBuiltIn(hardware, settings, built);
        if (byIdentifier != null)
          foreach (ISensor sensor in hardware.Sensors)
            byIdentifier[sensor.Identifier.ToString()] = sensor;
      }
      if (byIdentifier != null)
        foreach (CustomAlertRule rule in customRules)
          if (byIdentifier.TryGetValue(rule.SensorIdentifier, out ISensor? sensor))
            built.Add(new CustomWatch(rule, sensor));

      Dictionary<string, AlertState> kept =
        new Dictionary<string, AlertState>(StringComparer.Ordinal);
      foreach (AlertWatch watch in built) {
        watch.Switch = watch.RuleId == CustomRuleId ? customSwitch : switches[watch.RuleId];
        if (states.TryGetValue(watch.Key, out AlertState? state))
          watch.State = state;
        kept[watch.Key] = watch.State;
      }
      states = kept;
      watches.Clear();
      watches.AddRange(built);
    }
  }
}
