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
  /// Edits a fan curve in place: the temperature source, a graph of duty
  /// against temperature with draggable points, and the hysteresis. Every
  /// finished change raises <see cref="CurveCommitted"/>; nothing is applied
  /// while a point is still being dragged.
  /// </summary>
  public sealed class FanCurveEditor : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private const float MaxHysteresis = 10;

    private readonly SensorPicker source;
    private readonly ModernButton lessHysteresis;
    private readonly ModernButton moreHysteresis;
    private readonly CurveGraph graph;
    private Theme theme = Theme.Current;
    private string? sourceIdentifier;
    private float hysteresis = FanCurve.DefaultHysteresis;
    private int fontDpi;
    private Font? smallFont;

    public FanCurveEditor() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

      source = new SensorPicker { AccessibleName = "Temperature source" };
      lessHysteresis = new ModernButton { Text = "−", Kind = ButtonKind.Secondary,
        AccessibleName = "Less hysteresis" };
      moreHysteresis = new ModernButton { Text = "+", Kind = ButtonKind.Secondary,
        AccessibleName = "More hysteresis" };
      graph = new CurveGraph();

      source.Click += delegate { ShowSourceMenu(); };
      lessHysteresis.Click += delegate { ChangeHysteresis(-1); };
      moreHysteresis.Click += delegate { ChangeHysteresis(1); };
      graph.Committed += delegate { Commit(); };

      Controls.Add(source);
      Controls.Add(lessHysteresis);
      Controls.Add(moreHysteresis);
      Controls.Add(graph);
      AccessibleName = "Fan curve editor";
    }

    /// <summary>Raised after every finished change.</summary>
    public event EventHandler? CurveCommitted;

    /// <summary>Supplies the sensors a curve can follow when the picker opens.</summary>
    public Func<IReadOnlyList<ISensor>>? TemperatureSensors { get; set; }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        foreach (ModernControl child in new ModernControl[] { source, lessHysteresis, moreHysteresis, graph }) {
          child.Theme = value;
          child.SurfaceColor = BackColor;
        }
        Invalidate(true);
      }
    }

    public override Color BackColor {
      get { return base.BackColor; }
      set {
        base.BackColor = value;
        if (source != null)
          foreach (ModernControl child in new ModernControl[] { source, lessHysteresis, moreHysteresis, graph })
            child.SurfaceColor = value;
      }
    }

    /// <summary>True while a change is in progress and outside updates should wait.</summary>
    public bool IsEditing {
      get { return graph.IsInteracting; }
    }

    public float MinimumDuty {
      get { return graph.MinimumDuty; }
      set { graph.MinimumDuty = value; graph.Invalidate(); }
    }

    public float MaximumDuty {
      get { return graph.MaximumDuty; }
      set { graph.MaximumDuty = value; graph.Invalidate(); }
    }

    /// <summary>The calibrated lowest duty that keeps the fan turning, drawn as a band.</summary>
    public float? RunningMinimum {
      get { return graph.RunningMinimum; }
      set {
        if (graph.RunningMinimum != value) {
          graph.RunningMinimum = value;
          graph.Invalidate();
        }
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    public int PreferredHeight {
      get { return (int)Math.Round(S(34 + 10 + 196 + 8 + 16)); }
    }

    /// <summary>Shows <paramref name="curve"/>, unless the user is changing the curve right now.</summary>
    public void Load(FanCurve curve, string? sourceName) {
      if (IsEditing)
        return;
      sourceIdentifier = curve.SourceSensorIdentifier;
      hysteresis = curve.Hysteresis;
      source.Text = sourceName ?? "A sensor that is not available";
      graph.SetPoints(curve.Points);
      UpdateHysteresisButtons();
      Invalidate();
    }

    /// <summary>The current temperature of the source and the duty the fan gets.</summary>
    public void SetLive(float? temperature, float? duty) {
      graph.SetLive(temperature, duty);
    }

    /// <summary>The curve as edited, or null when it cannot be built.</summary>
    public FanCurve? BuildCurve() {
      if (string.IsNullOrEmpty(sourceIdentifier) || graph.Points.Count < 2)
        return null;
      try {
        return new FanCurve(sourceIdentifier,
          graph.Points.Select(p => new FanCurvePoint(p.X, p.Y)), hysteresis);
      } catch (ArgumentException) {
        return null;
      }
    }

    private void Commit() {
      if (BuildCurve() != null)
        CurveCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void ChangeHysteresis(float change) {
      float next = Math.Clamp((float)Math.Round(hysteresis + change), 0, MaxHysteresis);
      if (next == hysteresis)
        return;
      hysteresis = next;
      UpdateHysteresisButtons();
      Invalidate();
      Commit();
    }

    private void UpdateHysteresisButtons() {
      lessHysteresis.Enabled = Enabled && hysteresis > 0;
      moreHysteresis.Enabled = Enabled && hysteresis < MaxHysteresis;
      lessHysteresis.AccessibleDescription = moreHysteresis.AccessibleDescription =
        HysteresisText();
    }

    private string HysteresisText() {
      return "Hysteresis " + hysteresis.ToString("0", CultureInfo.CurrentCulture) + " °C";
    }

    protected override void OnEnabledChanged(EventArgs e) {
      base.OnEnabledChanged(e);
      UpdateHysteresisButtons();
      Invalidate(true);
    }

    private void ShowSourceMenu() {
      IReadOnlyList<ISensor> sensors = TemperatureSensors?.Invoke() ?? Array.Empty<ISensor>();
      ContextMenuStrip menu = new ContextMenuStrip();
      if (sensors.Count == 0) {
        menu.Items.Add(new ToolStripMenuItem("No temperature sensors are available") { Enabled = false });
      }
      IHardware? group = null;
      foreach (ISensor sensor in sensors) {
        if (sensor.Hardware != group) {
          if (menu.Items.Count > 0)
            menu.Items.Add(new ToolStripSeparator());
          group = sensor.Hardware;
          menu.Items.Add(new ToolStripMenuItem(group?.Name ?? "") { Enabled = false });
        }
        string id = sensor.Identifier.ToString();
        ToolStripMenuItem item = new ToolStripMenuItem(sensor.Name) {
          Checked = id == sourceIdentifier
        };
        string name = SensorName(sensor);
        item.Click += delegate {
          if (id == sourceIdentifier)
            return;
          sourceIdentifier = id;
          source.Text = name;
          Commit();
        };
        menu.Items.Add(item);
      }
      menu.Closed += delegate { BeginInvoke((Action)menu.Dispose); };
      menu.Show(source, new Point(0, source.Height));
    }

    public static string SensorName(ISensor sensor) {
      return sensor.Hardware != null ? sensor.Hardware.Name + " · " + sensor.Name : sensor.Name;
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && smallFont != null)
        return;
      smallFont?.Dispose();
      fontDpi = DeviceDpi;
      smallFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
    }

    private int HysteresisTextWidth {
      get {
        EnsureFonts();
        return TextRenderer.MeasureText("Hysteresis 10 °C", smallFont!, Size.Empty, Flags).Width;
      }
    }

    protected override void OnLayout(LayoutEventArgs levent) {
      base.OnLayout(levent);
      int row = (int)Math.Round(S(34));
      Size button = new Size(row, row);
      int textWidth = HysteresisTextWidth + (int)S(16);
      moreHysteresis.Bounds = new Rectangle(new Point(Width - button.Width, 0), button);
      lessHysteresis.Bounds = new Rectangle(new Point(moreHysteresis.Left - textWidth - button.Width, 0), button);
      source.Bounds = new Rectangle(0, 0, Math.Max((int)S(80), lessHysteresis.Left - (int)S(12)), row);
      int graphTop = row + (int)S(10);
      int hint = (int)S(16);
      graph.Bounds = new Rectangle(0, graphTop, Width,
        Math.Max((int)S(80), Height - graphTop - hint - (int)S(8)));
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(BackColor);
      Color text = Enabled ? theme.Text : theme.TextTertiary;
      Rectangle hysteresisText = new Rectangle(lessHysteresis.Right, 0,
        moreHysteresis.Left - lessHysteresis.Right, lessHysteresis.Height);
      TextRenderer.DrawText(g, HysteresisText(), smallFont!, hysteresisText, text,
        Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      TextRenderer.DrawText(g, "Drag points. Double-click adds one; right-click or Delete removes it.",
        smallFont!, new Rectangle(0, graph.Bottom + (int)S(6), Width, smallFont!.Height),
        theme.TextTertiary, Flags | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        smallFont?.Dispose();
      base.Dispose(disposing);
    }
  }

  /// <summary>A button-like picker: "Follows" and the chosen sensor, with a chevron.</summary>
  internal sealed class SensorPicker : ModernControl {

    public SensorPicker() {
      Cursor = Cursors.Hand;
      AccessibleRole = AccessibleRole.ButtonDropDown;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text {
      get { return base.Text; }
      set {
        base.Text = value;
        AccessibleDescription = value;
        Invalidate();
      }
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      PaintBackground(g);
      float h = Hover.Value;
      RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      Color fill = ColorMath.Lerp(Theme.IsDark ? Theme.Blend(Behind, Theme.Text, 0.06f) : Theme.Surface,
        Theme.SurfaceSunken, h);
      Color border = ColorMath.Lerp(Theme.Border, Theme.Blend(Theme.Border, Theme.Text, 0.2f), h);
      using (GraphicsPath path = Theme.RoundedRect(bounds, S(8))) {
        using (SolidBrush brush = new SolidBrush(fill))
          g.FillPath(brush, path);
        using (Pen pen = new Pen(border, Math.Max(1f, S(1))))
          g.DrawPath(pen, path);
      }

      float x = S(12);
      const string caption = "Follows ";
      Size captionSize = TextRenderer.MeasureText(g, caption, RegularFont, Size.Empty, TextFlags);
      Rectangle row = new Rectangle((int)x, 0, captionSize.Width, Height);
      TextRenderer.DrawText(g, caption, RegularFont, row, Enabled ? Theme.TextSecondary : Theme.TextTertiary,
        TextFlags | TextFormatFlags.VerticalCenter);
      float chevron = S(8);
      int nameWidth = (int)(Width - x - captionSize.Width - chevron - S(24));
      TextRenderer.DrawText(g, Text, SemiboldFont,
        new Rectangle(row.Right, 0, Math.Max(0, nameWidth), Height),
        Enabled ? Theme.Text : Theme.TextTertiary,
        TextFlags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

      float cx = Width - S(16), cy = Height / 2f;
      using (Pen pen = new Pen(Enabled ? Theme.TextSecondary : Theme.TextTertiary, S(1.5f))) {
        pen.StartCap = pen.EndCap = LineCap.Round;
        g.DrawLines(pen, new[] {
          new PointF(cx - chevron / 2, cy - chevron / 4),
          new PointF(cx, cy + chevron / 4),
          new PointF(cx + chevron / 2, cy - chevron / 4)
        });
      }
      DrawFocusRing(g, bounds, S(8));
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      bool click = e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location);
      base.OnMouseUp(e);
      if (click)
        OnClick(EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) {
      return keyData == Keys.Down || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter ||
        e.KeyCode == Keys.Down || e.KeyCode == Keys.F4)) {
        e.Handled = true;
        OnClick(EventArgs.Empty);
      }
    }

    protected override void OnClick(EventArgs e) {
      if (e is MouseEventArgs)
        return;
      base.OnClick(e);
    }
  }

  /// <summary>
  /// The curve itself: temperature across, duty up. Points keep their order
  /// and stay inside the control's range; the keyboard can do everything the
  /// mouse can.
  /// </summary>
  internal sealed class CurveGraph : ModernControl {

    private readonly List<PointF> points = new List<PointF>();
    private float rangeLow = 20, rangeHigh = 100;
    private int selected = -1;
    private int hover = -1;
    private int dragging = -1;
    private bool changed;
    private bool keyChanging;
    private float? liveTemperature, liveDuty;

    public CurveGraph() {
      AccessibleRole = AccessibleRole.Chart;
      AccessibleName = "Fan curve";
      Cursor = Cursors.Cross;
    }

    public event EventHandler? Committed;

    public IReadOnlyList<PointF> Points {
      get { return points; }
    }

    public float MinimumDuty { get; set; }

    public float MaximumDuty { get; set; } = 100;

    public float? RunningMinimum { get; set; }

    public bool IsInteracting {
      get { return dragging >= 0 || keyChanging; }
    }

    protected override float FontPoints {
      get { return 8f; }
    }

    public void SetPoints(IEnumerable<FanCurvePoint> curve) {
      points.Clear();
      points.AddRange(curve.Select(p => new PointF(p.Temperature, p.Duty)).OrderBy(p => p.X));
      float low = points.Count > 0 ? points.Min(p => p.X) : 20;
      float high = points.Count > 0 ? points.Max(p => p.X) : 100;
      rangeLow = Math.Min(20, (float)Math.Floor(low / 10) * 10);
      rangeHigh = Math.Max(100, (float)Math.Ceiling(high / 10) * 10);
      if (selected >= points.Count)
        selected = points.Count - 1;
      hover = -1;
      Describe();
      Invalidate();
    }

    public void SetLive(float? temperature, float? duty) {
      if (temperature == liveTemperature && duty == liveDuty)
        return;
      liveTemperature = temperature;
      liveDuty = duty;
      Invalidate();
    }

    // ---- geometry ---------------------------------------------------------------

    private RectangleF Plot {
      get {
        return new RectangleF(S(40), S(8), Math.Max(S(40), Width - S(40) - S(12)),
          Math.Max(S(40), Height - S(8) - S(22)));
      }
    }

    private float XOf(float temperature) {
      RectangleF plot = Plot;
      return plot.Left + (temperature - rangeLow) / (rangeHigh - rangeLow) * plot.Width;
    }

    private float YOf(float duty) {
      RectangleF plot = Plot;
      return plot.Bottom - duty / 100f * plot.Height;
    }

    private float TemperatureAt(float x) {
      RectangleF plot = Plot;
      return rangeLow + (x - plot.Left) / plot.Width * (rangeHigh - rangeLow);
    }

    private float DutyAt(float y) {
      RectangleF plot = Plot;
      return (plot.Bottom - y) / plot.Height * 100f;
    }

    private float Evaluate(float temperature) {
      if (points.Count == 0)
        return 0;
      if (temperature <= points[0].X)
        return points[0].Y;
      for (int i = 1; i < points.Count; i++) {
        if (temperature <= points[i].X) {
          PointF a = points[i - 1], b = points[i];
          return a.Y + (b.Y - a.Y) * (temperature - a.X) / (b.X - a.X);
        }
      }
      return points[points.Count - 1].Y;
    }

    private int HitTest(Point location) {
      float radius = S(10);
      int best = -1;
      float bestDistance = radius * radius;
      for (int i = 0; i < points.Count; i++) {
        float dx = XOf(points[i].X) - location.X, dy = YOf(points[i].Y) - location.Y;
        float distance = dx * dx + dy * dy;
        if (distance <= bestDistance) {
          best = i;
          bestDistance = distance;
        }
      }
      return best;
    }

    // ---- editing ----------------------------------------------------------------

    /// <summary>Moves a point, keeping it between its neighbours and inside the allowed duty.</summary>
    private void MovePoint(int index, float temperature, float duty) {
      float low = index > 0 ? points[index - 1].X + 1 : rangeLow;
      float high = index < points.Count - 1 ? points[index + 1].X - 1 : rangeHigh;
      temperature = Math.Clamp((float)Math.Round(temperature), low, Math.Max(low, high));
      duty = Math.Clamp((float)Math.Round(duty), MinimumDuty, Math.Max(MinimumDuty, MaximumDuty));
      PointF next = new PointF(temperature, duty);
      if (points[index] == next)
        return;
      points[index] = next;
      changed = true;
      Describe();
      Invalidate();
    }

    private void AddPoint(float temperature, float duty) {
      temperature = Math.Clamp((float)Math.Round(temperature), rangeLow, rangeHigh);
      duty = Math.Clamp((float)Math.Round(duty), MinimumDuty, Math.Max(MinimumDuty, MaximumDuty));
      if (points.Any(p => Math.Abs(p.X - temperature) < 1))
        return;
      int index = points.FindIndex(p => p.X > temperature);
      if (index < 0)
        index = points.Count;
      points.Insert(index, new PointF(temperature, duty));
      selected = index;
      Describe();
      Invalidate();
      Committed?.Invoke(this, EventArgs.Empty);
    }

    private void RemovePoint(int index) {
      if (index < 0 || index >= points.Count)
        return;
      if (points.Count <= 2) {
        System.Media.SystemSounds.Beep.Play();
        return;
      }
      points.RemoveAt(index);
      selected = Math.Min(index, points.Count - 1);
      hover = -1;
      Describe();
      Invalidate();
      Committed?.Invoke(this, EventArgs.Empty);
    }

    private void Describe() {
      if (selected < 0 || selected >= points.Count) {
        AccessibleDescription = points.Count.ToString(CultureInfo.CurrentCulture) + " points";
        return;
      }
      PointF p = points[selected];
      AccessibleDescription = string.Format(CultureInfo.CurrentCulture,
        "Point {0} of {1}: {2:0} °C, {3:0} %", selected + 1, points.Count, p.X, p.Y);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (!Enabled)
        return;
      int hit = HitTest(e.Location);
      if (e.Button == MouseButtons.Left) {
        if (hit >= 0) {
          selected = hit;
          dragging = hit;
          changed = false;
          Capture = true;
          Describe();
        }
        Invalidate();
      } else if (e.Button == MouseButtons.Right && hit >= 0) {
        RemovePoint(hit);
      }
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      if (dragging >= 0) {
        MovePoint(dragging, TemperatureAt(e.X), DutyAt(e.Y));
        return;
      }
      int hit = Enabled ? HitTest(e.Location) : -1;
      if (hit != hover) {
        hover = hit;
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Cross;
        Invalidate();
      }
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (dragging < 0)
        return;
      dragging = -1;
      Capture = false;
      Invalidate();
      if (changed) {
        changed = false;
        Committed?.Invoke(this, EventArgs.Empty);
      }
    }

    protected override void OnMouseCaptureChanged(EventArgs e) {
      base.OnMouseCaptureChanged(e);
      // Capture lost mid-drag (a window popped up): keep what was done.
      if (dragging >= 0 && !Capture) {
        dragging = -1;
        if (changed) {
          changed = false;
          Committed?.Invoke(this, EventArgs.Empty);
        }
      }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) {
      base.OnMouseDoubleClick(e);
      if (!Enabled || e.Button != MouseButtons.Left || HitTest(e.Location) >= 0 ||
        !Plot.Contains(e.Location))
        return;
      AddPoint(TemperatureAt(e.X), DutyAt(e.Y));
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      if (hover >= 0) {
        hover = -1;
        Invalidate();
      }
    }

    protected override bool IsInputKey(Keys keyData) {
      switch (keyData & Keys.KeyCode) {
        case Keys.Left:
        case Keys.Right:
        case Keys.Up:
        case Keys.Down:
          return true;
      }
      return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (!Enabled || points.Count == 0)
        return;
      if (selected < 0)
        selected = 0;
      float step = e.Shift ? 5 : 1;
      PointF p = points[selected];
      switch (e.KeyCode) {
        case Keys.Left when e.Control:
        case Keys.PageUp:
          selected = Math.Max(0, selected - 1);
          break;
        case Keys.Right when e.Control:
        case Keys.PageDown:
          selected = Math.Min(points.Count - 1, selected + 1);
          break;
        case Keys.Home:
          selected = 0;
          break;
        case Keys.End:
          selected = points.Count - 1;
          break;
        case Keys.Left:
          keyChanging = true;
          MovePoint(selected, p.X - step, p.Y);
          break;
        case Keys.Right:
          keyChanging = true;
          MovePoint(selected, p.X + step, p.Y);
          break;
        case Keys.Up:
          keyChanging = true;
          MovePoint(selected, p.X, p.Y + step);
          break;
        case Keys.Down:
          keyChanging = true;
          MovePoint(selected, p.X, p.Y - step);
          break;
        case Keys.Delete:
        case Keys.Back:
          RemovePoint(selected);
          break;
        case Keys.Insert:
        case Keys.Oemplus:
        case Keys.Add:
          InsertAfterSelected();
          break;
        default:
          return;
      }
      e.Handled = true;
      Describe();
      Invalidate();
    }

    private void InsertAfterSelected() {
      int a = selected < points.Count - 1 ? selected : selected - 1;
      if (a < 0)
        return;
      PointF first = points[a], second = points[a + 1];
      if (second.X - first.X < 2)
        return;
      AddPoint((first.X + second.X) / 2, (first.Y + second.Y) / 2);
    }

    protected override void OnKeyUp(KeyEventArgs e) {
      base.OnKeyUp(e);
      FinishKeyChange();
    }

    protected override void OnLostFocus(EventArgs e) {
      base.OnLostFocus(e);
      FinishKeyChange();
    }

    private void FinishKeyChange() {
      if (!keyChanging)
        return;
      keyChanging = false;
      if (changed) {
        changed = false;
        Committed?.Invoke(this, EventArgs.Empty);
      }
    }

    // ---- painting ---------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      PaintBackground(g);
      RectangleF plot = Plot;
      float fade = Enabled ? 1 : 0.5f;

      Color ground = Theme.Blend(Behind, Theme.SurfaceSunken, Theme.IsDark ? 0.55f : 0.7f);
      using (GraphicsPath path = Theme.RoundedRect(RectangleF.Inflate(plot, S(2), S(2)), S(6)))
      using (SolidBrush brush = new SolidBrush(ground))
        g.FillPath(brush, path);

      // Duties the fan cannot get: below the control's minimum, and below
      // what keeps it turning.
      if (MinimumDuty > 0)
        using (SolidBrush brush = new SolidBrush(Theme.Blend(ground, Theme.Text, 0.07f)))
          g.FillRectangle(brush, plot.Left, YOf(MinimumDuty), plot.Width, plot.Bottom - YOf(MinimumDuty));
      if (MaximumDuty < 100)
        using (SolidBrush brush = new SolidBrush(Theme.Blend(ground, Theme.Text, 0.07f)))
          g.FillRectangle(brush, plot.Left, plot.Top, plot.Width, YOf(MaximumDuty) - plot.Top);
      if (RunningMinimum.HasValue && RunningMinimum.Value > MinimumDuty) {
        float top = YOf(RunningMinimum.Value), bottom = YOf(MinimumDuty);
        using (HatchBrush hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal,
          ColorMath.WithAlpha(Theme.Warm, 0.22f), Color.Transparent))
          g.FillRectangle(hatch, plot.Left, top, plot.Width, bottom - top);
        if (bottom - top > RegularFont.Height)
          TextRenderer.DrawText(g, "Too slow to keep turning", RegularFont,
            new Rectangle((int)(plot.Left + S(6)), (int)(bottom - RegularFont.Height - S(2)),
              (int)plot.Width, RegularFont.Height),
            Theme.Blend(Theme.Warm, Theme.TextSecondary, 0.4f), TextFlags);
      }

      using (Pen grid = new Pen(Theme.Blend(ground, Theme.Border, Theme.IsDark ? 1f : 0.9f), Math.Max(1f, S(1)))) {
        float span = rangeHigh - rangeLow;
        float every = span > 100 ? 20 : span > 60 ? 10 : 5;
        for (float t = (float)Math.Ceiling(rangeLow / every) * every; t <= rangeHigh + 0.01f; t += every) {
          float x = XOf(t);
          g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
          if (t % (every * 2) == 0)
            TextRenderer.DrawText(g, t.ToString("0", CultureInfo.CurrentCulture) + "°", RegularFont,
              new Rectangle((int)(x - S(20)), (int)(plot.Bottom + S(5)), (int)S(40), RegularFont.Height),
              Theme.TextTertiary, TextFlags | TextFormatFlags.HorizontalCenter);
        }
        for (int d = 0; d <= 100; d += 25) {
          float y = YOf(d);
          g.DrawLine(grid, plot.Left, y, plot.Right, y);
          if (d % 50 == 0)
            TextRenderer.DrawText(g, d.ToString(CultureInfo.CurrentCulture) + " %", RegularFont,
              new Rectangle(0, (int)(y - RegularFont.Height / 2f), (int)(plot.Left - S(6)), RegularFont.Height),
              Theme.TextTertiary, TextFlags | TextFormatFlags.Right);
        }
      }

      if (points.Count >= 2) {
        List<PointF> line = new List<PointF> { new PointF(plot.Left, YOf(points[0].Y)) };
        line.AddRange(points.Select(p => new PointF(XOf(p.X), YOf(p.Y))));
        line.Add(new PointF(plot.Right, YOf(points[points.Count - 1].Y)));

        GraphicsState state = g.Save();
        g.SetClip(plot);
        using (GraphicsPath area = new GraphicsPath()) {
          area.AddLines(line.ToArray());
          area.AddLine(plot.Right, plot.Bottom, plot.Left, plot.Bottom);
          area.CloseFigure();
          using (SolidBrush brush = new SolidBrush(ColorMath.WithAlpha(Theme.Accent, 0.14f * fade)))
            g.FillPath(brush, area);
        }
        using (Pen pen = new Pen(ColorMath.WithAlpha(Theme.Accent, fade), S(2)) {
          LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round
        })
          g.DrawLines(pen, line.ToArray());
        g.Restore(state);
      }

      // Where the fan is now.
      if (liveTemperature.HasValue && float.IsFinite(liveTemperature.Value)) {
        float t = Math.Clamp(liveTemperature.Value, rangeLow, rangeHigh);
        float x = XOf(t);
        using (Pen pen = new Pen(Theme.TextTertiary, Math.Max(1f, S(1))) { DashStyle = DashStyle.Dash })
          g.DrawLine(pen, x, plot.Top, x, plot.Bottom);
        float duty = liveDuty.HasValue && float.IsFinite(liveDuty.Value) ? liveDuty.Value : Evaluate(t);
        float y = YOf(Math.Clamp(duty, 0, 100));
        float r = S(4.5f);
        using (SolidBrush ring = new SolidBrush(Behind))
          g.FillEllipse(ring, x - r - S(1.5f), y - r - S(1.5f), 2 * (r + S(1.5f)), 2 * (r + S(1.5f)));
        using (SolidBrush brush = new SolidBrush(Theme.Warm))
          g.FillEllipse(brush, x - r, y - r, 2 * r, 2 * r);
        string label = string.Format(CultureInfo.CurrentCulture, "{0:0} °C → {1:0} %",
          liveTemperature.Value, duty);
        Size size = TextRenderer.MeasureText(g, label, SemiboldFont, Size.Empty, TextFlags);
        float lx = Math.Clamp(x + S(6), plot.Left + S(4), plot.Right - size.Width - S(4));
        TextRenderer.DrawText(g, label, SemiboldFont, new Point((int)lx, (int)(plot.Top + S(4))),
          Theme.TextSecondary, TextFlags);
      }

      for (int i = 0; i < points.Count; i++) {
        float x = XOf(points[i].X), y = YOf(points[i].Y);
        bool isSelected = i == selected && (Focused || dragging == i);
        float r = S(5) + (i == hover || i == dragging ? S(1.5f) : 0);
        using (SolidBrush brush = new SolidBrush(isSelected ? Theme.Accent : Behind))
          g.FillEllipse(brush, x - r, y - r, 2 * r, 2 * r);
        using (Pen pen = new Pen(ColorMath.WithAlpha(Theme.Accent, fade), S(2)))
          g.DrawEllipse(pen, x - r, y - r, 2 * r, 2 * r);
      }

      int shown = dragging >= 0 ? dragging : Focused ? selected : -1;
      if (shown >= 0 && shown < points.Count) {
        PointF p = points[shown];
        string label = string.Format(CultureInfo.CurrentCulture, "{0:0} °C, {1:0} %", p.X, p.Y);
        Size size = TextRenderer.MeasureText(g, label, SemiboldFont, Size.Empty, TextFlags);
        RectangleF chip = new RectangleF(0, 0, size.Width + S(12), size.Height + S(6));
        chip.X = Math.Clamp(XOf(p.X) - chip.Width / 2, plot.Left, plot.Right - chip.Width);
        chip.Y = YOf(p.Y) - chip.Height - S(10);
        if (chip.Y < plot.Top)
          chip.Y = YOf(p.Y) + S(10);
        using (GraphicsPath path = Theme.RoundedRect(chip, S(5)))
        using (SolidBrush brush = new SolidBrush(Theme.Text))
          g.FillPath(brush, path);
        TextRenderer.DrawText(g, label, SemiboldFont, Rectangle.Round(chip), Behind,
          TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      }

      DrawFocusRing(g, RectangleF.Inflate(plot, S(2), S(2)), S(6));
    }
  }
}
