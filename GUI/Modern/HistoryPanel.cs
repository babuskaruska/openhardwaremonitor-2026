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
using System.Linq;
using System.Windows.Forms;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// The history page: one sensor over the last 10 minutes to 24 hours,
  /// optionally compared with another sensor of the same type. Opened from a
  /// sensor in the list or from an overview card's trend; Back or Esc returns
  /// to where it was opened.
  /// </summary>
  public sealed class HistoryPanel : UserControl {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private const string Hint = "Line: average  ·  Band: lowest to highest  ·  Esc goes back";

    private static readonly TimeSpan[] Ranges = {
      TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)
    };

    private readonly IComputer computer;
    private readonly object sync;
    private readonly UnitManager unitManager;
    private readonly SegmentedControl rangeSelector;
    private readonly ModernButton compareButton;
    private readonly ModernButton sensorButton;
    private readonly HistoryChart chart;
    private readonly AnimatedValue backHover;

    private Theme theme = Theme.Current;
    private ISensor? sensor;
    private ISensor? compare;
    private bool available = true;
    private volatile bool hardwareChanged;
    private string backText = "Overview";
    private Rectangle backBounds;
    private bool backPressed;
    private int fontDpi;
    private Font? buttonFont, titleFont, subtitleFont, hintFont;

    /// <param name="sync">The hardware lock, taken to copy history outside
    /// the refresh after a sensor update.</param>
    public HistoryPanel(IComputer computer, object sync, UnitManager unitManager) {
      this.computer = computer;
      this.sync = sync;
      this.unitManager = unitManager;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

      backHover = new AnimatedValue(this, 0.18f);
      backHover.Set(0);

      rangeSelector = new SegmentedControl {
        Items = new[] { "10 min", "1 h", "6 h", "24 h" },
        SelectedIndex = 1,
        AccessibleName = "Time range"
      };
      compareButton = new ModernButton { Text = "Compare", Kind = ButtonKind.Secondary };
      sensorButton = new ModernButton { Text = "Change sensor", Kind = ButtonKind.Secondary };
      chart = new HistoryChart { Range = Ranges[1] };

      rangeSelector.SelectedIndexChanged += delegate {
        chart.Range = Ranges[rangeSelector.SelectedIndex];
        CopySamplesLocked();
        RangeIndexChanged?.Invoke(this, EventArgs.Empty);
      };
      compareButton.Click += delegate { ShowCompareMenu(); };
      sensorButton.Click += delegate { ShowSensorMenu(); };

      Controls.Add(chart);
      Controls.Add(rangeSelector);
      Controls.Add(compareButton);
      Controls.Add(sensorButton);

      computer.HardwareAdded += OnHardwareChanged;
      computer.HardwareRemoved += OnHardwareChanged;
      ApplyTheme();
    }

    public event EventHandler? BackClicked;
    public event EventHandler? RangeIndexChanged;
    public event EventHandler? SensorChanged;

    /// <summary>Label of the back button: the page the history was opened from.</summary>
    public string BackText {
      get { return backText; }
      set {
        backText = value ?? "";
        Invalidate();
      }
    }

    /// <summary>0: 10 minutes, 1: 1 hour, 2: 6 hours, 3: 24 hours.</summary>
    public int RangeIndex {
      get { return rangeSelector.SelectedIndex; }
      set { rangeSelector.SelectedIndex = Math.Max(0, Math.Min(Ranges.Length - 1, value)); }
    }

    public ISensor? Sensor {
      get { return sensor; }
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        ApplyTheme();
      }
    }

    private void ApplyTheme() {
      BackColor = theme.Background;
      rangeSelector.Theme = theme;
      compareButton.Theme = theme;
      sensorButton.Theme = theme;
      chart.Theme = theme;
      Invalidate(true);
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private void OnHardwareChanged(IHardware hardware) {
      // Raised on the sensor thread; sensors are looked up again on the next refresh.
      hardwareChanged = true;
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        computer.HardwareAdded -= OnHardwareChanged;
        computer.HardwareRemoved -= OnHardwareChanged;
        foreach (Font? font in new[] { buttonFont, titleFont, subtitleFont, hintFont })
          font?.Dispose();
      }
      base.Dispose(disposing);
    }

    // ---- data -----------------------------------------------------------------------

    /// <summary>Shows a sensor, or the empty state for null. Takes the hardware lock.</summary>
    public void ShowSensor(ISensor? value) {
      if (compare != null && (value == null || compare == value ||
        compare.SensorType != value.SensorType))
        compare = null;
      sensor = value;
      available = true;
      CopySamplesLocked();
      Invalidate();
      SensorChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Copies the latest history into the chart. Call after each sensor update,
    /// in the refresh on the UI thread while the sensor thread waits.
    /// </summary>
    public void UpdateValues() {
      if (hardwareChanged)
        Resolve();
      CopySamples();
    }

    private void CopySamplesLocked() {
      lock (sync)
        CopySamples();
    }

    private void CopySamples() {
      if (sensor == null) {
        chart.CopySamples(0, null, "");
        chart.CopySamples(1, null, "");
        return;
      }
      chart.Unit = HistoryUnit.For(sensor.SensorType,
        unitManager.TemperatureUnit == TemperatureUnit.Fahrenheit);
      bool acrossHardware = compare != null && compare.Hardware != sensor.Hardware;
      chart.CopySamples(0, sensor, SeriesName(sensor, acrossHardware));
      chart.CopySamples(1, compare, compare != null ? SeriesName(compare, acrossHardware) : "");
    }

    private static string SeriesName(ISensor item, bool withHardware) {
      return withHardware ? item.Name + "  ·  " + item.Hardware.Name : item.Name;
    }

    /// <summary>
    /// Rescanning or switching hardware on and off replaces the sensor objects;
    /// follow the shown sensors to their replacements by identifier.
    /// </summary>
    private void Resolve() {
      hardwareChanged = false;
      string? sensorId = sensor?.Identifier.ToString();
      string? compareId = compare?.Identifier.ToString();
      ISensor? foundSensor = null, foundCompare = null;
      computer.Accept(new SensorVisitor(delegate(ISensor candidate) {
        string id = candidate.Identifier.ToString();
        if (id == sensorId)
          foundSensor = candidate;
        else if (id == compareId)
          foundCompare = candidate;
      }));
      if (foundSensor != null)
        sensor = foundSensor;
      if (foundCompare != null)
        compare = foundCompare;
      bool nowAvailable = sensor == null || foundSensor != null;
      if (nowAvailable != available) {
        available = nowAvailable;
        Invalidate();
      }
    }

    // ---- menus ------------------------------------------------------------------------

    private List<ISensor> AllSensors() {
      List<ISensor> result = new List<ISensor>();
      lock (sync) {
        foreach (IHardware hardware in computer.Hardware)
          Collect(hardware, result);
      }
      return result;
    }

    private static void Collect(IHardware hardware, List<ISensor> result) {
      result.AddRange(hardware.Sensors);
      foreach (IHardware sub in hardware.SubHardware)
        Collect(sub, result);
    }

    private static string TypeName(SensorType type, bool plural) {
      switch (type) {
        case SensorType.Voltage: return plural ? "Voltages" : "Voltage";
        case SensorType.Clock: return plural ? "Clock speeds" : "Clock speed";
        case SensorType.Temperature: return plural ? "Temperatures" : "Temperature";
        case SensorType.Load: return plural ? "Loads" : "Load";
        case SensorType.Fan: return plural ? "Fan speeds" : "Fan speed";
        case SensorType.Flow: return plural ? "Flow rates" : "Flow rate";
        case SensorType.Control: return plural ? "Fan outputs" : "Fan output";
        case SensorType.Level: return plural ? "Levels" : "Level";
        case SensorType.Factor: return plural ? "Factors" : "Factor";
        case SensorType.Power: return "Power";
        case SensorType.Data: return "Data";
        case SensorType.SmallData: return plural ? "Memory" : "Memory";
        case SensorType.Throughput: return "Throughput";
        default: return type.ToString();
      }
    }

    private void ShowSensorMenu() {
      ContextMenuStrip menu = new ContextMenuStrip();
      foreach (IGrouping<IHardware, ISensor> hardware in AllSensors().GroupBy(s => s.Hardware)) {
        ToolStripMenuItem hardwareItem = new ToolStripMenuItem(hardware.Key.Name) {
          Checked = sensor != null && sensor.Hardware == hardware.Key
        };
        foreach (IGrouping<SensorType, ISensor> type in hardware.GroupBy(s => s.SensorType)
          .OrderBy(g => g.Key)) {
          ToolStripMenuItem typeItem = new ToolStripMenuItem(TypeName(type.Key, true)) {
            Checked = hardwareItem.Checked && sensor!.SensorType == type.Key
          };
          foreach (ISensor item in type.OrderBy(s => s.Index)) {
            ISensor target = item;
            typeItem.DropDownItems.Add(new ToolStripMenuItem(item.Name, null,
              delegate { ShowSensor(target); }) { Checked = item == sensor });
          }
          hardwareItem.DropDownItems.Add(typeItem);
        }
        menu.Items.Add(hardwareItem);
      }
      if (menu.Items.Count == 0)
        menu.Items.Add(new ToolStripMenuItem("No sensors found") { Enabled = false });
      ShowMenu(menu, sensorButton);
    }

    private void ShowCompareMenu() {
      ContextMenuStrip menu = new ContextMenuStrip();
      menu.Items.Add(new ToolStripMenuItem("None", null, delegate { SetCompare(null); }) {
        Checked = compare == null
      });
      if (sensor != null) {
        SensorType type = sensor.SensorType;
        foreach (IGrouping<IHardware, ISensor> hardware in AllSensors()
          .Where(s => s.SensorType == type && s != sensor).GroupBy(s => s.Hardware)) {
          menu.Items.Add(new ToolStripSeparator());
          menu.Items.Add(new ToolStripMenuItem(hardware.Key.Name) { Enabled = false });
          foreach (ISensor item in hardware.OrderBy(s => s.Index)) {
            ISensor target = item;
            menu.Items.Add(new ToolStripMenuItem(item.Name, null,
              delegate { SetCompare(target); }) { Checked = item == compare });
          }
        }
        if (menu.Items.Count == 1)
          menu.Items.Add(new ToolStripMenuItem("No other sensors of this type") { Enabled = false });
      }
      ShowMenu(menu, compareButton);
    }

    private void ShowMenu(ContextMenuStrip menu, Control below) {
      menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
      menu.Show(below, new Point(0, below.Height + (int)S(4)));
    }

    private void SetCompare(ISensor? value) {
      compare = value;
      CopySamplesLocked();
    }

    // ---- layout and painting ----------------------------------------------------------

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && buttonFont != null)
        return;
      foreach (Font? font in new[] { buttonFont, titleFont, subtitleFont, hintFont })
        font?.Dispose();
      fontDpi = DeviceDpi;
      buttonFont = Theme.CreateFont(Theme.SemiboldFamily, 9.5f, FontStyle.Regular, DeviceDpi);
      titleFont = Theme.CreateFont(Theme.DisplayFamily, 17f, FontStyle.Regular, DeviceDpi);
      subtitleFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
      hintFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      PerformLayout();
      Invalidate();
    }

    private float RowTop {
      get { return S(16); }
    }

    private float RowHeight {
      get { return S(34); }
    }

    private float TitleTop {
      get { return RowTop + RowHeight + S(14); }
    }

    protected override void OnLayout(LayoutEventArgs e) {
      base.OnLayout(e);
      EnsureFonts();
      int margin = (int)Math.Round(S(24));
      int top = (int)Math.Round(RowTop);
      int right = Width - margin;

      Size size = sensorButton.GetPreferredSize(Size.Empty);
      sensorButton.Bounds = new Rectangle(right - size.Width, top, size.Width, size.Height);
      right = sensorButton.Left - (int)Math.Round(S(8));
      size = compareButton.GetPreferredSize(Size.Empty);
      compareButton.Bounds = new Rectangle(right - size.Width, top, size.Width, size.Height);
      right = compareButton.Left - (int)Math.Round(S(16));
      size = rangeSelector.GetPreferredSize(Size.Empty);
      rangeSelector.Bounds = new Rectangle(right - size.Width, top, size.Width, size.Height);

      int chartTop = (int)Math.Ceiling(TitleTop + titleFont!.Height + S(4) +
        subtitleFont!.Height + S(16));
      chart.Bounds = new Rectangle(margin, chartTop, Math.Max(1, Width - 2 * margin),
        Math.Max(1, Height - chartTop - margin));
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      float margin = S(24);

      // Back button: chevron and the page it returns to, as above the sensor list.
      Size labelSize = TextRenderer.MeasureText(backText, buttonFont!, Size.Empty, Flags);
      float chevronWidth = S(8);
      float pillHeight = buttonFont!.Height + S(14);
      RectangleF back = new RectangleF(margin, RowTop + (RowHeight - pillHeight) / 2,
        S(14) + chevronWidth + S(8) + labelSize.Width + S(16), pillHeight);
      if (backPressed)
        back.Inflate(-S(1), -S(1));
      float h = backHover.Value;
      using (GraphicsPath path = Theme.RoundedRect(back, S(8)))
      using (SolidBrush fill = new SolidBrush(ColorMath.Lerp(theme.Surface, theme.SurfaceSunken, h)))
      using (Pen border = new Pen(ColorMath.Lerp(theme.Border,
        Theme.Blend(theme.Border, theme.Text, 0.18f), h), Math.Max(1f, S(1)))) {
        g.FillPath(fill, path);
        g.DrawPath(border, path);
      }
      float cx = back.Left + S(14) + chevronWidth / 2 - S(2) * h;
      float cy = back.Top + back.Height / 2;
      using (Pen pen = new Pen(theme.Text, Math.Max(1.5f, S(1.75f)))) {
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        g.DrawLines(pen, new[] {
          new PointF(cx + S(2.5f), cy - S(5)), new PointF(cx - S(2.5f), cy),
          new PointF(cx + S(2.5f), cy + S(5))
        });
      }
      TextRenderer.DrawText(g, backText, buttonFont,
        new Point((int)(back.Left + S(14) + chevronWidth + S(8)),
          (int)(back.Top + (back.Height - labelSize.Height) / 2)),
        theme.Text, Flags);
      backBounds = Rectangle.Round(back);

      // Sensor and hardware.
      int width = (int)Math.Max(1, Width - 2 * margin);
      float titleTop = TitleTop;
      TextRenderer.DrawText(g, sensor?.Name ?? "Sensor history", titleFont!,
        new Rectangle((int)margin, (int)titleTop, width, titleFont!.Height),
        theme.Text, Flags | TextFormatFlags.EndEllipsis);

      string subtitle = sensor == null
        ? "Choose a sensor to see its readings over the last 24 hours."
        : sensor.Hardware.Name + "  ·  " + TypeName(sensor.SensorType, false) +
          (available ? "" : "  ·  No longer available");
      float subtitleTop = titleTop + titleFont.Height + S(4);
      Size subtitleSize = TextRenderer.MeasureText(subtitle, subtitleFont!, Size.Empty, Flags);
      TextRenderer.DrawText(g, subtitle, subtitleFont!,
        new Rectangle((int)margin, (int)subtitleTop, width, subtitleFont!.Height),
        available ? theme.TextSecondary : theme.Warm, Flags | TextFormatFlags.EndEllipsis);

      Size hintSize = TextRenderer.MeasureText(Hint, hintFont!, Size.Empty, Flags);
      float hintX = Width - margin - hintSize.Width;
      if (hintX > margin + subtitleSize.Width + S(24))
        TextRenderer.DrawText(g, Hint, hintFont!,
          new Point((int)hintX, (int)(subtitleTop + (subtitleFont.Height - hintSize.Height) / 2)),
          theme.TextTertiary, Flags);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      bool over = backBounds.Contains(e.Location);
      backHover.Set(over ? 1 : 0);
      Cursor = over ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      backHover.Set(0);
      backPressed = false;
      Cursor = Cursors.Default;
      Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (e.Button == MouseButtons.Left && backBounds.Contains(e.Location)) {
        backPressed = true;
        Invalidate(backBounds);
      }
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left)
        return;
      bool click = backPressed && backBounds.Contains(e.Location);
      backPressed = false;
      Invalidate();
      if (click)
        BackClicked?.Invoke(this, EventArgs.Empty);
    }
  }
}
