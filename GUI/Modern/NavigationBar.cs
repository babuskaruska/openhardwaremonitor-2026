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
  /// The bar at the top of the window: app mark, page tabs with a gliding
  /// indicator, the Export for AI action and an overflow menu. Replaces the
  /// classic menu bar.
  /// </summary>
  public sealed class NavigationBar : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding |
      TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private const string AppName = "Open Hardware Monitor";
    private const string ExportText = "Export for AI";

    private string[] tabs = Array.Empty<string>();
    private RectangleF[] tabBounds = Array.Empty<RectangleF>();
    private int selectedIndex;
    private int hoverTab = -1;
    private int pressedTarget = -1; // tab index, 100 export, 101 more
    private readonly AnimatedValue indicatorLeft;
    private readonly AnimatedValue indicatorWidth;
    private readonly AnimatedValue exportHover;
    private readonly AnimatedValue moreHover;
    private Rectangle exportBounds, moreBounds;
    private Theme theme = Theme.Current;
    private int fontDpi;
    private Font? appFont, tabFont, tabSelectedFont, buttonFont;
    private bool layoutValid;

    public NavigationBar() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
      indicatorLeft = new AnimatedValue(this, 0.28f);
      indicatorWidth = new AnimatedValue(this, 0.28f);
      exportHover = new AnimatedValue(this, 0.16f);
      exportHover.Set(0);
      moreHover = new AnimatedValue(this, 0.16f);
      moreHover.Set(0);
      AccessibleRole = AccessibleRole.PageTabList;
    }

    public event EventHandler? SelectedIndexChanged;
    public event EventHandler? ExportClicked;

    /// <summary>Raised with the screen point where a menu should open.</summary>
    public event EventHandler<Point>? MoreClicked;

    public string[] Tabs {
      get { return tabs; }
      set {
        tabs = value ?? Array.Empty<string>();
        layoutValid = false;
        Invalidate();
      }
    }

    public int SelectedIndex {
      get { return selectedIndex; }
      set { Select(value, false); }
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

    private void Select(int index, bool byUser) {
      if (index < 0 || index >= tabs.Length || index == selectedIndex && layoutValid)
        return;
      selectedIndex = index;
      EnsureLayout();
      indicatorLeft.Set(tabBounds[index].Left + S(10), byUser);
      indicatorWidth.Set(tabBounds[index].Width - S(20), byUser);
      Invalidate();
      if (byUser)
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && appFont != null)
        return;
      foreach (Font? font in new[] { appFont, tabFont, tabSelectedFont, buttonFont })
        font?.Dispose();
      fontDpi = DeviceDpi;
      appFont = Theme.CreateFont(Theme.SemiboldFamily, 10.5f, FontStyle.Regular, DeviceDpi);
      tabFont = Theme.CreateFont(Theme.TextFamily, 10f, FontStyle.Regular, DeviceDpi);
      tabSelectedFont = Theme.CreateFont(Theme.SemiboldFamily, 10f, FontStyle.Regular, DeviceDpi);
      buttonFont = Theme.CreateFont(Theme.SemiboldFamily, 9.5f, FontStyle.Regular, DeviceDpi);
      int height = (int)Math.Round(S(58));
      if (Height != height)
        Height = height;
      layoutValid = false;
    }

    private void EnsureLayout() {
      EnsureFonts();
      if (layoutValid)
        return;
      float x = S(24) + S(26) + S(10) +
        TextRenderer.MeasureText(AppName, appFont!, Size.Empty, Flags).Width + S(28);
      tabBounds = new RectangleF[tabs.Length];
      for (int i = 0; i < tabs.Length; i++) {
        float width = TextRenderer.MeasureText(tabs[i], tabSelectedFont!, Size.Empty, Flags).Width + S(28);
        tabBounds[i] = new RectangleF(x, S(10), width, Height - S(20));
        x += width;
      }
      layoutValid = true;
      if (selectedIndex >= 0 && selectedIndex < tabs.Length) {
        indicatorLeft.Set(tabBounds[selectedIndex].Left + S(10), false);
        indicatorWidth.Set(tabBounds[selectedIndex].Width - S(20), false);
      }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
      base.OnDpiChangedAfterParent(e);
      fontDpi = 0;
      Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      EnsureLayout();
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureLayout();
      Graphics g = e.Graphics;
      g.Clear(theme.Background);
      g.SmoothingMode = SmoothingMode.AntiAlias;

      // App mark: an accent tile with a small pulse line.
      float markSize = S(26);
      RectangleF mark = new RectangleF(S(24), (Height - markSize) / 2, markSize, markSize);
      using (GraphicsPath path = Theme.RoundedRect(mark, S(8)))
      using (LinearGradientBrush brush = new LinearGradientBrush(mark,
        Theme.Blend(theme.Accent, Color.White, 0.15f), theme.Accent, LinearGradientMode.ForwardDiagonal))
        g.FillPath(brush, path);
      using (Pen pen = new Pen(Color.White, S(1.8f))) {
        pen.LineJoin = LineJoin.Round;
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        float mx = mark.X, my = mark.Y + mark.Height / 2, u = mark.Width / 26;
        g.DrawLines(pen, new[] {
          new PointF(mx + 6 * u, my), new PointF(mx + 10 * u, my),
          new PointF(mx + 12 * u, my - 5 * u), new PointF(mx + 15 * u, my + 5 * u),
          new PointF(mx + 17 * u, my), new PointF(mx + 20 * u, my)
        });
      }
      TextRenderer.DrawText(g, AppName, appFont!,
        new Point((int)(mark.Right + S(10)), (int)((Height - appFont!.Height) / 2)), theme.Text, Flags);

      // Tabs.
      for (int i = 0; i < tabs.Length; i++) {
        bool selected = i == selectedIndex;
        RectangleF bounds = tabBounds[i];
        if (i == hoverTab && !selected) {
          using (GraphicsPath path = Theme.RoundedRect(RectangleF.Inflate(bounds, -S(2), -S(2)), S(8)))
          using (SolidBrush brush = new SolidBrush(theme.SurfaceSunken))
            g.FillPath(brush, path);
        }
        Color color = selected ? theme.Text : i == hoverTab ? theme.Text : theme.TextSecondary;
        TextRenderer.DrawText(g, tabs[i], selected ? tabSelectedFont! : tabFont!,
          Rectangle.Round(bounds), color,
          Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      }
      if (tabs.Length > 0) {
        RectangleF indicator = new RectangleF(indicatorLeft.Value, Height - S(12),
          Math.Max(S(8), indicatorWidth.Value), S(3));
        using (GraphicsPath path = Theme.RoundedRect(indicator, S(1.5f)))
        using (SolidBrush brush = new SolidBrush(theme.Accent))
          g.FillPath(brush, path);
      }

      // Right side: overflow and export.
      float right = Width - S(24);
      float buttonHeight = S(34);
      float top = (Height - buttonHeight) / 2;

      RectangleF more = new RectangleF(right - buttonHeight, top, buttonHeight, buttonHeight);
      if (pressedTarget == 101)
        more.Inflate(-S(1), -S(1));
      float mh = moreHover.Value;
      if (mh > 0.01f) {
        using (GraphicsPath path = Theme.RoundedRect(more, S(8)))
        using (SolidBrush brush = new SolidBrush(ColorMath.WithAlpha(theme.SurfaceSunken, mh)))
          g.FillPath(brush, path);
      }
      using (SolidBrush dots = new SolidBrush(theme.TextSecondary)) {
        float r = S(1.8f);
        for (int i = -1; i <= 1; i++)
          g.FillEllipse(dots, more.X + more.Width / 2 + i * S(6) - r, more.Y + more.Height / 2 - r, 2 * r, 2 * r);
      }
      moreBounds = Rectangle.Round(more);

      Size exportText = TextRenderer.MeasureText(ExportText, buttonFont!, Size.Empty, Flags);
      RectangleF export = new RectangleF(more.Left - S(8) - exportText.Width - S(32), top,
        exportText.Width + S(32), buttonHeight);
      if (pressedTarget == 100)
        export.Inflate(-S(1), -S(1));
      float eh = exportHover.Value;
      using (GraphicsPath path = Theme.RoundedRect(export, S(8)))
      using (SolidBrush brush = new SolidBrush(ColorMath.Lerp(theme.Accent,
        Theme.Blend(theme.Accent, theme.IsDark ? Color.White : Color.Black, 0.12f), eh)))
        g.FillPath(brush, path);
      TextRenderer.DrawText(g, ExportText, buttonFont!, Rectangle.Round(export),
        theme.IsDark ? Color.FromArgb(0x0B, 0x14, 0x24) : Color.White,
        Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      exportBounds = Rectangle.Round(export);
    }

    private int HitTab(Point location) {
      for (int i = 0; i < tabBounds.Length; i++)
        if (tabBounds[i].Contains(location))
          return i;
      return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      int tab = HitTab(e.Location);
      bool overExport = exportBounds.Contains(e.Location);
      bool overMore = moreBounds.Contains(e.Location);
      exportHover.Set(overExport ? 1 : 0);
      moreHover.Set(overMore ? 1 : 0);
      Cursor = tab >= 0 || overExport || overMore ? Cursors.Hand : Cursors.Default;
      if (tab != hoverTab) {
        hoverTab = tab;
        Invalidate();
      }
    }

    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
      hoverTab = -1;
      pressedTarget = -1;
      exportHover.Set(0);
      moreHover.Set(0);
      Cursor = Cursors.Default;
      Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      if (e.Button != MouseButtons.Left)
        return;
      pressedTarget = exportBounds.Contains(e.Location) ? 100
        : moreBounds.Contains(e.Location) ? 101 : HitTab(e.Location);
      Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left)
        return;
      int was = pressedTarget;
      pressedTarget = -1;
      Invalidate();
      if (was == 100 && exportBounds.Contains(e.Location))
        ExportClicked?.Invoke(this, EventArgs.Empty);
      else if (was == 101 && moreBounds.Contains(e.Location))
        MoreClicked?.Invoke(this, PointToScreen(new Point(moreBounds.Right, moreBounds.Bottom + (int)S(4))));
      else if (was >= 0 && was < tabs.Length && HitTab(e.Location) == was)
        Select(was, true);
    }

    protected override void Dispose(bool disposing) {
      if (disposing)
        foreach (Font? font in new[] { appFont, tabFont, tabSelectedFont, buttonFont })
          font?.Dispose();
      base.Dispose(disposing);
    }
  }
}
