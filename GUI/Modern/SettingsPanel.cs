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
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// All settings in one scrolling page of titled sections. Rows are declared
  /// by the application with getters and setters, so the page always shows
  /// and changes the real underlying options.
  /// </summary>
  public sealed class SettingsPanel : UserControl {

    private readonly PageHeader header;
    private readonly Panel scroller;
    private readonly List<SettingsSection> sections = new List<SettingsSection>();
    private Theme theme = Theme.Current;

    public SettingsPanel() {
      SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
      header = new PageHeader {
        Dock = DockStyle.Top,
        Title = "Settings",
        Subtitle = "Changes apply immediately unless noted."
      };
      scroller = new BufferedScrollPanel { Dock = DockStyle.Fill, AutoScroll = true };
      scroller.Resize += delegate { LayoutSections(); };
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
      foreach (SettingsSection section in sections)
        section.Theme = theme;
    }

    private int S(float logical) {
      return (int)Math.Round(logical * DeviceDpi / 96f);
    }

    public SettingsSection AddSection(string title, string? description = null) {
      SettingsSection section = new SettingsSection(title, description) { Theme = theme };
      section.HeightChanged += delegate { LayoutSections(); };
      sections.Add(section);
      scroller.Controls.Add(section);
      LayoutSections();
      return section;
    }

    /// <summary>Re-reads every row, for values that change elsewhere.</summary>
    public void RefreshValues() {
      foreach (SettingsSection section in sections)
        section.RefreshValues();
    }

    private void LayoutSections() {
      int margin = S(24), gap = S(16);
      int width = Math.Min(S(820), Math.Max(S(360), scroller.ClientSize.Width - 2 * margin));
      int x = margin;
      Point scroll = scroller.AutoScrollPosition;
      int y = S(4);
      scroller.SuspendLayout();
      foreach (SettingsSection section in sections) {
        int height = section.MeasureHeight(width);
        Rectangle bounds = new Rectangle(x + scroll.X, y + scroll.Y, width, height);
        if (section.Bounds != bounds)
          section.Bounds = bounds;
        y += height + gap;
      }
      scroller.AutoScrollMargin = new Size(0, margin);
      scroller.ResumeLayout(true);
    }
  }

  /// <summary>A titled card holding setting rows.</summary>
  public sealed class SettingsSection : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private readonly string title;
    private readonly string? description;
    private readonly List<SettingRow> rows = new List<SettingRow>();
    private Theme theme = Theme.Current;
    private int fontDpi;
    private Font? titleFont, descriptionFont;
    private int lastHeight;

    internal SettingsSection(string title, string? description) {
      this.title = title;
      this.description = description;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    internal event EventHandler? HeightChanged;

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Background;
        foreach (SettingRow row in rows)
          row.Theme = theme;
        Invalidate(true);
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      titleFont?.Dispose();
      descriptionFont?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.SemiboldFamily, 12f, FontStyle.Regular, DeviceDpi);
      descriptionFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
    }

    // ---- rows -------------------------------------------------------------------

    public void AddToggle(string title, string? description, Func<bool> get, Action<bool> set,
      Func<bool>? enabled = null) {
      ToggleSwitch toggle = new ToggleSwitch();
      SettingRow row = null!;
      row = AddRow(title, description, toggle, () => {
        row.Sync(() => toggle.Checked = get());
        if (enabled != null)
          toggle.Enabled = enabled();
      });
      toggle.CheckedChanged += delegate {
        if (!row.Syncing) {
          set(toggle.Checked);
          row.RefreshValues();
        }
      };
      row.RefreshValues();
    }

    public void AddChoice(string title, string? description, string[] options, Func<int> get,
      Action<int> set) {
      SegmentedControl choice = new SegmentedControl { Items = options };
      SettingRow row = null!;
      row = AddRow(title, description, choice, () => row.Sync(() => choice.SelectedIndex = get()));
      choice.SelectedIndexChanged += delegate {
        if (!row.Syncing && choice.SelectedIndex >= 0) {
          set(choice.SelectedIndex);
          row.RefreshValues();
        }
      };
      row.RefreshValues();
    }

    public void AddButton(string title, string? description, string buttonText, Action action,
      ButtonKind kind = ButtonKind.Secondary, Func<bool>? visible = null) {
      ModernButton button = new ModernButton { Text = buttonText, Kind = kind };
      SettingRow row = AddRow(title, description, button,
        visible == null ? null : () => button.Visible = visible());
      button.Click += delegate { action(); };
      row.RefreshValues();
    }

    public void AddInfo(string title, Func<string> text) {
      SettingRow row = AddRow(title, text(), null, null);
      row.DescriptionProvider = text;
    }

    /// <summary>
    /// Shows the row added last only while <paramref name="visible"/> returns
    /// true, for actions that apply only sometimes (such as downloading an
    /// available update). Re-evaluated whenever the values are refreshed.
    /// </summary>
    public void ShowLastRowWhen(Func<bool> visible) {
      if (rows.Count == 0)
        throw new InvalidOperationException("Add a row first.");
      rows[rows.Count - 1].ShownProvider = visible;
      RefreshValues();
    }

    private SettingRow AddRow(string title, string? description, ModernControl? editor,
      Action? refresh) {
      SettingRow row = new SettingRow(title, description, editor, refresh) { Theme = theme };
      rows.Add(row);
      Controls.Add(row);
      PerformLayout();
      return row;
    }

    public void RefreshValues() {
      foreach (SettingRow row in rows)
        row.RefreshValues();
      int height = MeasureHeight(Width);
      if (height != lastHeight) {
        lastHeight = height;
        HeightChanged?.Invoke(this, EventArgs.Empty);
      }
    }

    // ---- layout and painting -----------------------------------------------------

    internal int MeasureHeight(int width) {
      EnsureFonts();
      float pad = S(22);
      float y = pad + titleFont!.Height;
      if (!string.IsNullOrEmpty(description))
        y += S(4) + TextRenderer.MeasureText(description, descriptionFont!,
          new Size(Math.Max(50, (int)(width - 2 * pad)), int.MaxValue),
          Flags | TextFormatFlags.WordBreak).Height;
      y += S(10);
      foreach (SettingRow row in rows)
        if (row.IsShown)
          y += row.MeasureHeight((int)(width - 2 * pad));
      return (int)Math.Ceiling(y + pad - S(4));
    }

    protected override void OnLayout(LayoutEventArgs levent) {
      base.OnLayout(levent);
      EnsureFonts();
      float pad = S(22);
      float y = pad + titleFont!.Height;
      if (!string.IsNullOrEmpty(description))
        y += S(4) + TextRenderer.MeasureText(description, descriptionFont!,
          new Size(Math.Max(50, (int)(Width - 2 * pad)), int.MaxValue),
          Flags | TextFormatFlags.WordBreak).Height;
      y += S(10);
      int rowWidth = (int)(Width - 2 * pad);
      foreach (SettingRow row in rows) {
        if (!row.IsShown)
          continue;
        int height = row.MeasureHeight(rowWidth);
        row.Bounds = new Rectangle((int)pad, (int)y, rowWidth, height);
        y += height;
      }
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
      float pad = S(22);
      TextRenderer.DrawText(g, title, titleFont!, new Point((int)pad, (int)pad), theme.Text,
        Flags | TextFormatFlags.SingleLine);
      if (!string.IsNullOrEmpty(description)) {
        int width = (int)(Width - 2 * pad);
        TextRenderer.DrawText(g, description, descriptionFont!,
          new Rectangle((int)pad, (int)(pad + titleFont!.Height + S(4)), width, Height),
          theme.TextSecondary, Flags | TextFormatFlags.WordBreak);
      }
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        titleFont?.Dispose();
        descriptionFont?.Dispose();
      }
      base.Dispose(disposing);
    }
  }

  /// <summary>One setting: title and explanation on the left, its control on the right.</summary>
  internal sealed class SettingRow : Control {

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private readonly string title;
    private string? description;
    private readonly ModernControl? editor;
    private readonly Action? refresh;
    private Theme theme = Theme.Current;
    private int fontDpi;
    private Font? titleFont, descriptionFont;

    public SettingRow(string title, string? description, ModernControl? editor, Action? refresh) {
      this.title = title;
      this.description = description;
      this.editor = editor;
      this.refresh = refresh;
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
      if (editor != null)
        Controls.Add(editor);
      AccessibleName = title;
    }

    public bool Syncing { get; private set; }

    public Func<string>? DescriptionProvider { get; set; }

    public Theme Theme {
      get { return theme; }
      set {
        theme = value;
        BackColor = theme.Surface;
        if (editor != null) {
          editor.Theme = theme;
          editor.SurfaceColor = theme.Surface;
        }
        Invalidate(true);
      }
    }

    private float S(float logical) {
      return logical * DeviceDpi / 96f;
    }

    public void Sync(Action apply) {
      Syncing = true;
      try {
        apply();
      } finally {
        Syncing = false;
      }
    }

    /// <summary>Decides <see cref="IsShown"/>; see SettingsSection.ShowLastRowWhen.</summary>
    public Func<bool>? ShownProvider { get; set; }

    /// <summary>Whether the section lays the row out. Unlike Visible, it does not depend on the parent.</summary>
    public bool IsShown { get; private set; } = true;

    public void RefreshValues() {
      if (ShownProvider != null) {
        bool shown = ShownProvider();
        if (shown != IsShown) {
          IsShown = shown;
          Visible = shown;
          Parent?.PerformLayout();
        }
      }
      refresh?.Invoke();
      if (DescriptionProvider != null) {
        string text = DescriptionProvider();
        if (text != description) {
          description = text;
          Invalidate();
        }
      }
    }

    private void EnsureFonts() {
      if (fontDpi == DeviceDpi && titleFont != null)
        return;
      titleFont?.Dispose();
      descriptionFont?.Dispose();
      fontDpi = DeviceDpi;
      titleFont = Theme.CreateFont(Theme.TextFamily, 10f, FontStyle.Regular, DeviceDpi);
      descriptionFont = Theme.CreateFont(Theme.TextFamily, 8.5f, FontStyle.Regular, DeviceDpi);
    }

    private int EditorWidth {
      get { return editor == null || !editor.Visible ? 0 : editor.GetPreferredSize(Size.Empty).Width; }
    }

    public int MeasureHeight(int width) {
      EnsureFonts();
      int textWidth = Math.Max(80, width - EditorWidth - (int)S(24));
      int textHeight = titleFont!.Height;
      if (!string.IsNullOrEmpty(description))
        textHeight += (int)S(2) + TextRenderer.MeasureText(description, descriptionFont!,
          new Size(textWidth, int.MaxValue), Flags | TextFormatFlags.WordBreak).Height;
      int editorHeight = editor == null ? 0 : editor.GetPreferredSize(Size.Empty).Height;
      return Math.Max(textHeight, editorHeight) + (int)S(24);
    }

    protected override void OnLayout(LayoutEventArgs levent) {
      base.OnLayout(levent);
      if (editor == null)
        return;
      Size size = editor.GetPreferredSize(Size.Empty);
      editor.Bounds = new Rectangle(Width - size.Width, (Height - size.Height) / 2,
        size.Width, size.Height);
    }

    protected override void OnPaint(PaintEventArgs e) {
      EnsureFonts();
      Graphics g = e.Graphics;
      g.Clear(theme.Surface);
      int textWidth = Math.Max(80, Width - EditorWidth - (int)S(24));
      int textHeight = titleFont!.Height;
      Size descriptionSize = Size.Empty;
      if (!string.IsNullOrEmpty(description)) {
        descriptionSize = TextRenderer.MeasureText(description, descriptionFont!,
          new Size(textWidth, int.MaxValue), Flags | TextFormatFlags.WordBreak);
        textHeight += (int)S(2) + descriptionSize.Height;
      }
      int y = (Height - textHeight) / 2;
      TextRenderer.DrawText(g, title, titleFont, new Rectangle(0, y, textWidth, titleFont.Height),
        theme.Text, Flags | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
      if (!string.IsNullOrEmpty(description))
        TextRenderer.DrawText(g, description, descriptionFont!,
          new Rectangle(0, y + titleFont.Height + (int)S(2), textWidth, descriptionSize.Height),
          theme.TextSecondary, Flags | TextFormatFlags.WordBreak);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        titleFont?.Dispose();
        descriptionFont?.Dispose();
      }
      base.Dispose(disposing);
    }
  }
}
