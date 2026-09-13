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
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;
using OpenHardwareMonitor.Hardware.Alerts;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// Alert values in the units the interface shows. Rules store canonical
  /// units; temperatures are shown and typed in °C or °F as chosen.
  /// </summary>
  internal static class AlertValueFormat {

    public static string Unit(SensorType type, TemperatureUnit unit) {
      switch (type) {
        case SensorType.Voltage: return "V";
        case SensorType.Clock: return "MHz";
        case SensorType.Temperature: return unit == TemperatureUnit.Fahrenheit ? "°F" : "°C";
        case SensorType.Load:
        case SensorType.Control:
        case SensorType.Level: return "%";
        case SensorType.Fan: return "RPM";
        case SensorType.Flow: return "L/h";
        case SensorType.Power: return "W";
        case SensorType.Data: return "GB";
        case SensorType.SmallData: return "MB";
        case SensorType.Throughput: return "MB/s";
        default: return "";
      }
    }

    private static bool IsFahrenheit(SensorType type, TemperatureUnit unit) {
      return type == SensorType.Temperature && unit == TemperatureUnit.Fahrenheit;
    }

    public static float ToDisplay(float value, SensorType type, TemperatureUnit unit) {
      return IsFahrenheit(type, unit) ? value * 1.8f + 32 : value;
    }

    public static float FromDisplay(float value, SensorType type, TemperatureUnit unit) {
      return IsFahrenheit(type, unit) ? (value - 32) / 1.8f : value;
    }

    /// <summary>The number alone, in the display unit, as typed in the rule editor.</summary>
    public static string Number(float displayValue, SensorType type) {
      string format;
      switch (type) {
        case SensorType.Voltage: format = "0.00#"; break;
        case SensorType.Fan:
        case SensorType.Clock:
        case SensorType.SmallData: format = "0"; break;
        case SensorType.Factor:
        case SensorType.Data:
        case SensorType.Throughput: format = "0.##"; break;
        default: format = "0.#"; break;
      }
      return displayValue.ToString(format, CultureInfo.CurrentCulture);
    }

    /// <summary>A canonical value with its display unit, for example "81 °F".</summary>
    public static string Format(float value, SensorType type, TemperatureUnit unit) {
      string unitText = Unit(type, unit);
      string number = Number(ToDisplay(value, type, unit), type);
      return unitText.Length == 0 ? number : number + " " + unitText;
    }

    public static string TemperatureDifference(float celsius, TemperatureUnit unit) {
      float value = unit == TemperatureUnit.Fahrenheit ? celsius * 1.8f : celsius;
      return value.ToString("0.#", CultureInfo.CurrentCulture) + " " +
        Unit(SensorType.Temperature, unit);
    }

    public static string Duration(int seconds) {
      CultureInfo c = CultureInfo.CurrentCulture;
      if (seconds < 60)
        return seconds.ToString(c) + " s";
      if (seconds % 60 == 0)
        return (seconds / 60).ToString(c) + " min";
      return (seconds / 60).ToString(c) + " min " + (seconds % 60).ToString(c) + " s";
    }

    /// <summary>The time of day for today, date and time otherwise.</summary>
    public static string Time(DateTimeOffset time) {
      DateTime local = time.LocalDateTime;
      return local.Date == DateTime.Today
        ? local.ToString("T", CultureInfo.CurrentCulture)
        : local.ToString("g", CultureInfo.CurrentCulture);
    }
  }

  /// <summary>A sensor offered in the rule editor, copied under the hardware lock.</summary>
  internal sealed class AlertSensorChoice {

    private AlertSensorChoice(ISensor sensor) {
      Identifier = sensor.Identifier.ToString();
      Name = sensor.Name;
      HardwareIdentifier = sensor.Hardware.Identifier.ToString();
      HardwareName = sensor.Hardware.Name;
      Type = sensor.SensorType;
      Value = sensor.Value;
    }

    public string Identifier { get; }
    public string Name { get; }
    public string HardwareIdentifier { get; }
    public string HardwareName { get; }
    public SensorType Type { get; }
    public float? Value { get; }

    /// <summary>Every sensor, grouped by hardware in tree order. Call under the hardware lock.</summary>
    public static List<AlertSensorChoice> Collect(IComputer computer) {
      List<AlertSensorChoice> choices = new List<AlertSensorChoice>();
      foreach (IHardware hardware in computer.Hardware)
        Add(hardware, choices);
      return choices;
    }

    private static void Add(IHardware hardware, List<AlertSensorChoice> choices) {
      foreach (ISensor sensor in hardware.Sensors)
        choices.Add(new AlertSensorChoice(sensor));
      foreach (IHardware sub in hardware.SubHardware)
        Add(sub, choices);
    }
  }

  /// <summary>Lists, adds and removes custom alert rules.</summary>
  internal sealed class AlertRulesDialog : ModernDialog {

    private readonly AlertEngine engine;
    private readonly UnitManager unitManager;
    private readonly Dictionary<string, AlertSensorChoice> sensorsById =
      new Dictionary<string, AlertSensorChoice>(StringComparer.Ordinal);
    private readonly Panel scroller;
    private readonly AddAlertRuleCard editor;
    private SettingsSection? list;

    public AlertRulesDialog(Theme theme, AlertEngine engine,
      IReadOnlyList<AlertSensorChoice> sensors, UnitManager unitManager)
      : base(theme, "Custom alerts",
        "Get an alert when a sensor stays above or below a value. Rules are checked after every sensor update.") {
      this.engine = engine;
      this.unitManager = unitManager;
      foreach (AlertSensorChoice sensor in sensors)
        sensorsById[sensor.Identifier] = sensor;

      scroller = new BufferedScrollPanel { AutoScroll = true, BackColor = theme.Background };
      scroller.Resize += delegate { LayoutContent(); };
      editor = new AddAlertRuleCard(theme, sensors, unitManager);
      editor.RuleCreated += delegate(object? sender, CustomAlertRule rule) {
        engine.AddCustomRule(rule);
        RebuildList();
      };
      editor.HeightChanged += delegate { LayoutContent(); };
      scroller.Controls.Add(editor);
      SetContent(scroller);

      AddButton("Done", ButtonKind.Primary, Close);
      ClientSize = new Size(Px(660), Px(720));
      MinimumSize = new Size(Px(520), Px(480));
      RebuildList();
    }

    private void RebuildList() {
      scroller.SuspendLayout();
      if (list != null) {
        scroller.Controls.Remove(list);
        list.Dispose();
      }
      IReadOnlyList<CustomAlertRule> rules = engine.CustomRules;
      list = new SettingsSection("Your rules",
        rules.Count == 0 ? "No custom rules yet. Add one below." : null) { Theme = UiTheme };
      foreach (CustomAlertRule rule in rules) {
        string id = rule.Id;
        // Rebuilding disposes the button, so it must not happen inside its own click.
        list.AddButton(Title(rule), Detail(rule), "Remove",
          () => BeginInvoke((Action)(() => {
            engine.RemoveCustomRule(id);
            RebuildList();
          })));
      }
      scroller.Controls.Add(list);
      scroller.ResumeLayout(false);
      LayoutContent();
    }

    private string Title(CustomAlertRule rule) {
      string direction = rule.Direction == AlertDirection.Above ? " above " : " below ";
      if (sensorsById.TryGetValue(rule.SensorIdentifier, out AlertSensorChoice? sensor))
        return sensor.Name + direction +
          AlertValueFormat.Format(rule.Threshold, sensor.Type, unitManager.TemperatureUnit);
      return "Sensor not found" + direction +
        rule.Threshold.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private string Detail(CustomAlertRule rule) {
      string duration = rule.DurationSeconds == 0
        ? "Alerts right away"
        : "For " + AlertValueFormat.Duration(rule.DurationSeconds);
      return (sensorsById.TryGetValue(rule.SensorIdentifier, out AlertSensorChoice? sensor)
        ? sensor.HardwareName : rule.SensorIdentifier) + "  ·  " + duration;
    }

    private void LayoutContent() {
      if (list == null)
        return;
      int margin = Px(24), gap = Px(16);
      int width = Math.Max(Px(360), scroller.ClientSize.Width - 2 * margin);
      Point scroll = scroller.AutoScrollPosition;
      int y = Px(4);
      int listHeight = list.MeasureHeight(width);
      list.Bounds = new Rectangle(margin + scroll.X, y + scroll.Y, width, listHeight);
      y += listHeight + gap;
      editor.Bounds = new Rectangle(margin + scroll.X, y + scroll.Y, width,
        editor.MeasureHeight());
      scroller.AutoScrollMargin = new Size(0, margin);
    }
  }

  /// <summary>The card that creates a rule: sensor, above or below, value and duration.</summary>
  internal sealed class AddAlertRuleCard : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private static readonly string[] Labels = { "Sensor", "When", "Value", "For" };
    private static readonly int[] DurationSeconds = { 0, 10, 30, 60, 300 };

    private sealed class GroupItem {
      public GroupItem(string name) {
        Name = name;
      }

      public string Name { get; }

      public override string ToString() {
        return Name;
      }
    }

    private sealed class SensorItem {
      public SensorItem(AlertSensorChoice choice) {
        Choice = choice;
      }

      public AlertSensorChoice Choice { get; }

      public override string ToString() {
        return Choice.HardwareName + " · " + Choice.Name;
      }
    }

    private readonly Theme theme;
    private readonly UnitManager unitManager;
    private readonly ComboBox sensorBox;
    private readonly SegmentedControl directionBox;
    private readonly TextBox valueBox;
    private readonly SegmentedControl durationBox;
    private readonly ModernButton addButton;
    private int fontDpi;
    private Font? titleFont, labelFont;
    private int lastSensorIndex = -1;
    private string? error;
    private Rectangle[] labelBounds = Array.Empty<Rectangle>();
    private Rectangle valueFrame, errorBounds;

    public AddAlertRuleCard(Theme theme, IReadOnlyList<AlertSensorChoice> sensors,
      UnitManager unitManager) {
      this.theme = theme;
      this.unitManager = unitManager;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
      BackColor = theme.Background;

      sensorBox = new ComboBox {
        DropDownStyle = ComboBoxStyle.DropDownList,
        DrawMode = DrawMode.OwnerDrawFixed,
        FlatStyle = FlatStyle.Flat,
        BackColor = theme.SurfaceSunken,
        ForeColor = theme.Text,
        IntegralHeight = false,
        MaxDropDownItems = 18,
        AccessibleName = "Sensor"
      };
      string? group = null;
      foreach (AlertSensorChoice choice in sensors) {
        if (choice.HardwareIdentifier != group) {
          group = choice.HardwareIdentifier;
          sensorBox.Items.Add(new GroupItem(choice.HardwareName));
        }
        sensorBox.Items.Add(new SensorItem(choice));
      }
      sensorBox.DrawItem += DrawSensorItem;
      sensorBox.SelectedIndexChanged += delegate { OnSensorChanged(); };

      directionBox = new SegmentedControl {
        Items = new[] { "Above", "Below" },
        Theme = theme,
        SurfaceColor = theme.Surface,
        AccessibleName = "When"
      };
      directionBox.SelectedIndex = 0;

      valueBox = new TextBox {
        BorderStyle = BorderStyle.None,
        BackColor = theme.SurfaceSunken,
        ForeColor = theme.Text,
        AccessibleName = "Value"
      };
      valueBox.TextChanged += delegate { SetError(null); };
      valueBox.KeyDown += delegate(object? sender, KeyEventArgs e) {
        if (e.KeyCode == Keys.Enter) {
          e.SuppressKeyPress = true;
          CreateRule();
        }
      };

      durationBox = new SegmentedControl {
        Items = new[] { "Right away", "10 s", "30 s", "1 min", "5 min" },
        Theme = theme,
        SurfaceColor = theme.Surface,
        AccessibleName = "For"
      };
      durationBox.SelectedIndex = 2;

      addButton = new ModernButton {
        Text = "Add rule",
        Kind = ButtonKind.Primary,
        Theme = theme,
        SurfaceColor = theme.Surface
      };
      addButton.Click += delegate { CreateRule(); };

      Controls.Add(sensorBox);
      Controls.Add(directionBox);
      Controls.Add(valueBox);
      Controls.Add(durationBox);
      Controls.Add(addButton);

      for (int i = 0; i < sensorBox.Items.Count; i++) {
        if (sensorBox.Items[i] is SensorItem) {
          sensorBox.SelectedIndex = i;
          break;
        }
      }
      if (sensorBox.Items.Count == 0) {
        sensorBox.Enabled = false;
        addButton.Enabled = false;
        error = "No sensors have been detected yet.";
      }
    }

    public event EventHandler<CustomAlertRule>? RuleCreated;

    /// <summary>Raised when a validation message appears or goes away.</summary>
    public event EventHandler? HeightChanged;

    private int Px(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      titleFont?.Dispose();
      labelFont?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 12f, FontStyle.Regular, DeviceDpi);
      labelFont = Theme.CreateFont(Theme.TextFamily, 10f, FontStyle.Regular, DeviceDpi);
      sensorBox.ItemHeight = Px(26);
    }

    private string SelectedUnit {
      get {
        return sensorBox.SelectedItem is SensorItem item
          ? AlertValueFormat.Unit(item.Choice.Type, unitManager.TemperatureUnit) : "";
      }
    }

    // ---- behaviour ------------------------------------------------------------------

    private void OnSensorChanged() {
      int index = sensorBox.SelectedIndex;
      if (index >= 0 && sensorBox.Items[index] is GroupItem) {
        // Hardware headings are not choices; step past them in the direction of travel.
        int next = index < lastSensorIndex && index > 0 ? index - 1 : index + 1;
        if (next < sensorBox.Items.Count && sensorBox.Items[next] is SensorItem)
          sensorBox.SelectedIndex = next;
        else if (lastSensorIndex >= 0)
          sensorBox.SelectedIndex = lastSensorIndex;
        return;
      }
      lastSensorIndex = index;
      if (!(sensorBox.SelectedItem is SensorItem item))
        return;

      AlertSensorChoice choice = item.Choice;
      // Fans and levels (spare, remaining life) are usually a worry when they drop.
      directionBox.SelectedIndex =
        choice.Type == SensorType.Fan || choice.Type == SensorType.Level ? 1 : 0;
      valueBox.Text = choice.Value.HasValue
        ? AlertValueFormat.Number(AlertValueFormat.ToDisplay(choice.Value.Value, choice.Type,
          unitManager.TemperatureUnit), choice.Type)
        : "";
      SetError(null);
      Invalidate();
    }

    private void CreateRule() {
      if (!(sensorBox.SelectedItem is SensorItem item)) {
        SetError("Choose a sensor.");
        return;
      }
      if (!float.TryParse(valueBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture,
        out float display) || !float.IsFinite(display)) {
        SetError("Enter a number for the value.");
        valueBox.Focus();
        return;
      }

      CustomAlertRule rule;
      try {
        rule = new CustomAlertRule(item.Choice.Identifier,
          directionBox.SelectedIndex == 1 ? AlertDirection.Below : AlertDirection.Above,
          AlertValueFormat.FromDisplay(display, item.Choice.Type, unitManager.TemperatureUnit),
          DurationSeconds[Math.Max(0, durationBox.SelectedIndex)]);
      } catch (ArgumentException) {
        SetError("This sensor cannot be used for an alert.");
        return;
      }
      SetError(null);
      RuleCreated?.Invoke(this, rule);
    }

    private void SetError(string? message) {
      if (error == message)
        return;
      error = message;
      PerformLayout();
      Invalidate();
      HeightChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- layout and painting ----------------------------------------------------------

    public int MeasureHeight() {
      EnsureFonts();
      int height = Px(22) + titleFont!.Height + Px(12) + Labels.Length * Px(48);
      if (error != null)
        height += labelFont!.Height + Px(8);
      return height + Px(10) + addButton.GetPreferredSize(Size.Empty).Height + Px(22);
    }

    protected override void OnLayout(LayoutEventArgs levent) {
      base.OnLayout(levent);
      EnsureFonts();
      int pad = Px(22);
      int labelWidth = Px(96);
      int x = pad + labelWidth;
      int right = Width - pad;
      int row = Px(48);
      int y = pad + titleFont!.Height + Px(12);

      labelBounds = new Rectangle[Labels.Length];
      for (int i = 0; i < Labels.Length; i++)
        labelBounds[i] = new Rectangle(pad, y + i * row, labelWidth, row);

      sensorBox.Width = Math.Max(Px(160), right - x);
      sensorBox.Location = new Point(x, y + (row - sensorBox.Height) / 2);

      Size direction = directionBox.GetPreferredSize(Size.Empty);
      directionBox.Bounds = new Rectangle(x, y + row + (row - direction.Height) / 2,
        direction.Width, direction.Height);

      int frameHeight = Px(34);
      valueFrame = new Rectangle(x, y + 2 * row + (row - frameHeight) / 2, Px(140), frameHeight);
      int textHeight = valueBox.PreferredHeight;
      valueBox.Bounds = new Rectangle(valueFrame.X + Px(10),
        valueFrame.Y + (frameHeight - textHeight) / 2, valueFrame.Width - Px(20), textHeight);

      Size duration = durationBox.GetPreferredSize(Size.Empty);
      durationBox.Bounds = new Rectangle(x, y + 3 * row + (row - duration.Height) / 2,
        duration.Width, duration.Height);

      y += Labels.Length * row;
      errorBounds = Rectangle.Empty;
      if (error != null) {
        errorBounds = new Rectangle(x, y, Math.Max(0, right - x), labelFont!.Height);
        y += labelFont.Height + Px(8);
      }
      y += Px(10);
      Size add = addButton.GetPreferredSize(Size.Empty);
      addButton.Bounds = new Rectangle(x, y, add.Width, add.Height);
    }

    private void DrawSensorItem(object? sender, DrawItemEventArgs e) {
      if (e.Index < 0 || e.Index >= sensorBox.Items.Count)
        return;
      EnsureFonts();
      Graphics g = e.Graphics;
      object? item = sensorBox.Items[e.Index];
      bool editPortion = (e.State & DrawItemState.ComboBoxEdit) != 0;
      bool selected = (e.State & DrawItemState.Selected) != 0 && item is SensorItem;
      using (SolidBrush brush = new SolidBrush(selected && !editPortion
        ? theme.Selection : theme.SurfaceSunken))
        g.FillRectangle(brush, e.Bounds);

      Font font = e.Font ?? sensorBox.Font;
      if (item is GroupItem heading) {
        Rectangle bounds = new Rectangle(e.Bounds.X + Px(8), e.Bounds.Y,
          e.Bounds.Width - Px(16), e.Bounds.Height);
        using (Font bold = new Font(font, FontStyle.Bold))
          TextRenderer.DrawText(g, heading.Name, bold, bounds, theme.TextSecondary,
            Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        return;
      }
      if (!(item is SensorItem sensor))
        return;

      AlertSensorChoice choice = sensor.Choice;
      int indent = editPortion ? Px(6) : Px(22);
      Rectangle text = new Rectangle(e.Bounds.X + indent, e.Bounds.Y,
        Math.Max(0, e.Bounds.Width - indent - Px(8)), e.Bounds.Height);
      string value = choice.Value.HasValue
        ? AlertValueFormat.Format(choice.Value.Value, choice.Type, unitManager.TemperatureUnit)
        : "";
      if (value.Length > 0) {
        Size valueSize = TextRenderer.MeasureText(value, font, Size.Empty, Flags);
        TextRenderer.DrawText(g, value, font, text, theme.TextTertiary,
          Flags | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        text.Width = Math.Max(0, text.Width - valueSize.Width - Px(12));
      }
      TextRenderer.DrawText(g, editPortion ? choice.HardwareName + "  ·  " + choice.Name : choice.Name,
        font, text, theme.Text,
        Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      using (GraphicsPath path = Theme.RoundedRect(card, Px(14)))
      using (SolidBrush surface = new SolidBrush(theme.Surface))
      using (Pen border = new Pen(theme.Border, Math.Max(1f, DeviceDpi / 96f))) {
        g.FillPath(surface, path);
        g.DrawPath(border, path);
      }

      int pad = Px(22);
      TextRenderer.DrawText(g, "Add a rule", titleFont!, new Point(pad, pad), theme.Text, Flags);
      for (int i = 0; i < labelBounds.Length; i++)
        TextRenderer.DrawText(g, Labels[i], labelFont!, labelBounds[i], theme.TextSecondary,
          Flags | TextFormatFlags.VerticalCenter);

      if (!valueFrame.IsEmpty) {
        using (GraphicsPath path = Theme.RoundedRect(valueFrame, Px(8)))
        using (SolidBrush brush = new SolidBrush(theme.SurfaceSunken))
          g.FillPath(brush, path);
        string unit = SelectedUnit;
        if (unit.Length > 0)
          TextRenderer.DrawText(g, unit, labelFont!,
            new Rectangle(valueFrame.Right + Px(10), valueFrame.Y, Px(80), valueFrame.Height),
            theme.TextSecondary, Flags | TextFormatFlags.VerticalCenter);
      }

      if (error != null && !errorBounds.IsEmpty)
        TextRenderer.DrawText(g, error, labelFont!, errorBounds, theme.Hot,
          Flags | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        titleFont?.Dispose();
        labelFont?.Dispose();
      }
      base.Dispose(disposing);
    }
  }
}
