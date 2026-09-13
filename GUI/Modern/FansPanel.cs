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

  public enum FanMode {
    Automatic,
    Fixed,
    Curve
  }

  /// <summary>What the Fans page needs from the application.</summary>
  public interface IFanControlHost {

    /// <summary>A sentence about missing access, or null when all is well.</summary>
    string? AccessNote { get; }

    FanMode GetMode(ISensor control);

    /// <summary>For example "CPU Package · 5 points".</summary>
    string? DescribeCurve(ISensor control);

    void SetAutomatic(ISensor control);

    void SetFixed(ISensor control, float percent);

    /// <summary>Opens the curve editor; returns once it closes.</summary>
    void EditCurve(ISensor control);
  }

  /// <summary>
  /// One card per controllable fan: live speed, and a clear choice between
  /// automatic control, a fixed speed and a temperature curve.
  /// </summary>
  public sealed class FansPanel : UserControl {

    private readonly IComputer computer;
    private readonly IFanControlHost host;
    private readonly PageHeader header;
    private readonly Panel scroller;
    private readonly List<FanCard> cards = new List<FanCard>();
    private string signature = "";
    private Theme theme = Theme.Current;

    public FansPanel(IComputer computer, IFanControlHost host) {
      this.computer = computer;
      this.host = host;
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

      header = new PageHeader {
        Dock = DockStyle.Top,
        Title = "Fans",
        Subtitle = "Choose how each fan is driven. Fans always return to automatic control when Open Hardware Monitor exits."
      };
      scroller = new BufferedScrollPanel { Dock = DockStyle.Fill, AutoScroll = true };
      scroller.Resize += delegate { LayoutCards(); };
      Controls.Add(scroller);
      Controls.Add(header);
      ApplyTheme();
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
      scroller.BackColor = theme.Background;
      header.Theme = theme;
      foreach (FanCard card in cards)
        card.Theme = theme;
      Invalidate(true);
    }

    private int S(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    /// <summary>Call after each sensor update, on the UI thread.</summary>
    public void UpdateValues() {
      List<ISensor> controls = new List<ISensor>();
      foreach (IHardware hardware in computer.Hardware)
        CollectControls(hardware, controls);

      string current = string.Join("|", controls.Select(c => c.Identifier.ToString()));
      if (current != signature) {
        signature = current;
        Rebuild(controls);
      }

      header.Note = host.AccessNote;
      header.Invalidate();
      foreach (FanCard card in cards)
        card.RefreshValues();
    }

    private static void CollectControls(IHardware hardware, List<ISensor> controls) {
      foreach (ISensor sensor in hardware.Sensors)
        if (sensor.SensorType == SensorType.Control && sensor.Control != null)
          controls.Add(sensor);
      foreach (IHardware sub in hardware.SubHardware)
        CollectControls(sub, controls);
    }

    private void Rebuild(List<ISensor> controls) {
      scroller.SuspendLayout();
      foreach (FanCard card in cards) {
        scroller.Controls.Remove(card);
        card.Dispose();
      }
      cards.Clear();
      foreach (ISensor control in controls) {
        FanCard card = new FanCard(control, host) { Theme = theme };
        cards.Add(card);
        scroller.Controls.Add(card);
      }
      scroller.ResumeLayout(false);
      header.EmptyMessage = cards.Count == 0
        ? "No controllable fans were found. Graphics card fans need administrator rights; motherboard fans also need PawnIO."
        : null;
      LayoutCards();
    }

    private void LayoutCards() {
      int margin = S(24), gap = S(16), minimum = S(380);
      int available = Math.Max(minimum, scroller.ClientSize.Width - 2 * margin);
      int columns = Math.Max(1, Math.Min(2, (available + gap) / (minimum + gap)));
      int width = (available - (columns - 1) * gap) / columns;
      Point scroll = scroller.AutoScrollPosition;
      int[] heights = new int[columns];
      scroller.SuspendLayout();
      for (int i = 0; i < cards.Count; i++) {
        int column = i % columns;
        int height = cards[i].PreferredHeight;
        Rectangle bounds = new Rectangle(margin + column * (width + gap) + scroll.X,
          S(4) + heights[column] + scroll.Y, width, height);
        if (cards[i].Bounds != bounds)
          cards[i].Bounds = bounds;
        heights[column] += height + gap;
      }
      scroller.AutoScrollMargin = new Size(0, margin);
      scroller.ResumeLayout(true);
    }
  }

  /// <summary>A card for one fan output.</summary>
  internal sealed class FanCard : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private readonly ISensor control;
    private readonly IFanControlHost host;
    private readonly SegmentedControl mode;
    private readonly ModernSlider slider;
    private readonly ModernButton editCurve;
    private Theme theme = Theme.Current;
    private bool syncing;
    private int fontDpi;
    private Font? titleFont, subtitleFont, bigFont, labelFont, valueFont;

    public FanCard(ISensor control, IFanControlHost host) {
      this.control = control;
      this.host = host;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

      mode = new SegmentedControl { Items = new[] { "Automatic", "Fixed speed", "Curve" } };
      slider = new ModernSlider();
      editCurve = new ModernButton { Text = "Edit curve", Kind = ButtonKind.Secondary };

      IControl hardwareControl = control.Control;
      slider.Minimum = hardwareControl.MinSoftwareValue;
      slider.Maximum = hardwareControl.MaxSoftwareValue;
      slider.Value = hardwareControl.SoftwareValue > 0
        ? hardwareControl.SoftwareValue
        : Math.Max(hardwareControl.MinSoftwareValue, control.Value ?? 50);

      mode.SelectedIndexChanged += OnModeChanged;
      slider.ValueChanged += delegate { Invalidate(); };
      slider.ValueCommitted += delegate { host.SetFixed(control, slider.Value); };
      editCurve.Click += delegate {
        host.EditCurve(control);
        Sync();
      };

      Controls.Add(mode);
      Controls.Add(slider);
      Controls.Add(editCurve);
      AccessibleName = control.Name;
      Sync();
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        foreach (ModernControl child in new ModernControl[] { mode, slider, editCurve }) {
          child.Theme = theme;
          child.SurfaceColor = theme.Surface;
        }
        Invalidate(true);
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    public int PreferredHeight {
      get { return (int)Math.Round(S(196)); }
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      foreach (Font? font in new[] { titleFont, subtitleFont, bigFont, labelFont, valueFont })
        font?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 10.5f, FontStyle.Regular, DeviceDpi);
      subtitleFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      bigFont = Theme.CreateFont(Theme.DisplayFamily, 18f, FontStyle.Regular, DeviceDpi);
      labelFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      valueFont = Theme.CreateFont(Theme.SemiboldFamily, 10f, FontStyle.Regular, DeviceDpi);
    }

    private void OnModeChanged(object? sender, EventArgs e) {
      if (syncing)
        return;
      switch ((FanMode)mode.SelectedIndex) {
        case FanMode.Automatic:
          host.SetAutomatic(control);
          break;
        case FanMode.Fixed:
          host.SetFixed(control, slider.Value);
          break;
        case FanMode.Curve:
          host.EditCurve(control);
          break;
      }
      Sync();
    }

    /// <summary>Brings the selection in line with what is actually in effect.</summary>
    private void Sync() {
      syncing = true;
      try {
        FanMode current = host.GetMode(control);
        mode.SelectedIndex = (int)current;
        slider.Visible = current == FanMode.Fixed;
        editCurve.Visible = current == FanMode.Curve;
      } finally {
        syncing = false;
      }
      Invalidate();
    }

    public void RefreshValues() {
      if (!slider.Capture && !mode.Focused)
        Sync();
      Invalidate();
    }

    private ISensor? FindSpeedSensor() {
      IHardware hardware = control.Hardware;
      ISensor? byName = null, byIndex = null;
      foreach (ISensor sensor in hardware.Sensors) {
        if (sensor.SensorType != SensorType.Fan)
          continue;
        if (string.Equals(sensor.Name, control.Name, StringComparison.OrdinalIgnoreCase))
          byName = sensor;
        else if (sensor.Index == control.Index)
          byIndex = sensor;
      }
      return byName ?? byIndex;
    }

    protected override void OnLayout(LayoutEventArgs e) {
      base.OnLayout(e);
      float pad = S(20);
      Size modeSize = mode.GetPreferredSize(Size.Empty);
      mode.Bounds = new Rectangle((int)pad, (int)(pad + S(62)), modeSize.Width, modeSize.Height);
      int rowY = (int)(mode.Bottom + S(16));
      slider.Bounds = new Rectangle((int)(pad - S(10)), rowY,
        (int)(Width - 2 * pad - S(64)), (int)S(28));
      Size buttonSize = editCurve.GetPreferredSize(Size.Empty);
      editCurve.Bounds = new Rectangle((int)(Width - pad - buttonSize.Width),
        rowY - (int)S(3), buttonSize.Width, buttonSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      RectangleF card = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      using (GraphicsPath path = Theme.RoundedRect(card, S(14)))
      using (SolidBrush surface = new SolidBrush(theme.Surface))
      using (Pen border = new Pen(theme.Border, Math.Max(1f, S(1)))) {
        g.FillPath(surface, path);
        g.DrawPath(border, path);
      }

      float pad = S(20);
      Image icon = ModernIcons.ForSensorType(SensorType.Fan);
      float iconSize = S(32);
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.DrawImage(icon, pad, pad + S(2), iconSize, iconSize);

      float textX = pad + iconSize + S(12);
      float rightWidth = S(150);
      int textWidth = (int)(Width - textX - pad - rightWidth);
      TextRenderer.DrawText(g, control.Name, titleFont!,
        new Rectangle((int)textX, (int)pad, textWidth, titleFont!.Height),
        theme.Text, Flags | TextFormatFlags.EndEllipsis);
      TextRenderer.DrawText(g, control.Hardware.Name, subtitleFont!,
        new Rectangle((int)textX, (int)pad + titleFont.Height, textWidth, subtitleFont!.Height),
        theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);

      // Live readings, right-aligned.
      ISensor? speed = FindSpeedSensor();
      string rpm = speed?.Value != null
        ? speed.Value.Value.ToString("N0", CultureInfo.CurrentCulture) + " RPM" : "–";
      string output = control.Value.HasValue
        ? "Output " + control.Value.Value.ToString("0", CultureInfo.CurrentCulture) + " %"
        : "Output managed by hardware";
      Rectangle right = new Rectangle((int)(Width - pad - rightWidth), (int)(pad - S(4)),
        (int)rightWidth, bigFont!.Height);
      TextRenderer.DrawText(g, rpm, bigFont, right, theme.Text,
        Flags | TextFormatFlags.Right);
      TextRenderer.DrawText(g, output, labelFont!,
        new Rectangle(right.X - (int)S(40), right.Bottom, right.Width + (int)S(40), labelFont!.Height),
        theme.TextSecondary, Flags | TextFormatFlags.Right);

      // The row below the mode selector explains or adjusts the choice.
      FanMode current = (FanMode)Math.Max(0, mode.SelectedIndex);
      int rowY = (int)(mode.Bottom + S(16));
      switch (current) {
        case FanMode.Automatic:
          TextRenderer.DrawText(g, control.Hardware.HardwareType == HardwareType.GpuNvidia ||
            control.Hardware.HardwareType == HardwareType.GpuAti
              ? "The graphics driver controls this fan."
              : "The motherboard controls this fan using its own settings.",
            labelFont, new Rectangle((int)pad, rowY + (int)S(6), (int)(Width - 2 * pad), labelFont.Height),
            theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
          break;
        case FanMode.Fixed:
          TextRenderer.DrawText(g, slider.Value.ToString("0", CultureInfo.CurrentCulture) + " %",
            valueFont!, new Rectangle((int)(Width - pad - S(56)), rowY + (int)S(5), (int)S(56), valueFont!.Height),
            theme.Text, Flags | TextFormatFlags.Right);
          break;
        case FanMode.Curve:
          string description = host.DescribeCurve(control) ?? "No curve set yet";
          TextRenderer.DrawText(g, description, labelFont,
            new Rectangle((int)pad, rowY + (int)S(6), (int)(editCurve.Left - pad - S(12)), labelFont.Height),
            theme.TextSecondary, Flags | TextFormatFlags.EndEllipsis);
          break;
      }
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        foreach (Font? font in new[] { titleFont, subtitleFont, bigFont, labelFont, valueFont })
          font?.Dispose();
      base.Dispose(disposing);
    }
  }

  /// <summary>Title, explanation and an optional note at the top of a page.</summary>
  public sealed class PageHeader : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private Theme theme = Theme.Current;
    private int fontDpi;
    private Font? titleFont, subtitleFont, noteFont;

    public PageHeader() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    private string? title, subtitle, note, emptyMessage;

    public string Title {
      get { return title ?? ""; }
      set { Update(ref title, value); }
    }

    public string Subtitle {
      get { return subtitle ?? ""; }
      set { Update(ref subtitle, value); }
    }

    public string? Note {
      get { return note; }
      set { Update(ref note, value); }
    }

    public string? EmptyMessage {
      get { return emptyMessage; }
      set { Update(ref emptyMessage, value); }
    }

    private void Update(ref string? field, string? value) {
      if (field == value)
        return;
      field = value;
      UpdateHeight();
      Invalidate();
    }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        Invalidate();
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      foreach (Font? font in new[] { titleFont, subtitleFont, noteFont })
        font?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.DisplayFamily, 17f, FontStyle.Regular, DeviceDpi);
      subtitleFont = Theme.CreateFont(Theme.TextFamily, 9f, FontStyle.Regular, DeviceDpi);
      noteFont = Theme.CreateFont(Theme.SemiboldFamily, 8.5f, FontStyle.Regular, DeviceDpi);
    }

    // The height follows the text, so it is kept current whenever the text or
    // the width changes rather than being corrected during painting.
    private void UpdateHeight() {
      int height = MeasureHeight();
      if (Height != height)
        Height = height;
    }

    protected override void OnSizeChanged(EventArgs e) {
      base.OnSizeChanged(e);
      UpdateHeight();
    }

    private int MeasureHeight() {
      EnsureFonts();
      int width = Math.Max(100, Width - (int)S(48));
      float y = S(22) + titleFont!.Height + S(6);
      y += TextRenderer.MeasureText(Subtitle, subtitleFont!, new Size(width, int.MaxValue),
        Flags | TextFormatFlags.WordBreak).Height;
      if (!string.IsNullOrEmpty(Note))
        y += S(12) + noteFont!.Height + S(10);
      if (!string.IsNullOrEmpty(EmptyMessage))
        y += S(24) + TextRenderer.MeasureText(EmptyMessage, subtitleFont!,
          new Size(width, int.MaxValue), Flags | TextFormatFlags.WordBreak).Height;
      return (int)Math.Ceiling(y + S(14));
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      int margin = (int)S(24);
      int width = Math.Max(100, Width - 2 * margin);
      float y = S(22);
      TextRenderer.DrawText(g, Title, titleFont!, new Point(margin, (int)y), theme.Text,
        Flags | TextFormatFlags.SingleLine);
      y += titleFont!.Height + S(6);
      Size subtitle = TextRenderer.MeasureText(Subtitle, subtitleFont!, new Size(width, int.MaxValue),
        Flags | TextFormatFlags.WordBreak);
      TextRenderer.DrawText(g, Subtitle, subtitleFont!, new Rectangle(margin, (int)y, width, subtitle.Height),
        theme.TextSecondary, Flags | TextFormatFlags.WordBreak);
      y += subtitle.Height;

      if (!string.IsNullOrEmpty(Note)) {
        y += S(12);
        Size text = TextRenderer.MeasureText(Note, noteFont!, Size.Empty, Flags | TextFormatFlags.SingleLine);
        RectangleF chip = new RectangleF(margin, y, S(26) + text.Width + S(12), noteFont!.Height + S(10));
        chip.Width = Math.Min(chip.Width, width);
        using (GraphicsPath path = Theme.RoundedRect(chip, chip.Height / 2))
        using (SolidBrush brush = new SolidBrush(Theme.Blend(theme.Background, theme.Warm,
          theme.IsDark ? 0.18f : 0.12f)))
          g.FillPath(brush, path);
        float dot = S(7);
        using (SolidBrush brush = new SolidBrush(theme.Warm))
          g.FillEllipse(brush, chip.Left + S(11), chip.Top + (chip.Height - dot) / 2, dot, dot);
        TextRenderer.DrawText(g, Note, noteFont!,
          new Rectangle((int)(chip.Left + S(24)), (int)(chip.Top + S(5)), (int)(chip.Width - S(30)), noteFont!.Height),
          Theme.Blend(theme.Warm, theme.Text, theme.IsDark ? 0.35f : 0.25f),
          Flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        y += chip.Height;
      }

      if (!string.IsNullOrEmpty(EmptyMessage)) {
        y += S(24);
        TextRenderer.DrawText(g, EmptyMessage, subtitleFont!,
          new Rectangle(margin, (int)y, width, (int)S(200)), theme.TextSecondary,
          Flags | TextFormatFlags.WordBreak);
      }
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        foreach (Font? font in new[] { titleFont, subtitleFont, noteFont })
          font?.Dispose();
      base.Dispose(disposing);
    }
  }

  internal sealed class BufferedScrollPanel : Panel {
    public BufferedScrollPanel() {
      DoubleBuffered = true;
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
  }
}
