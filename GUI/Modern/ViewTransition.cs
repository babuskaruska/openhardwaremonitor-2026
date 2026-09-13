/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>
  /// Switches between two sibling views with a short cross-fade and a small
  /// horizontal slide: forward (towards more detail) moves left, back moves
  /// right. The live views are swapped immediately; only a snapshot animates
  /// on top, so input goes to the new view at once.
  /// </summary>
  public static class ViewTransition {

    public static void Switch(Control from, Control to, bool forward) {
      Control? host = from.Parent;
      if (host == null || to.Parent != host || !host.IsHandleCreated ||
        !Animator.Enabled || !from.Visible || from.Width < 2 || from.Height < 2) {
        to.Visible = true;
        from.Visible = false;
        return;
      }

      Bitmap before = Capture(from);
      to.Visible = true;
      to.BringToFront();
      from.Visible = false;
      host.PerformLayout();
      Bitmap after = Capture(to);

      TransitionOverlay overlay = new TransitionOverlay(before, after, forward) {
        Bounds = to.Bounds
      };
      host.Controls.Add(overlay);
      overlay.BringToFront();
      overlay.Start();
    }

    private static Bitmap Capture(Control control) {
      Bitmap bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
      try {
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
      } catch (Exception) {
        // A control that cannot print just transitions from blank.
      }
      return bitmap;
    }

    private sealed class TransitionOverlay : Control {

      private const float Seconds = 0.32f;

      private readonly Bitmap before;
      private readonly Bitmap after;
      private readonly bool forward;
      private double start;

      public TransitionOverlay(Bitmap before, Bitmap after, bool forward) {
        this.before = before;
        this.after = after;
        this.forward = forward;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
          ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
      }

      public void Start() {
        start = Animator.Now;
        Animator.Run(() => {
          if (IsDisposed)
            return false;
          if (Animator.Now - start >= Seconds) {
            Parent?.Controls.Remove(this);
            Dispose();
            return false;
          }
          Invalidate();
          return true;
        });
      }

      protected override void OnPaint(PaintEventArgs e) {
        float t = Easing.InOutCubic((float)((Animator.Now - start) / Seconds));
        float distance = 28 * DeviceDpi / 96f;
        float direction = forward ? -1 : 1;

        e.Graphics.DrawImageUnscaled(before, 0, 0);
        DrawFaded(e.Graphics, before, direction * distance * t, 1 - t, true);
        DrawFaded(e.Graphics, after, -direction * distance * (1 - t), t, false);
      }

      private void DrawFaded(Graphics g, Bitmap image, float offsetX, float alpha,
        bool clearFirst) {
        if (clearFirst) {
          using (SolidBrush brush = new SolidBrush(BackColorFrom(after)))
            g.FillRectangle(brush, ClientRectangle);
        }
        if (alpha <= 0)
          return;
        using (ImageAttributes attributes = new ImageAttributes()) {
          ColorMatrix matrix = new ColorMatrix { Matrix33 = Math.Min(1, alpha) };
          attributes.SetColorMatrix(matrix);
          g.DrawImage(image,
            new Rectangle((int)Math.Round(offsetX), 0, image.Width, image.Height),
            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
      }

      private static Color BackColorFrom(Bitmap image) {
        return image.GetPixel(Math.Min(image.Width - 1, 2), Math.Min(image.Height - 1, 2));
      }

      protected override void Dispose(bool disposing) {
        if (disposing) {
          before.Dispose();
          after.Dispose();
        }
        base.Dispose(disposing);
      }
    }
  }
}
