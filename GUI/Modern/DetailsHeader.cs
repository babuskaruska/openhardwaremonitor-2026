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
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// The slim bar above the full sensor list: a way back to the overview,
  /// the list's title and a hint about what the list can do.
  /// </summary>
  public sealed class DetailsHeader : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private const string BackText = "Overview";

    private Theme theme = Theme.Current;
    private readonly AnimatedValue backHover;
    private bool backPressed;
    private Rectangle backBounds;
    private int fontDpi;
    private Font? buttonFont, titleFont, hintFont;

    public DetailsHeader() {
      SetStyle(ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
        ControlStyles.ResizeRedraw, true);
      backHover = new AnimatedValue(this, 0.18f);
      backHover.Set(0);
      AccessibleRole = AccessibleRole.ToolBar;
    }

    public event EventHandler? BackClicked;

    public string Title { get; set; } = "All sensors";

    public string Hint { get; set; } =
      "Right-click a sensor for options  ·  Esc returns to the overview";

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
      if (fontDpi == DeviceDpi && buttonFont != null)
        return;
      foreach (Font? font in new[] { buttonFont, titleFont, hintFont })
        font?.Dispose();
      fontDpi = DeviceDpi;
      buttonFont = Theme.CreateFont(Theme.SemiboldFamily, 9.5f, FontStyle.Regular, DeviceDpi);
      titleFont = Theme.CreateFont(Theme.DisplayFamily, 14f, FontStyle.Regular, DeviceDpi);
      hintFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
      int height = (int)Math.Ceiling(S(16) + Math.Max(titleFont.Height,
        buttonFont.Height + S(14)) + S(12));
      if (Height != height)
        Height = height;
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      EnsureFonts();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      EnsureFonts();
      Invalidate();
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        foreach (Font? font in new[] { buttonFont, titleFont, hintFont })
          font?.Dispose();
      base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      float margin = S(24);
      float top = S(16);
      float rowHeight = Math.Max(titleFont!.Height, buttonFont!.Height + S(14));

      // Back button: chevron and label in a quiet pill.
      Size labelSize = TextRenderer.MeasureText(BackText, buttonFont, Size.Empty, Flags);
      float chevronWidth = S(8);
      RectangleF back = new RectangleF(margin, top + (rowHeight - (buttonFont.Height + S(14))) / 2,
        S(14) + chevronWidth + S(8) + labelSize.Width + S(16), buttonFont.Height + S(14));
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
          new PointF(cx + S(2.5f), cy - S(5)),
          new PointF(cx - S(2.5f), cy),
          new PointF(cx + S(2.5f), cy + S(5))
        });
      }
      TextRenderer.DrawText(g, BackText, buttonFont,
        new Point((int)(back.Left + S(14) + chevronWidth + S(8)),
          (int)(back.Top + (back.Height - labelSize.Height) / 2)),
        theme.Text, Flags);
      backBounds = Rectangle.Round(back);

      // Title and hint.
      float titleX = back.Right + S(16);
      Size titleSize = TextRenderer.MeasureText(Title, titleFont, Size.Empty, Flags);
      TextRenderer.DrawText(g, Title, titleFont,
        new Point((int)titleX, (int)(top + (rowHeight - titleSize.Height) / 2)),
        theme.Text, Flags);

      Size hintSize = TextRenderer.MeasureText(Hint, hintFont!, Size.Empty, Flags);
      float hintX = Width - margin - hintSize.Width;
      if (hintX > titleX + titleSize.Width + S(24))
        TextRenderer.DrawText(g, Hint, hintFont!,
          new Point((int)hintX, (int)(top + (rowHeight - hintSize.Height) / 2)),
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
        Invalidate();
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
