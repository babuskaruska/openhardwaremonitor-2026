/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// Shared plumbing for the owner-drawn controls: theme, DPI-exact fonts,
  /// hover and press state with motion, and keyboard focus cues.
  /// </summary>
  public abstract class ModernControl : Control {

    protected const TextFormatFlags TextFlags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private Theme theme = Theme.Current;
    private int fontDpi;
    private Font? regularFont, semiboldFont;

    protected ModernControl() {
      SetStyle(ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
        ControlStyles.ResizeRedraw | ControlStyles.Selectable |
        ControlStyles.SupportsTransparentBackColor, true);
      Hover = new AnimatedValue(this, 0.16f);
      Hover.Set(0);
      Press = new AnimatedValue(this, 0.10f);
      Press.Set(0);
      TabStop = true;
    }

    protected AnimatedValue Hover { get; }
    protected AnimatedValue Press { get; }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        Invalidate();
      }
    }

    /// <summary>The colour behind the control, usually a card surface.</summary>
    public Color SurfaceColor { get; set; } = Color.Empty;

    protected Color Behind {
      get { return SurfaceColor.IsEmpty ? theme.Background : SurfaceColor; }
    }

    protected float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    protected Font RegularFont {
      get {
        EnsureFonts();
        return regularFont!;
      }
    }

    protected Font SemiboldFont {
      get {
        EnsureFonts();
        return semiboldFont!;
      }
    }

    protected virtual float FontPoints {
      get { return 9.5f; }
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && regularFont != null)
        return;
      regularFont?.Dispose();
      semiboldFont?.Dispose();
      fontDpi = DeviceDpi;
      regularFont = Theme.CreateFont(Theme.TextFamily, FontPoints, FontStyle.Regular, DeviceDpi);
      semiboldFont = Theme.CreateFont(Theme.SemiboldFamily, FontPoints, FontStyle.Regular, DeviceDpi);
      OnFontsChanged();
    }

    protected virtual void OnFontsChanged() {
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) {
      base.OnMouseEnter(e);
      if (Enabled)
        Hover.Set(1);
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      Hover.Set(0);
      Press.Set(0);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (e.Button == MouseButtons.Left && Enabled) {
        Press.Set(1);
        Focus();
      }
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      Press.Set(0);
    }

    protected override void OnEnabledChanged(EventArgs e) {
      base.OnEnabledChanged(e);
      Cursor = Enabled ? Cursors.Hand : Cursors.Default;
      Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) {
      base.OnGotFocus(e);
      Invalidate();
    }

    protected override void OnLostFocus(EventArgs e) {
      base.OnLostFocus(e);
      Invalidate();
    }

    protected void PaintBackground(Graphics g) {
      g.Clear(Behind);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    protected void DrawFocusRing(Graphics g, RectangleF bounds, float radius) {
      if (!Focused || !ShowFocusCues)
        return;
      RectangleF ring = RectangleF.Inflate(bounds, S(2), S(2));
      using (GraphicsPath path = Theme.RoundedRect(ring, radius + S(2)))
      using (Pen pen = new Pen(theme.Accent, S(1.75f)))
        g.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        regularFont?.Dispose();
        semiboldFont?.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  /// <summary>An on/off switch with a sliding knob.</summary>
  public sealed class ToggleSwitch : ModernControl {

    private readonly AnimatedValue knob;
    private bool isChecked;

    public ToggleSwitch() {
      knob = new AnimatedValue(this, 0.2f);
      knob.Set(0);
      Cursor = Cursors.Hand;
      AccessibleRole = AccessibleRole.CheckButton;
      Size = new Size(44, 24);
    }

    public event EventHandler? CheckedChanged;

    public bool Checked {
      get { return isChecked; }
      set { SetChecked(value, false); }
    }

    private void SetChecked(bool value, bool byUser) {
      if (isChecked == value)
        return;
      isChecked = value;
      knob.Set(value ? 1 : 0, byUser);
      AccessibleDescription = value ? "On" : "Off";
      Invalidate();
      CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize) {
      return new Size((int)Math.Round(S(44)), (int)Math.Round(S(24)));
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      PaintBackground(g);
      float t = knob.Value;
      float h = Hover.Value;

      RectangleF track = new RectangleF(S(1), (Height - S(22)) / 2, S(42), S(22));
      Color off = ColorMath.Lerp(Theme.SurfaceSunken,
        Theme.Blend(Theme.SurfaceSunken, Theme.Text, 0.08f), h);
      Color on = ColorMath.Lerp(Theme.Accent,
        Theme.Blend(Theme.Accent, Theme.IsDark ? Color.White : Color.Black, 0.10f), h);
      Color fill = ColorMath.Lerp(off, on, t);
      if (!Enabled)
        fill = Theme.Blend(fill, Behind, 0.5f);
      using (GraphicsPath path = Theme.RoundedRect(track, track.Height / 2))
      using (SolidBrush brush = new SolidBrush(fill)) {
        g.FillPath(brush, path);
        if (t < 0.99f)
          using (Pen border = new Pen(ColorMath.WithAlpha(Theme.Border, 1 - t), Math.Max(1f, S(1))))
            g.DrawPath(border, path);
      }

      float size = S(16) + S(2) * Press.Value;
      float x = track.Left + S(3) + (track.Width - S(6) - size) * t;
      RectangleF knobBounds = new RectangleF(x, track.Top + (track.Height - size) / 2, size, size);
      using (SolidBrush shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
        g.FillEllipse(shadow, RectangleF.Inflate(knobBounds, S(0.5f), S(0.5f)) with { Y = knobBounds.Y + S(1) });
      using (SolidBrush brush = new SolidBrush(Enabled ? Color.White : Theme.Blend(Color.White, Behind, 0.4f)))
        g.FillEllipse(brush, knobBounds);

      DrawFocusRing(g, track, track.Height / 2);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      bool click = e.Button == MouseButtons.Left && Enabled &&
        ClientRectangle.Contains(e.Location);
      base.OnMouseUp(e);
      if (click)
        SetChecked(!isChecked, true);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) {
        e.Handled = true;
        SetChecked(!isChecked, true);
      }
    }
  }

  /// <summary>A row of mutually exclusive choices with a gliding selection pill.</summary>
  public sealed class SegmentedControl : ModernControl {

    private string[] items = Array.Empty<string>();
    private int selectedIndex = -1;
    private int hoverIndex = -1;
    private readonly AnimatedValue pillLeft;
    private readonly AnimatedValue pillWidth;
    private RectangleF[] segments = Array.Empty<RectangleF>();

    public SegmentedControl() {
      pillLeft = new AnimatedValue(this, 0.24f);
      pillWidth = new AnimatedValue(this, 0.24f);
      Cursor = Cursors.Hand;
      AccessibleRole = AccessibleRole.PageTabList;
    }

    public event EventHandler? SelectedIndexChanged;

    public string[] Items {
      get { return items; }
      set {
        items = value ?? Array.Empty<string>();
        Layout();
        Invalidate();
      }
    }

    public int SelectedIndex {
      get { return selectedIndex; }
      set { Select(value, false); }
    }

    private void Select(int index, bool byUser) {
      if (index < -1 || index >= items.Length || index == selectedIndex)
        return;
      selectedIndex = index;
      MovePill(byUser);
      AccessibleDescription = index >= 0 ? items[index] : "";
      Invalidate();
      SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private new void Layout() {
      if (items.Length == 0) {
        segments = Array.Empty<RectangleF>();
        return;
      }
      float padding = S(14);
      float x = S(3);
      segments = new RectangleF[items.Length];
      for (int i = 0; i < items.Length; i++) {
        Size text = TextRenderer.MeasureText(items[i], SemiboldFont, Size.Empty, TextFlags);
        float width = text.Width + 2 * padding;
        segments[i] = new RectangleF(x, S(3), width, Height - S(6));
        x += width;
      }
    }

    private void MovePill(bool animate) {
      if (selectedIndex < 0 || selectedIndex >= segments.Length)
        return;
      pillLeft.Set(segments[selectedIndex].Left, animate);
      pillWidth.Set(segments[selectedIndex].Width, animate);
    }

    protected override void OnFontsChanged() {
      Layout();
      MovePill(false);
    }

    protected override void OnSizeChanged(EventArgs e) {
      base.OnSizeChanged(e);
      Layout();
      MovePill(false);
    }

    public override Size GetPreferredSize(Size proposedSize) {
      Layout();
      float width = segments.Length > 0 ? segments[segments.Length - 1].Right + S(3) : S(40);
      return new Size((int)Math.Ceiling(width), (int)Math.Round(S(34)));
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      PaintBackground(g);
      if (segments.Length != items.Length)
        Layout();

      RectangleF track = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      using (GraphicsPath path = Theme.RoundedRect(track, S(9)))
      using (SolidBrush brush = new SolidBrush(Theme.SurfaceSunken))
        g.FillPath(brush, path);

      if (selectedIndex >= 0 && segments.Length > 0) {
        RectangleF pill = new RectangleF(pillLeft.Value, S(3), pillWidth.Value, Height - S(6));
        RectangleF shadow = pill;
        shadow.Offset(0, S(1));
        using (GraphicsPath path = Theme.RoundedRect(shadow, S(7)))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(Theme.IsDark ? 60 : 22, 0, 0, 0)))
          g.FillPath(brush, path);
        using (GraphicsPath path = Theme.RoundedRect(pill, S(7)))
        using (SolidBrush brush = new SolidBrush(Theme.IsDark
          ? Theme.Blend(Theme.Surface, Theme.Text, 0.10f) : Theme.Surface))
          g.FillPath(brush, path);
      }

      for (int i = 0; i < segments.Length; i++) {
        bool selected = i == selectedIndex;
        Color color = selected ? Theme.Text
          : i == hoverIndex ? Theme.Blend(Theme.TextSecondary, Theme.Text, 0.5f)
          : Theme.TextSecondary;
        if (!Enabled)
          color = Theme.Blend(color, Behind, 0.45f);
        TextRenderer.DrawText(g, items[i], selected ? SemiboldFont : RegularFont,
          Rectangle.Round(segments[i]), color,
          TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      }

      DrawFocusRing(g, track, S(9));
    }

    private int HitTest(Point location) {
      for (int i = 0; i < segments.Length; i++)
        if (segments[i].Contains(location))
          return i;
      return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      int index = HitTest(e.Location);
      if (index != hoverIndex) {
        hoverIndex = index;
        Invalidate();
      }
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      hoverIndex = -1;
      Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button == MouseButtons.Left && Enabled) {
        int index = HitTest(e.Location);
        if (index >= 0)
          Select(index, true);
      }
    }

    protected override bool IsInputKey(Keys keyData) {
      return keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (!Enabled || items.Length == 0)
        return;
      if (e.KeyCode == Keys.Left) {
        Select(Math.Max(0, selectedIndex - 1), true);
        e.Handled = true;
      } else if (e.KeyCode == Keys.Right) {
        Select(Math.Min(items.Length - 1, selectedIndex + 1), true);
        e.Handled = true;
      }
    }
  }

  /// <summary>
  /// A horizontal slider. <see cref="ValueChanged"/> fires while dragging for
  /// live feedback; <see cref="ValueCommitted"/> fires once when the user lets
  /// go, which is when hardware should be told.
  /// </summary>
  public sealed class ModernSlider : ModernControl {

    private float minimum;
    private float maximum = 100;
    private float value;
    private bool dragging;
    private readonly AnimatedValue grow;

    public ModernSlider() {
      grow = new AnimatedValue(this, 0.14f);
      grow.Set(0);
      Cursor = Cursors.Hand;
      AccessibleRole = AccessibleRole.Slider;
    }

    public event EventHandler? ValueChanged;
    public event EventHandler? ValueCommitted;

    public float Minimum {
      get { return minimum; }
      set {
        minimum = value;
        Value = this.value;
        Invalidate();
      }
    }

    public float Maximum {
      get { return maximum; }
      set {
        maximum = Math.Max(minimum, value);
        Value = this.value;
        Invalidate();
      }
    }

    public float Value {
      get { return value; }
      set { SetValue(value, false); }
    }

    private void SetValue(float newValue, bool byUser) {
      newValue = (float)Math.Round(Math.Max(minimum, Math.Min(maximum, newValue)));
      if (Math.Abs(newValue - value) < 0.001f)
        return;
      value = newValue;
      AccessibleDescription = value.ToString("0", CultureInfo.CurrentCulture) + " %";
      Invalidate();
      if (byUser)
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize) {
      return new Size(Math.Max(proposedSize.Width, (int)S(160)), (int)Math.Round(S(28)));
    }

    private RectangleF Track {
      get {
        float inset = S(10);
        return new RectangleF(inset, Height / 2f - S(2), Width - 2 * inset, S(4));
      }
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      PaintBackground(g);
      RectangleF track = Track;
      float fraction = maximum > minimum ? (value - minimum) / (maximum - minimum) : 0;

      using (GraphicsPath path = Theme.RoundedRect(track, track.Height / 2))
      using (SolidBrush brush = new SolidBrush(Theme.SurfaceSunken))
        g.FillPath(brush, path);
      RectangleF filled = new RectangleF(track.X, track.Y,
        Math.Max(track.Height, track.Width * fraction), track.Height);
      Color accent = Enabled ? Theme.Accent : Theme.Blend(Theme.Accent, Behind, 0.55f);
      using (GraphicsPath path = Theme.RoundedRect(filled, track.Height / 2))
      using (SolidBrush brush = new SolidBrush(accent))
        g.FillPath(brush, path);

      float h = Math.Max(Hover.Value, grow.Value);
      float size = S(16) + S(4) * h;
      float cx = track.X + track.Width * fraction;
      RectangleF thumb = new RectangleF(cx - size / 2, Height / 2f - size / 2, size, size);
      if (h > 0.01f) {
        RectangleF halo = RectangleF.Inflate(thumb, S(5) * h, S(5) * h);
        using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(50 * h), Theme.Accent)))
          g.FillEllipse(brush, halo);
      }
      using (SolidBrush brush = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
        g.FillEllipse(brush, thumb with { Y = thumb.Y + S(1) });
      using (SolidBrush brush = new SolidBrush(Color.White))
        g.FillEllipse(brush, thumb);
      using (Pen pen = new Pen(accent, S(2)))
        g.DrawEllipse(pen, RectangleF.Inflate(thumb, -S(1), -S(1)));

      DrawFocusRing(g, new RectangleF(track.X - S(6), Height / 2f - S(10), track.Width + S(12), S(20)), S(10));
    }

    private void SetFromPointer(int x) {
      RectangleF track = Track;
      float fraction = track.Width > 0 ? (x - track.X) / track.Width : 0;
      SetValue(minimum + (maximum - minimum) * Math.Max(0, Math.Min(1, fraction)), true);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (e.Button != MouseButtons.Left || !Enabled)
        return;
      dragging = true;
      Capture = true;
      grow.Set(1);
      SetFromPointer(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      if (dragging)
        SetFromPointer(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (!dragging)
        return;
      dragging = false;
      Capture = false;
      grow.Set(0);
      ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) {
      switch (keyData) {
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
      if (!Enabled)
        return;
      float step;
      switch (e.KeyCode) {
        case Keys.Left:
        case Keys.Down: step = -1; break;
        case Keys.Right:
        case Keys.Up: step = 1; break;
        case Keys.PageDown: step = -10; break;
        case Keys.PageUp: step = 10; break;
        case Keys.Home: step = minimum - value; break;
        case Keys.End: step = maximum - value; break;
        default: return;
      }
      e.Handled = true;
      SetValue(value + step, true);
    }

    protected override void OnKeyUp(KeyEventArgs e) {
      base.OnKeyUp(e);
      switch (e.KeyCode) {
        case Keys.Left:
        case Keys.Right:
        case Keys.Up:
        case Keys.Down:
        case Keys.PageUp:
        case Keys.PageDown:
        case Keys.Home:
        case Keys.End:
          ValueCommitted?.Invoke(this, EventArgs.Empty);
          break;
      }
    }
  }

  public enum ButtonKind {
    Primary,
    Secondary,
    Subtle
  }

  /// <summary>A rounded push button in three weights.</summary>
  public sealed class ModernButton : ModernControl {

    public ModernButton() {
      Cursor = Cursors.Hand;
      AccessibleRole = AccessibleRole.PushButton;
    }

    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text {
      get { return base.Text; }
      set {
        base.Text = value;
        AccessibleName = value;
        Invalidate();
      }
    }

    public override Size GetPreferredSize(Size proposedSize) {
      Size text = TextRenderer.MeasureText(Text, SemiboldFont, Size.Empty, TextFlags);
      return new Size(text.Width + (int)Math.Round(S(32)), (int)Math.Round(S(34)));
    }

    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics;
      PaintBackground(g);
      float h = Hover.Value;
      float p = Press.Value;

      RectangleF bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
      bounds.Inflate(-S(1.5f) * p, -S(1.5f) * p);

      Color fill, text, border = Color.Empty;
      switch (Kind) {
        case ButtonKind.Primary:
          fill = ColorMath.Lerp(Theme.Accent,
            Theme.Blend(Theme.Accent, Theme.IsDark ? Color.White : Color.Black, 0.12f), h);
          text = Theme.IsDark ? Color.FromArgb(0x0B, 0x14, 0x24) : Color.White;
          break;
        case ButtonKind.Subtle:
          fill = ColorMath.Lerp(Behind, Theme.SurfaceSunken, h);
          text = Theme.Accent;
          break;
        default:
          fill = ColorMath.Lerp(Theme.IsDark ? Theme.Blend(Behind, Theme.Text, 0.06f) : Theme.Surface,
            Theme.SurfaceSunken, h);
          text = Theme.Text;
          border = ColorMath.Lerp(Theme.Border, Theme.Blend(Theme.Border, Theme.Text, 0.2f), h);
          break;
      }
      if (!Enabled) {
        fill = Theme.Blend(fill, Behind, 0.5f);
        text = Theme.Blend(text, Behind, 0.5f);
      }

      using (GraphicsPath path = Theme.RoundedRect(bounds, S(8))) {
        using (SolidBrush brush = new SolidBrush(fill))
          g.FillPath(brush, path);
        if (!border.IsEmpty)
          using (Pen pen = new Pen(border, Math.Max(1f, S(1))))
            g.DrawPath(pen, path);
      }
      TextRenderer.DrawText(g, Text, SemiboldFont, Rectangle.Round(bounds), text,
        TextFlags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      DrawFocusRing(g, bounds, S(8));
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      bool click = e.Button == MouseButtons.Left && Enabled &&
        ClientRectangle.Contains(e.Location);
      base.OnMouseUp(e);
      if (click)
        OnClick(EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
      if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) {
        e.Handled = true;
        OnClick(EventArgs.Empty);
      }
    }

    protected override void OnClick(EventArgs e) {
      // Control raises Click from mouse handling itself; only raise it from
      // OnMouseUp and the keyboard so a click fires exactly once.
      if (e is MouseEventArgs)
        return;
      base.OnClick(e);
    }
  }
}
