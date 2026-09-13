/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// The default screen: one card per kind of hardware showing the few
  /// readings that matter. Every sensor remains one click away in the
  /// detailed list ("More info").
  /// </summary>
  public sealed class OverviewPanel : UserControl {

    private const int TrendSamples = 300;
    private const float MoveSeconds = 0.38f;

    private readonly IComputer computer;
    private readonly UnitManager unitManager;
    private readonly OverviewHeader header;
    private readonly BufferedPanel host;
    private readonly List<CardSlot> slots = new List<CardSlot>();
    private readonly Dictionary<string, Queue<float>> history =
      new Dictionary<string, Queue<float>>();
    private readonly Dictionary<SensorCard, CardMove> moves =
      new Dictionary<SensorCard, CardMove>();
    private Theme theme = Theme.Current;
    private bool rebuildPending = true;
    private bool firstUpdate = true;

    private sealed class CardSlot {
      public CardSlot(SensorCard card, Func<CardModel> build, IHardware? target) {
        Card = card;
        Build = build;
        Target = target;
      }

      public SensorCard Card { get; }
      public Func<CardModel> Build { get; }
      public IHardware? Target { get; }
      public bool Placed { get; set; }
    }

    private readonly struct CardMove {
      public CardMove(Rectangle from, Rectangle to, double start) {
        From = from;
        To = to;
        Start = start;
      }

      public Rectangle From { get; }
      public Rectangle To { get; }
      public double Start { get; }
    }

    private sealed class BufferedPanel : Panel {
      public BufferedPanel() {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer |
          ControlStyles.AllPaintingInWmPaint, true);
      }
    }

    public OverviewPanel(IComputer computer, UnitManager unitManager) {
      this.computer = computer;
      this.unitManager = unitManager;
      SetStyle(ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.AllPaintingInWmPaint, true);

      header = new OverviewHeader { Dock = DockStyle.Top };
      header.DetailsClicked += delegate { DetailsRequested?.Invoke(this, null); };
      header.ExportClicked += delegate { ExportRequested?.Invoke(this, EventArgs.Empty); };

      host = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true };
      host.Resize += delegate { LayoutCards(false); };

      // Docking resolves from the last-added control, so the header goes last.
      Controls.Add(host);
      Controls.Add(header);

      computer.HardwareAdded += OnHardwareChanged;
      computer.HardwareRemoved += OnHardwareChanged;
      ApplyTheme(theme);
    }

    /// <summary>Raised with the card's hardware, or null for the whole list.</summary>
    public event EventHandler<IHardware?>? DetailsRequested;

    public event EventHandler? ExportRequested;

    public bool ShowExportButton {
      get { return header.ShowExport; }
      set { header.ShowExport = value; }
    }

    public Theme Theme {
      get { return theme; }
      set { ApplyTheme(value); }
    }

    private int S(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    private void ApplyTheme(Theme value) {
      theme = value;
      BackColor = theme.Background;
      host.BackColor = theme.Background;
      header.Theme = theme;
      foreach (CardSlot slot in slots)
        slot.Card.Theme = theme;
      Invalidate(true);
    }

    private void OnHardwareChanged(IHardware hardware) {
      rebuildPending = true;
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        computer.HardwareAdded -= OnHardwareChanged;
        computer.HardwareRemoved -= OnHardwareChanged;
      }
      base.Dispose(disposing);
    }

    /// <summary>Refreshes every card. Call after each sensor update.</summary>
    public void UpdateValues() {
      RecordHistory();
      bool rebuilt = rebuildPending;
      if (rebuildPending)
        Rebuild();

      header.Title = Environment.MachineName;
      header.Subtitle = (Environment.OSVersion.Version.Build >= 22000
        ? "Windows 11" : "Windows") + "  ·  " + Environment.ProcessorCount +
        " logical processors";
      bool deep = HardwareAccess.Tier == AccessTier.Deep;
      header.Chip = deep ? "Full sensor access" : "Basic sensor access";
      header.ChipSeverity = deep ? Severity.Normal : Severity.Warm;
      if (firstUpdate) {
        header.PlayEntrance();
        firstUpdate = false;
      }
      header.Invalidate();

      foreach (CardSlot slot in slots) {
        try {
          slot.Card.Model = slot.Build();
        } catch (Exception) {
          // One misbehaving sensor must not blank the whole screen.
        }
      }
      LayoutCards(!rebuilt);

      if (rebuilt)
        for (int i = 0; i < slots.Count; i++)
          slots[i].Card.PlayEntrance(0.08f + 0.055f * i);
    }

    // ---- history --------------------------------------------------------------

    private void RecordHistory() {
      foreach (IHardware hardware in computer.Hardware)
        foreach (ISensor sensor in AllSensors(hardware)) {
          string key = sensor.Identifier.ToString();
          if (!history.TryGetValue(key, out Queue<float>? samples)) {
            samples = new Queue<float>(TrendSamples);
            DateTime since = DateTime.UtcNow.AddSeconds(-TrendSamples);
            foreach (SensorValue value in sensor.Values)
              if (value.Time >= since && !float.IsNaN(value.Value))
                samples.Enqueue(value.Value);
            history[key] = samples;
          }
          samples.Enqueue(sensor.Value ?? float.NaN);
          while (samples.Count > TrendSamples)
            samples.Dequeue();
        }
    }

    private float[]? Trend(ISensor? sensor) {
      if (sensor == null ||
        !history.TryGetValue(sensor.Identifier.ToString(), out Queue<float>? samples) ||
        samples.Count < 2)
        return null;
      float[] values = samples.ToArray();
      if (sensor.SensorType == SensorType.Temperature)
        for (int i = 0; i < values.Length; i++)
          values[i] = ToDisplayTemperature(values[i]);
      return values;
    }

    // ---- layout -----------------------------------------------------------------

    private void Rebuild() {
      rebuildPending = false;
      host.SuspendLayout();
      moves.Clear();
      foreach (CardSlot slot in slots) {
        host.Controls.Remove(slot.Card);
        slot.Card.Dispose();
      }
      slots.Clear();

      IHardware[] all = computer.Hardware;
      foreach (IHardware cpu in all.Where(h => h.HardwareType == HardwareType.CPU))
        Add(cpu, () => BuildCpu(cpu));
      foreach (IHardware gpu in all.Where(h => h.HardwareType == HardwareType.GpuNvidia ||
        h.HardwareType == HardwareType.GpuAti))
        Add(gpu, () => BuildGpu(gpu));
      foreach (IHardware ram in all.Where(h => h.HardwareType == HardwareType.RAM))
        Add(ram, () => BuildMemory(ram));
      foreach (IHardware board in all.Where(h => h.HardwareType == HardwareType.Mainboard))
        Add(board, () => BuildMainboard(board));
      IHardware? firstDrive = all.FirstOrDefault(h => h.HardwareType == HardwareType.HDD);
      if (firstDrive != null)
        Add(firstDrive, BuildStorage);
      foreach (IHardware other in all.Where(h => h.HardwareType == HardwareType.TBalancer ||
        h.HardwareType == HardwareType.Heatmaster))
        Add(other, () => BuildGeneric(other));

      host.ResumeLayout(false);
    }

    private void Add(IHardware? target, Func<CardModel> build) {
      SensorCard card = new SensorCard { Theme = theme, TabIndex = slots.Count };
      CardSlot slot = new CardSlot(card, build, target);
      card.MoreInfoClicked += delegate { DetailsRequested?.Invoke(this, slot.Target); };
      card.MouseWheel += OnCardMouseWheel;
      slots.Add(slot);
      host.Controls.Add(card);
    }

    private void OnCardMouseWheel(object? sender, MouseEventArgs e) {
      int y = -host.AutoScrollPosition.Y - e.Delta;
      host.AutoScrollPosition = new Point(0, Math.Max(0, y));
    }

    /// <summary>
    /// Masonry layout: each card goes into the currently shortest column.
    /// When a card changes height during an update, the cards around it glide
    /// to their new places; resizing the window moves them immediately.
    /// </summary>
    private void LayoutCards(bool animate) {
      if (slots.Count == 0)
        return;
      if (!animate)
        moves.Clear();

      int margin = S(24), gap = S(16), minimumWidth = S(330);
      int available = Math.Max(minimumWidth, host.ClientSize.Width - 2 * margin);
      int columns = Math.Max(1, Math.Min(3, (available + gap) / (minimumWidth + gap)));
      int width = (available - (columns - 1) * gap) / columns;

      int[] columnHeights = new int[columns];
      Point scroll = host.AutoScrollPosition;
      host.SuspendLayout();
      foreach (CardSlot slot in slots) {
        SensorCard card = slot.Card;
        Padding shadow = card.ShadowPadding;
        int height = card.GetPreferredHeight(width);
        int column = 0;
        for (int i = 1; i < columns; i++)
          if (columnHeights[i] < columnHeights[column])
            column = i;

        Rectangle target = new Rectangle(
          margin + column * (width + gap) + scroll.X - shadow.Left,
          S(4) + columnHeights[column] + scroll.Y - shadow.Top,
          width + shadow.Horizontal, height + shadow.Vertical);

        bool canGlide = animate && slot.Placed && Animator.Enabled &&
          card.Width == target.Width;
        if (canGlide && card.Bounds != target)
          MoveTo(card, target);
        else if (!moves.ContainsKey(card) && card.Bounds != target)
          card.Bounds = target;
        slot.Placed = true;

        columnHeights[column] += height + gap;
      }
      host.AutoScrollMargin = new Size(0, margin);
      host.ResumeLayout(true);
    }

    private void MoveTo(SensorCard card, Rectangle target) {
      if (moves.TryGetValue(card, out CardMove existing) && existing.To == target)
        return;
      bool idle = moves.Count == 0;
      moves[card] = new CardMove(card.Bounds, target, Animator.Now);
      if (idle)
        Animator.Run(MoveFrame);
    }

    private bool MoveFrame() {
      if (IsDisposed) {
        moves.Clear();
        return false;
      }
      foreach (KeyValuePair<SensorCard, CardMove> entry in moves.ToList()) {
        CardMove move = entry.Value;
        float t = Easing.OutCubic((float)((Animator.Now - move.Start) / MoveSeconds));
        entry.Key.Bounds = new Rectangle(
          (int)Math.Round(move.From.X + (move.To.X - move.From.X) * t),
          (int)Math.Round(move.From.Y + (move.To.Y - move.From.Y) * t),
          (int)Math.Round(move.From.Width + (move.To.Width - move.From.Width) * t),
          (int)Math.Round(move.From.Height + (move.To.Height - move.From.Height) * t));
        if (t >= 1)
          moves.Remove(entry.Key);
      }
      return moves.Count > 0;
    }

    // ---- sensor helpers -----------------------------------------------------------

    private static IEnumerable<ISensor> AllSensors(IHardware hardware) {
      foreach (ISensor sensor in hardware.Sensors)
        yield return sensor;
      foreach (IHardware sub in hardware.SubHardware)
        foreach (ISensor sensor in AllSensors(sub))
          yield return sensor;
    }

    private static bool HasValue(ISensor? sensor) {
      return sensor != null && sensor.Value.HasValue &&
        !float.IsNaN(sensor.Value.Value);
    }

    private static ISensor? Find(IEnumerable<ISensor> sensors, SensorType type,
      string name) {
      return sensors.FirstOrDefault(s => s.SensorType == type &&
        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Contains(string text, string fragment) {
      return text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static float? TjMax(ISensor? sensor) {
      if (sensor == null)
        return null;
      foreach (IParameter parameter in sensor.Parameters)
        if (parameter.Name.StartsWith("TjMax", StringComparison.OrdinalIgnoreCase))
          return parameter.Value;
      return null;
    }

    /// <summary>"NVIDIA NVIDIA GeForce RTX 3070" becomes "NVIDIA GeForce RTX 3070".</summary>
    private static string CleanName(string name) {
      string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (words.Length > 1 &&
        string.Equals(words[0], words[1], StringComparison.OrdinalIgnoreCase))
        return string.Join(" ", words.Skip(1));
      return name;
    }

    // ---- formatting -----------------------------------------------------------------

    private bool Fahrenheit {
      get { return unitManager.TemperatureUnit == TemperatureUnit.Fahrenheit; }
    }

    private string TemperatureUnitText {
      get { return Fahrenheit ? "°F" : "°C"; }
    }

    private float ToDisplayTemperature(float celsius) {
      return Fahrenheit ? celsius * 1.8f + 32 : celsius;
    }

    private string Temperature(float? celsius) {
      return celsius.HasValue
        ? ToDisplayTemperature(celsius.Value).ToString("F0", CultureInfo.CurrentCulture) +
          " " + TemperatureUnitText
        : "–";
    }

    private static string Percent(float? value) {
      return value.HasValue
        ? value.Value.ToString("F0", CultureInfo.CurrentCulture) + " %" : "–";
    }

    private static string Watts(float? value) {
      return value.HasValue
        ? value.Value.ToString(value.Value < 100 ? "F1" : "F0",
          CultureInfo.CurrentCulture) + " W"
        : "–";
    }

    private static string Rpm(float? value) {
      if (!value.HasValue)
        return "–";
      return value.Value < 1 ? "0 RPM"
        : value.Value.ToString("N0", CultureInfo.CurrentCulture) + " RPM";
    }

    private static string Volts(float? value) {
      return value.HasValue
        ? value.Value.ToString(value.Value < 2 ? "F3" : "F2",
          CultureInfo.CurrentCulture) + " V"
        : "–";
    }

    private static string Megabytes(float? megabytes) {
      if (!megabytes.HasValue)
        return "–";
      return megabytes.Value >= 1024
        ? (megabytes.Value / 1024).ToString("F1", CultureInfo.CurrentCulture) + " GB"
        : megabytes.Value.ToString("F0", CultureInfo.CurrentCulture) + " MB";
    }

    private static string Gigabytes(float? gigabytes) {
      return gigabytes.HasValue
        ? gigabytes.Value.ToString("F1", CultureInfo.CurrentCulture) + " GB" : "–";
    }

    private static Severity AtLeast(float? value, float warm, float hot) {
      if (!value.HasValue)
        return Severity.Normal;
      return value.Value >= hot ? Severity.Hot
        : value.Value >= warm ? Severity.Warm : Severity.Normal;
    }

    private static Severity Worst(params Severity[] severities) {
      return severities.Length == 0 ? Severity.Normal : severities.Max();
    }

    private void SetHero(CardModel model, float? celsius, string label,
      Severity severity, ISensor? trend) {
      model.HeroNumber = celsius.HasValue ? ToDisplayTemperature(celsius.Value) : (float?)null;
      model.HeroDecimals = 0;
      model.HeroValue = "–";
      model.HeroUnit = TemperatureUnitText;
      model.HeroLabel = label;
      model.HeroSeverity = severity;
      model.Trend = Trend(trend);
      model.TrendLabel = "Last 5 minutes";
    }

    private void SetHeroPercent(CardModel model, float? value, string label,
      Severity severity, ISensor? trend) {
      model.HeroNumber = value;
      model.HeroDecimals = 0;
      model.HeroValue = "–";
      model.HeroUnit = "%";
      model.HeroLabel = label;
      model.HeroSeverity = severity;
      model.Trend = Trend(trend);
      model.TrendLabel = "Last 5 minutes";
    }

    private static string AccessNote(string what) {
      return HardwareAccess.Tier == AccessTier.Deep
        ? what + " are not available on this hardware."
        : what + " need PawnIO and administrator rights.";
    }

    // ---- cards -----------------------------------------------------------------------

    private CardModel BuildCpu(IHardware cpu) {
      List<ISensor> sensors = AllSensors(cpu).ToList();
      CardModel model = new CardModel { Badge = "CPU", Title = CleanName(cpu.Name) };

      int cores = sensors.Count(s => s.SensorType == SensorType.Load &&
        !string.Equals(s.Name, "CPU Total", StringComparison.OrdinalIgnoreCase));
      model.Subtitle = cores > 0 ? "Processor  ·  " + cores + " cores" : "Processor";

      ISensor? total = Find(sensors, SensorType.Load, "CPU Total");
      List<ISensor> temperatures = sensors
        .Where(s => s.SensorType == SensorType.Temperature && HasValue(s)).ToList();
      ISensor? package = temperatures.FirstOrDefault(s => Contains(s.Name, "Package")) ??
        temperatures.FirstOrDefault(s => Contains(s.Name, "Tctl")) ??
        temperatures.FirstOrDefault(s => Contains(s.Name, "Tdie"));
      ISensor? hottest = temperatures.Where(s => s != package)
        .OrderByDescending(s => s.Value).FirstOrDefault();
      ISensor? power = Find(sensors, SensorType.Power, "CPU Package") ??
        sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && HasValue(s));

      ISensor? hero = package ?? hottest;
      if (hero != null) {
        float tjMax = TjMax(hero) ?? 100;
        Severity Heat(float? value) => AtLeast(value, tjMax - 20, tjMax - 10);

        SetHero(model, hero.Value,
          package != null ? "Package temperature" : hero.Name + " temperature",
          Heat(hero.Value), hero);
        model.Metrics.Add(new CardMetric("Load", Percent(total?.Value),
          fraction: total?.Value / 100));
        model.Metrics.Add(new CardMetric("Power", Watts(power?.Value)));
        if (hottest != null)
          model.Metrics.Add(new CardMetric("Hottest core", Temperature(hottest.Value),
            Heat(hottest.Value)));
        model.Metrics.Add(new CardMetric("Peak this session", Temperature(hero.Max),
          Heat(hero.Max)));
      } else {
        SetHeroPercent(model, total?.Value, "Total load",
          AtLeast(total?.Value, 85, 97), total);
        ISensor? fastest = sensors
          .Where(s => s.SensorType == SensorType.Clock && HasValue(s) &&
            !Contains(s.Name, "Bus"))
          .OrderByDescending(s => s.Value).FirstOrDefault();
        model.Metrics.Add(new CardMetric("Fastest core", fastest?.Value != null
          ? (fastest.Value.Value / 1000).ToString("F2", CultureInfo.CurrentCulture) + " GHz"
          : "–"));
        model.Metrics.Add(new CardMetric("Peak load", Percent(total?.Max)));
        model.Note = AccessNote("Temperatures and power");
      }
      return model;
    }

    private CardModel BuildGpu(IHardware gpu) {
      List<ISensor> sensors = AllSensors(gpu).ToList();
      CardModel model = new CardModel { Badge = "GPU", Title = CleanName(gpu.Name) };

      ISensor? temperature = Find(sensors, SensorType.Temperature, "GPU Core") ??
        sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && HasValue(s));
      ISensor? load = Find(sensors, SensorType.Load, "GPU Core") ??
        sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && HasValue(s));
      ISensor? power = sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && HasValue(s));
      ISensor? fan = sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && HasValue(s));
      ISensor? memoryUsed = Find(sensors, SensorType.SmallData, "GPU Memory Used");
      ISensor? memoryTotal = Find(sensors, SensorType.SmallData, "GPU Memory Total");

      model.Subtitle = HasValue(memoryTotal)
        ? "Graphics  ·  " + Megabytes(memoryTotal!.Value) : "Graphics";

      Severity Heat(float? value) => AtLeast(value, 75, 83);
      if (HasValue(temperature))
        SetHero(model, temperature!.Value, "Core temperature", Heat(temperature.Value),
          temperature);
      else
        SetHeroPercent(model, load?.Value, "Core load", Severity.Normal, load);

      model.Metrics.Add(new CardMetric("Load", Percent(load?.Value),
        fraction: load?.Value / 100));
      model.Metrics.Add(new CardMetric("Power", Watts(power?.Value)));
      model.Metrics.Add(new CardMetric("Fan", Rpm(fan?.Value)));
      if (HasValue(memoryUsed) && HasValue(memoryTotal) && memoryTotal!.Value > 0)
        model.Metrics.Add(new CardMetric("Video memory",
          Megabytes(memoryUsed!.Value) + " of " + Megabytes(memoryTotal.Value),
          fraction: memoryUsed.Value / memoryTotal.Value));
      return model;
    }

    private CardModel BuildMemory(IHardware ram) {
      List<ISensor> sensors = AllSensors(ram).ToList();
      CardModel model = new CardModel { Badge = "RAM", Title = ram.Name };

      ISensor? load = Find(sensors, SensorType.Load, "Memory");
      ISensor? used = Find(sensors, SensorType.Data, "Used Memory");
      ISensor? available = Find(sensors, SensorType.Data, "Available Memory");
      ISensor? virtualLoad = Find(sensors, SensorType.Load, "Virtual Memory");

      model.Subtitle = HasValue(used) && HasValue(available)
        ? "Memory  ·  " + Gigabytes(used!.Value + available!.Value) + " usable"
        : "Memory";
      SetHeroPercent(model, load?.Value, "In use", AtLeast(load?.Value, 80, 90), load);
      model.Metrics.Add(new CardMetric("Used", Gigabytes(used?.Value)));
      model.Metrics.Add(new CardMetric("Available", Gigabytes(available?.Value)));
      model.Metrics.Add(new CardMetric("Virtual memory", Percent(virtualLoad?.Value),
        AtLeast(virtualLoad?.Value, 80, 90), virtualLoad?.Value / 100));
      return model;
    }

    /// <summary>Nominal value of a supply rail from its name, or null.</summary>
    private static float? NominalVoltage(string name) {
      string n = name.Replace(" ", "").ToUpperInvariant();
      if (n.Contains("VBAT"))
        return 3.0f;
      if (n.Contains("12V"))
        return 12f;
      if (n.Contains("3.3V") || n.Contains("3VSB") || n.Contains("3VCC") || n.Contains("AVCC"))
        return 3.3f;
      if (n.Contains("5V"))
        return 5f;
      return null;
    }

    private CardModel BuildMainboard(IHardware board) {
      List<ISensor> sensors = AllSensors(board).ToList();
      CardModel model = new CardModel { Badge = "MB", Title = board.Name };
      string chips = string.Join(", ", board.SubHardware.Select(h => h.Name));
      model.Subtitle = chips.Length > 0 ? "Motherboard  ·  " + chips : "Motherboard";

      List<ISensor> temperatures = sensors
        .Where(s => s.SensorType == SensorType.Temperature && HasValue(s))
        .OrderByDescending(s => s.Value).ToList();
      List<ISensor> fans = sensors
        .Where(s => s.SensorType == SensorType.Fan && HasValue(s)).ToList();
      List<ISensor> voltages = sensors
        .Where(s => s.SensorType == SensorType.Voltage && HasValue(s)).ToList();

      if (temperatures.Count == 0 && fans.Count == 0 && voltages.Count == 0) {
        model.Note = AccessNote("Fans, voltages and board temperatures");
        return model;
      }

      Severity Heat(float? value) => AtLeast(value, 70, 85);
      if (temperatures.Count > 0) {
        ISensor hottest = temperatures[0];
        SetHero(model, hottest.Value, "Hottest  ·  " + hottest.Name,
          Heat(hottest.Value), hottest);
        foreach (ISensor temperature in temperatures.Skip(1).Take(3))
          model.Metrics.Add(new CardMetric(temperature.Name,
            Temperature(temperature.Value), Heat(temperature.Value)));
      } else {
        model.HeroNumber = fans.Count;
        model.HeroLabel = "Fans detected";
      }

      foreach (ISensor fan in fans.Take(6))
        model.Rows.Add(new CardMetric(fan.Name, Rpm(fan.Value)));

      foreach (ISensor voltage in voltages) {
        bool core = Contains(voltage.Name, "VCore");
        float? nominal = NominalVoltage(voltage.Name);
        if (!core && !nominal.HasValue)
          continue;
        Severity severity = Severity.Normal;
        if (nominal.HasValue && voltage.Value.HasValue) {
          float deviation = Math.Abs(voltage.Value.Value - nominal.Value) / nominal.Value;
          if (nominal.Value == 3.0f)
            severity = voltage.Value.Value < 2.6f ? Severity.Hot
              : voltage.Value.Value < 2.8f ? Severity.Warm : Severity.Normal;
          else
            severity = deviation > 0.10f ? Severity.Hot
              : deviation > 0.05f ? Severity.Warm : Severity.Normal;
        }
        model.Rows.Add(new CardMetric(voltage.Name, Volts(voltage.Value), severity));
        if (model.Rows.Count >= 12)
          break;
      }
      return model;
    }

    private CardModel BuildStorage() {
      List<IHardware> drives = computer.Hardware
        .Where(h => h.HardwareType == HardwareType.HDD).ToList();
      CardModel model = new CardModel {
        Badge = "SSD",
        Title = "Storage",
        Subtitle = drives.Count == 1 ? "1 drive" : drives.Count + " drives"
      };

      Severity Heat(float? value) => AtLeast(value, 60, 70);
      ISensor? hottest = null;
      IHardware? hottestDrive = null;

      foreach (IHardware drive in drives) {
        List<ISensor> sensors = AllSensors(drive).ToList();
        ISensor? temperature = sensors.FirstOrDefault(s =>
          s.SensorType == SensorType.Temperature && HasValue(s));
        ISensor? used = Find(sensors, SensorType.Load, "Used Space");
        ISensor? life = Find(sensors, SensorType.Level, "Remaining Life");
        ISensor? spare = Find(sensors, SensorType.Level, "Available Spare");

        if (temperature != null && (hottest == null || temperature.Value > hottest.Value)) {
          hottest = temperature;
          hottestDrive = drive;
        }

        List<string> parts = new List<string>();
        if (HasValue(temperature))
          parts.Add(Temperature(temperature!.Value));
        if (HasValue(life))
          parts.Add(Percent(life!.Value) + " life");
        if (HasValue(used))
          parts.Add(Percent(used!.Value) + " full");

        Severity lifeSeverity = !HasValue(life) ? Severity.Normal
          : life!.Value <= 10 ? Severity.Hot
          : life.Value <= 25 ? Severity.Warm : Severity.Normal;
        Severity spareSeverity = HasValue(spare) && spare!.Value < 10
          ? Severity.Warm : Severity.Normal;
        Severity severity = Worst(Heat(temperature?.Value), lifeSeverity,
          spareSeverity, AtLeast(used?.Value, 85, 95));

        model.Rows.Add(new CardMetric(drive.Name,
          parts.Count > 0 ? string.Join("  ·  ", parts) : "–", severity,
          used?.Value / 100));
      }

      if (hottest != null) {
        SetHero(model, hottest.Value, "Hottest  ·  " + hottestDrive!.Name,
          Heat(hottest.Value), hottest);
      } else {
        model.HeroNumber = drives.Count;
        model.HeroLabel = "Drives";
      }
      return model;
    }

    private CardModel BuildGeneric(IHardware hardware) {
      List<ISensor> sensors = AllSensors(hardware).ToList();
      CardModel model = new CardModel {
        Badge = hardware.HardwareType.ToString().Substring(0, 2).ToUpperInvariant(),
        Title = hardware.Name,
        Subtitle = hardware.HardwareType.ToString()
      };
      ISensor? temperature = sensors.FirstOrDefault(s =>
        s.SensorType == SensorType.Temperature && HasValue(s));
      if (temperature != null)
        SetHero(model, temperature.Value, temperature.Name, Severity.Normal, temperature);
      foreach (ISensor fan in sensors.Where(s => s.SensorType == SensorType.Fan && HasValue(s)).Take(6))
        model.Rows.Add(new CardMetric(fan.Name, Rpm(fan.Value)));
      return model;
    }
  }

  /// <summary>Title, access state and the two actions above the cards.</summary>
  internal sealed class OverviewHeader : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private const string DetailsText = "More info";
    private const string ExportText = "Export for AI";
    private const float EntranceSeconds = 0.55f;

    private Theme theme = Theme.Current;
    private Rectangle detailsBounds, exportBounds;
    private readonly AnimatedValue detailsHover, exportHover;
    private int pressed; // 0 none, 1 details, 2 export
    private double entranceStart = double.NaN;
    private int fontDpi;
    private Font? titleFont, subtitleFont, chipFont, buttonFont;
    private bool showExport;

    public OverviewHeader() {
      SetStyle(ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
        ControlStyles.ResizeRedraw, true);
      detailsHover = new AnimatedValue(this, 0.18f);
      detailsHover.Set(0);
      exportHover = new AnimatedValue(this, 0.18f);
      exportHover.Set(0);
    }

    public event EventHandler? DetailsClicked;
    public event EventHandler? ExportClicked;

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Chip { get; set; } = "";
    public Severity ChipSeverity { get; set; }

    public bool ShowExport {
      get { return showExport; }
      set {
        showExport = value;
        Invalidate();
      }
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        Invalidate();
      }
    }

    public void PlayEntrance() {
      if (!Animator.Enabled)
        return;
      entranceStart = Animator.Now;
      Animator.Run(() => {
        if (IsDisposed)
          return false;
        Invalidate();
        return EntranceProgress < 1;
      });
    }

    private float EntranceProgress {
      get {
        return double.IsNaN(entranceStart) ? 1
          : Easing.OutQuint((float)((Animator.Now - entranceStart) / EntranceSeconds));
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      foreach (Font? font in new[] { titleFont, subtitleFont, chipFont, buttonFont })
        font?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.DisplayFamily, 17f, FontStyle.Regular, DeviceDpi);
      subtitleFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
      chipFont = Theme.CreateFont(Theme.SemiboldFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      buttonFont = Theme.CreateFont(Theme.SemiboldFamily, 9.5f, FontStyle.Regular, DeviceDpi);
      int height = (int)Math.Ceiling(S(22) + titleFont.Height + S(6) +
        Math.Max(subtitleFont.Height, chipFont.Height + S(6)) + S(12));
      if (Height != height)
        Height = height;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      EnsureFonts();
      Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      EnsureFonts();
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        foreach (Font? font in new[] { titleFont, subtitleFont, chipFont, buttonFont })
          font?.Dispose();
      base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      float enter = EntranceProgress;
      float margin = S(24);
      float y = S(22) + (1 - enter) * S(10);

      TextRenderer.DrawText(g, Title, titleFont!, new Point((int)margin, (int)y),
        theme.Text, Flags);
      float rowY = y + titleFont!.Height + S(6);

      Size subtitleSize = TextRenderer.MeasureText(Subtitle, subtitleFont!, Size.Empty, Flags);
      float rowHeight = Math.Max(subtitleSize.Height, chipFont!.Height + S(6));
      TextRenderer.DrawText(g, Subtitle, subtitleFont!,
        new Point((int)margin, (int)(rowY + (rowHeight - subtitleSize.Height) / 2)),
        theme.TextSecondary, Flags);

      if (!string.IsNullOrEmpty(Chip)) {
        Size chipText = TextRenderer.MeasureText(Chip, chipFont, Size.Empty, Flags);
        float dot = S(7);
        RectangleF chip = new RectangleF(margin + subtitleSize.Width + S(12), rowY,
          S(10) + dot + S(6) + chipText.Width + S(10), chipFont.Height + S(6));
        Color tint = ChipSeverity == Severity.Normal ? theme.Good : theme.Warm;
        using (GraphicsPath path = Theme.RoundedRect(chip, chip.Height / 2))
        using (SolidBrush fill = new SolidBrush(
          Theme.Blend(theme.Background, tint, theme.IsDark ? 0.18f : 0.12f)))
          g.FillPath(fill, path);

        float dotX = chip.Left + S(10), dotY = chip.Top + (chip.Height - dot) / 2;
        // Live indicator: a slow, soft breathing halo around the status dot.
        float breath = (float)(0.5 + 0.5 * Math.Sin(Animator.Now * 2.2));
        if (Animator.Enabled) {
          float halo = S(2.5f) * breath;
          using (SolidBrush glow = new SolidBrush(Color.FromArgb(
            (int)Math.Round(70 * (1 - breath * 0.6f)), tint)))
            g.FillEllipse(glow, dotX - halo, dotY - halo, dot + 2 * halo, dot + 2 * halo);
        }
        using (SolidBrush dotBrush = new SolidBrush(tint))
          g.FillEllipse(dotBrush, dotX, dotY, dot, dot);
        TextRenderer.DrawText(g, Chip, chipFont,
          new Point((int)(dotX + dot + S(6)),
            (int)(chip.Top + (chip.Height - chipText.Height) / 2)),
          Theme.Blend(tint, theme.Text, theme.IsDark ? 0.35f : 0.25f), Flags);
      }

      // Buttons, right-aligned and centred on the title block.
      float right = Width - margin;
      float buttonHeight = buttonFont!.Height + S(16);
      float buttonY = S(22) + (titleFont.Height + S(6) + rowHeight - buttonHeight) / 2 +
        (1 - enter) * S(10);

      exportBounds = Rectangle.Empty;
      if (showExport) {
        Size exportText = TextRenderer.MeasureText(ExportText, buttonFont, Size.Empty, Flags);
        RectangleF export = new RectangleF(right - exportText.Width - S(32), buttonY,
          exportText.Width + S(32), buttonHeight);
        float h = exportHover.Value;
        if (pressed == 2)
          export.Inflate(-S(1), -S(1));
        Color fill = ColorMath.Lerp(theme.Accent,
          Theme.Blend(theme.Accent, theme.IsDark ? Color.White : Color.Black, 0.12f), h);
        // Soft accent glow under the primary action on hover.
        if (h > 0.01f) {
          RectangleF glow = RectangleF.Inflate(export, S(3) * h, S(3) * h);
          glow.Offset(0, S(2) * h);
          using (GraphicsPath path = Theme.RoundedRect(glow, S(10)))
          using (SolidBrush brush = new SolidBrush(Color.FromArgb(
            (int)Math.Round((theme.IsDark ? 60 : 45) * h), theme.Accent)))
            g.FillPath(brush, path);
        }
        using (GraphicsPath path = Theme.RoundedRect(export, S(8)))
        using (SolidBrush brush = new SolidBrush(fill))
          g.FillPath(brush, path);
        TextRenderer.DrawText(g, ExportText, buttonFont, Rectangle.Round(export),
          theme.IsDark ? Color.FromArgb(0x0B, 0x14, 0x24) : Color.White,
          Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        exportBounds = Rectangle.Round(export);
        right = export.Left - S(10);
      }

      Size detailsText = TextRenderer.MeasureText(DetailsText, buttonFont, Size.Empty, Flags);
      RectangleF details = new RectangleF(right - detailsText.Width - S(32), buttonY,
        detailsText.Width + S(32), buttonHeight);
      if (pressed == 1)
        details.Inflate(-S(1), -S(1));
      float dh = detailsHover.Value;
      using (GraphicsPath path = Theme.RoundedRect(details, S(8)))
      using (SolidBrush brush = new SolidBrush(
        ColorMath.Lerp(theme.Surface, theme.SurfaceSunken, dh)))
      using (Pen pen = new Pen(ColorMath.Lerp(theme.Border,
        Theme.Blend(theme.Border, theme.Text, 0.18f), dh), Math.Max(1f, S(1)))) {
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
      }
      TextRenderer.DrawText(g, DetailsText, buttonFont, Rectangle.Round(details), theme.Text,
        Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      detailsBounds = Rectangle.Round(details);

      if (enter < 1) {
        using (SolidBrush veil = new SolidBrush(
          Color.FromArgb((int)Math.Round((1 - enter) * 255), theme.Background)))
          g.FillRectangle(veil, ClientRectangle);
      }
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      bool overDetails = detailsBounds.Contains(e.Location);
      bool overExport = exportBounds.Contains(e.Location);
      detailsHover.Set(overDetails ? 1 : 0);
      exportHover.Set(overExport ? 1 : 0);
      Cursor = overDetails || overExport ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      detailsHover.Set(0);
      exportHover.Set(0);
      pressed = 0;
      Cursor = Cursors.Default;
      Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (e.Button != MouseButtons.Left)
        return;
      pressed = detailsBounds.Contains(e.Location) ? 1
        : exportBounds.Contains(e.Location) ? 2 : 0;
      Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left)
        return;
      int was = pressed;
      pressed = 0;
      Invalidate();
      if (was == 1 && detailsBounds.Contains(e.Location))
        DetailsClicked?.Invoke(this, EventArgs.Empty);
      else if (was == 2 && exportBounds.Contains(e.Location))
        ExportClicked?.Invoke(this, EventArgs.Empty);
    }
  }
}
