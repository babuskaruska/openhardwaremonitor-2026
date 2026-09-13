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
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI.Modern {

  public enum Severity {
    Normal,
    Warm,
    Hot
  }

  /// <summary>
  /// Colour, type and shape tokens for the interface, kept in one place so the
  /// overview, the details list, menus and window chrome agree. Separation
  /// comes from surfaces and spacing rather than lines.
  /// </summary>
  public sealed class Theme {

    public static Theme Light { get; } = new Theme(false);
    public static Theme Dark { get; } = new Theme(true);

    /// <summary>Follows the colour mode the application was started with.</summary>
    public static Theme Current {
      get { return Application.IsDarkModeEnabled ? Dark : Light; }
    }

    public bool IsDark { get; }

    /// <summary>Window ground, also used for the title bar.</summary>
    public Color Background { get; }
    /// <summary>Cards and raised panels.</summary>
    public Color Surface { get; }
    public Color SurfaceHover { get; }
    /// <summary>Bar tracks, chips and pressed states.</summary>
    public Color SurfaceSunken { get; }
    public Color Border { get; }
    public Color Text { get; }
    public Color TextSecondary { get; }
    public Color TextTertiary { get; }
    public Color Accent { get; }
    public Color Good { get; }
    public Color Warm { get; }
    public Color Hot { get; }
    public Color Selection { get; }

    private Theme(bool dark) {
      IsDark = dark;
      if (dark) {
        Background = Color.FromArgb(0x14, 0x16, 0x1B);
        Surface = Color.FromArgb(0x1E, 0x21, 0x28);
        SurfaceHover = Color.FromArgb(0x25, 0x29, 0x31);
        SurfaceSunken = Color.FromArgb(0x2C, 0x30, 0x39);
        Border = Color.FromArgb(0x2A, 0x2E, 0x37);
        Text = Color.FromArgb(0xEC, 0xEE, 0xF2);
        TextSecondary = Color.FromArgb(0xA0, 0xA7, 0xB3);
        TextTertiary = Color.FromArgb(0x70, 0x77, 0x84);
        Accent = Color.FromArgb(0x6B, 0xA6, 0xFF);
        Good = Color.FromArgb(0x4A, 0xD6, 0x8F);
        Warm = Color.FromArgb(0xF5, 0xB7, 0x4A);
        Hot = Color.FromArgb(0xFF, 0x6B, 0x6B);
        Selection = Color.FromArgb(0x26, 0x35, 0x4F);
      } else {
        Background = Color.FromArgb(0xF3, 0xF4, 0xF7);
        Surface = Color.White;
        SurfaceHover = Color.FromArgb(0xF7, 0xF8, 0xFA);
        SurfaceSunken = Color.FromArgb(0xEB, 0xED, 0xF1);
        Border = Color.FromArgb(0xE4, 0xE7, 0xEC);
        Text = Color.FromArgb(0x17, 0x1A, 0x21);
        TextSecondary = Color.FromArgb(0x5B, 0x63, 0x70);
        TextTertiary = Color.FromArgb(0x8A, 0x91, 0x9C);
        Accent = Color.FromArgb(0x25, 0x63, 0xEB);
        Good = Color.FromArgb(0x16, 0xA3, 0x4A);
        Warm = Color.FromArgb(0xD9, 0x77, 0x06);
        Hot = Color.FromArgb(0xDC, 0x26, 0x26);
        Selection = Color.FromArgb(0xE3, 0xEC, 0xFD);
      }
    }

    public Color ForSeverity(Severity severity) {
      switch (severity) {
        case Severity.Hot: return Hot;
        case Severity.Warm: return Warm;
        default: return Accent;
      }
    }

    /// <summary>Straight alpha blend of two opaque colours.</summary>
    public static Color Blend(Color background, Color foreground, float amount) {
      amount = Math.Max(0, Math.Min(1, amount));
      return Color.FromArgb(
        (int)Math.Round(background.R + (foreground.R - background.R) * amount),
        (int)Math.Round(background.G + (foreground.G - background.G) * amount),
        (int)Math.Round(background.B + (foreground.B - background.B) * amount));
    }

    // ---- typography ---------------------------------------------------------

    private static string? textFamily;
    private static string? semiboldFamily;
    private static string? displayFamily;

    /// <summary>Body text: Segoe UI Variable on Windows 11, Segoe UI before.</summary>
    public static string TextFamily {
      get { return textFamily ??= Pick("Segoe UI Variable Text", "Segoe UI"); }
    }

    public static string SemiboldFamily {
      get {
        return semiboldFamily ??= Pick("Segoe UI Variable Text Semibold",
          "Segoe UI Semibold", "Segoe UI");
      }
    }

    /// <summary>Large figures.</summary>
    public static string DisplayFamily {
      get {
        return displayFamily ??= Pick("Segoe UI Variable Display Semibold",
          "Segoe UI Semibold", "Segoe UI");
      }
    }

    private static string Pick(params string[] candidates) {
      try {
        using (InstalledFontCollection installed = new InstalledFontCollection()) {
          foreach (string candidate in candidates)
            foreach (FontFamily family in installed.Families)
              if (string.Equals(family.Name, candidate,
                StringComparison.OrdinalIgnoreCase))
                return family.Name;
        }
      } catch (Exception) {
        // Font enumeration is best effort.
      }
      return SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
    }

    /// <summary>
    /// A font sized for a specific DPI. Sizes are given in points at 96 DPI
    /// and created in pixels, so owner-drawn controls stay crisp on every
    /// monitor of a per-monitor-DPI-aware process.
    /// </summary>
    public static Font CreateFont(string family, float points, FontStyle style,
      int dpi) {
      return new Font(family, points * dpi / 72f, style, GraphicsUnit.Pixel);
    }

    /// <summary>Distance from the top of a text cell to the baseline, in pixels.</summary>
    public static float Ascent(Font font) {
      FontFamily family = font.FontFamily;
      return font.Size * family.GetCellAscent(font.Style) /
        family.GetEmHeight(font.Style);
    }

    // ---- shapes -------------------------------------------------------------

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius) {
      GraphicsPath path = new GraphicsPath();
      float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
      if (diameter <= 0.5f) {
        path.AddRectangle(bounds);
        return path;
      }
      RectangleF arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
      path.AddArc(arc, 180, 90);
      arc.X = bounds.Right - diameter;
      path.AddArc(arc, 270, 90);
      arc.Y = bounds.Bottom - diameter;
      path.AddArc(arc, 0, 90);
      arc.X = bounds.Left;
      path.AddArc(arc, 90, 90);
      path.CloseFigure();
      return path;
    }

    // ---- window chrome --------------------------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
      ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;

    /// <summary>
    /// Colours the title bar like the window ground so the frame and content
    /// read as one surface, and asks for rounded corners. Attributes an older
    /// Windows build does not know are ignored.
    /// </summary>
    public void ApplyWindowChrome(Form form) {
      if (!form.IsHandleCreated)
        return;
      try {
        IntPtr handle = form.Handle;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        int corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, 4);
        int caption = ColorTranslator.ToWin32(Background);
        DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, 4);
        int border = ColorTranslator.ToWin32(Background);
        DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, 4);
        int text = ColorTranslator.ToWin32(Text);
        DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref text, 4);
      } catch (Exception) {
        // dwmapi missing or refusing: keep the default frame.
      }
    }

    /// <summary>Small rounded corners for popup menus on Windows 11.</summary>
    public static void ApplyPopupCorners(Control popup) {
      if (!popup.IsHandleCreated)
        return;
      try {
        int corners = DWMWCP_ROUNDSMALL;
        DwmSetWindowAttribute(popup.Handle, DWMWA_WINDOW_CORNER_PREFERENCE,
          ref corners, 4);
      } catch (Exception) {
        // Cosmetic only.
      }
    }
  }
}
