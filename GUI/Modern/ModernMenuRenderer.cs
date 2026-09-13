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
  /// Flat menus: no gradients, no image-margin stripe, no 3D borders. Items
  /// highlight with a softly rounded fill.
  /// </summary>
  public sealed class ModernMenuRenderer : ToolStripProfessionalRenderer {

    private readonly Theme theme;

    public ModernMenuRenderer(Theme theme) : base(new FlatColors(theme)) {
      this.theme = theme;
      RoundedEdges = false;
    }

    /// <summary>Applies the renderer and matching colours to a strip.</summary>
    public static void Apply(ToolStrip strip, Theme theme) {
      strip.Renderer = new ModernMenuRenderer(theme);
      strip.BackColor = strip is ToolStripDropDown ? theme.Surface : theme.Background;
      strip.ForeColor = theme.Text;
      if (strip is MenuStrip menu) {
        menu.Padding = new Padding(6, 4, 6, 4);
        foreach (ToolStripItem item in menu.Items)
          item.Padding = new Padding(8, 3, 8, 3);
      }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
      Color fill = e.ToolStrip is ToolStripDropDown ? theme.Surface : theme.Background;
      using (SolidBrush brush = new SolidBrush(fill))
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
      if (e.ToolStrip is ToolStripDropDown dropDown)
        Theme.ApplyPopupCorners(dropDown);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
      if (!(e.ToolStrip is ToolStripDropDown))
        return;
      Rectangle bounds = new Rectangle(Point.Empty, e.ToolStrip.Size);
      bounds.Width -= 1;
      bounds.Height -= 1;
      using (Pen pen = new Pen(theme.Border))
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) {
      using (SolidBrush brush = new SolidBrush(theme.Surface))
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
      ToolStripItem item = e.Item;
      bool open = item is ToolStripMenuItem menuItem && menuItem.DropDown.Visible;
      if (!item.Enabled || !(item.Selected || item.Pressed || open))
        return;

      Rectangle bounds = new Rectangle(Point.Empty, item.Size);
      bounds.Inflate(item.IsOnDropDown ? -4 : -1, item.IsOnDropDown ? -1 : -2);
      Color fill = item.IsOnDropDown || open
        ? theme.SurfaceSunken
        : Theme.Blend(theme.Background, theme.Text, theme.IsDark ? 0.10f : 0.07f);

      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
      using (GraphicsPath path = Theme.RoundedRect(bounds, 5))
      using (SolidBrush brush = new SolidBrush(fill))
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
      e.TextColor = e.Item.Enabled ? theme.Text : theme.TextTertiary;
      base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) {
      e.ArrowColor = theme.TextSecondary;
      base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
      Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
      int y = bounds.Height / 2;
      using (Pen pen = new Pen(theme.Border))
        e.Graphics.DrawLine(pen, bounds.Left + 10, y, bounds.Right - 10, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) {
      Rectangle box = e.ImageRectangle;
      box.Inflate(1, 1);
      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
      using (GraphicsPath path = Theme.RoundedRect(box, 4))
      using (SolidBrush brush = new SolidBrush(theme.Accent))
        e.Graphics.FillPath(brush, path);

      float w = box.Width, h = box.Height;
      PointF[] tick = {
        new PointF(box.Left + w * 0.27f, box.Top + h * 0.52f),
        new PointF(box.Left + w * 0.44f, box.Top + h * 0.68f),
        new PointF(box.Left + w * 0.74f, box.Top + h * 0.34f)
      };
      using (Pen pen = new Pen(Color.White, Math.Max(1.5f, w / 9f))) {
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        e.Graphics.DrawLines(pen, tick);
      }
    }

    private sealed class FlatColors : ProfessionalColorTable {
      private readonly Theme theme;

      public FlatColors(Theme theme) {
        this.theme = theme;
        UseSystemColors = false;
      }

      public override Color MenuBorder { get { return theme.Border; } }
      public override Color MenuItemBorder { get { return Color.Transparent; } }
      public override Color MenuItemSelected { get { return theme.SurfaceSunken; } }
      public override Color MenuStripGradientBegin { get { return theme.Background; } }
      public override Color MenuStripGradientEnd { get { return theme.Background; } }
      public override Color ToolStripDropDownBackground { get { return theme.Surface; } }
      public override Color ImageMarginGradientBegin { get { return theme.Surface; } }
      public override Color ImageMarginGradientMiddle { get { return theme.Surface; } }
      public override Color ImageMarginGradientEnd { get { return theme.Surface; } }
      public override Color SeparatorDark { get { return theme.Border; } }
      public override Color SeparatorLight { get { return theme.Border; } }
    }
  }
}
