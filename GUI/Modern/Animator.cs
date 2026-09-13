/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenHardwareMonitor.GUI.Modern {

  /// <summary>Easing curves; t runs 0..1.</summary>
  public static class Easing {

    public static float Linear(float t) {
      return Clamp(t);
    }

    /// <summary>Fast start, gentle landing. Default for most motion.</summary>
    public static float OutCubic(float t) {
      t = Clamp(t) - 1;
      return t * t * t + 1;
    }

    public static float OutQuint(float t) {
      t = Clamp(t) - 1;
      return t * t * t * t * t + 1;
    }

    public static float InOutCubic(float t) {
      t = Clamp(t);
      return t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>Settles with a slight overshoot, for things that "arrive".</summary>
    public static float OutBack(float t) {
      const float c1 = 1.2f;
      const float c3 = c1 + 1;
      t = Clamp(t) - 1;
      return 1 + c3 * t * t * t + c1 * t * t;
    }

    private static float Clamp(float t) {
      return t < 0 ? 0 : t > 1 ? 1 : t;
    }
  }

  /// <summary>
  /// One frame clock for the whole interface. It only ticks while something
  /// is moving, so an idle window costs nothing; it honours the Windows
  /// "Animation effects" setting and the app's own animation setting.
  /// </summary>
  public static class Animator {

    private static readonly List<Func<bool>> active = new List<Func<bool>>();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static Timer? timer;

    // For measuring and troubleshooting: OHM_ANIMATIONS=0 turns motion off.
    private static readonly bool disabledByEnvironment = string.Equals(
      Environment.GetEnvironmentVariable("OHM_ANIMATIONS"), "0", StringComparison.Ordinal);

    // The Windows setting is read at most every two seconds; Enabled is
    // consulted every time a value changes.
    private static double systemCheckedAt = double.NegativeInfinity;
    private static bool systemEnabled = true;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param,
      ref bool value, uint winIni);

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    /// <summary>Seconds since start, for time-based effects such as pulses.</summary>
    public static double Now {
      get { return clock.Elapsed.TotalSeconds; }
    }

    /// <summary>
    /// False when motion is off: the Windows animation effects setting, the
    /// app setting, or rendering a still. Values then jump to their targets.
    /// </summary>
    public static bool Enabled {
      get {
        if (ForceDisabled || UserDisabled || disabledByEnvironment)
          return false;
        double now = Now;
        if (now - systemCheckedAt > 2) {
          systemCheckedAt = now;
          try {
            bool enabled = true;
            systemEnabled = SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0)
              ? enabled : SystemInformation.UIEffectsEnabled;
          } catch (Exception) {
            systemEnabled = true;
          }
        }
        return systemEnabled;
      }
    }

    /// <summary>For rendering stills (screenshots) without motion.</summary>
    public static bool ForceDisabled { get; set; }

    /// <summary>The user turned animations off in Settings.</summary>
    public static bool UserDisabled { get; set; }

    /// <summary>
    /// Holds every animation, for example while the window is being dragged.
    /// Values keep their targets and land there when resumed.
    /// </summary>
    public static bool Paused { get; set; }

    /// <summary>Number of animations currently running, for diagnostics.</summary>
    public static int ActiveCount {
      get { return active.Count; }
    }

    /// <summary>
    /// Runs <paramref name="frame"/> on every frame until it returns false.
    /// Frames run on the UI thread.
    /// </summary>
    public static void Run(Func<bool> frame) {
      active.Add(frame);
      if (timer == null) {
        timer = new Timer { Interval = 15 };
        timer.Tick += OnTick;
      }
      timer.Enabled = true;
    }

    private static void OnTick(object? sender, EventArgs e) {
      if (Paused)
        return;
      for (int i = active.Count - 1; i >= 0; i--) {
        bool keep;
        try {
          keep = active[i]();
        } catch (Exception) {
          keep = false;
        }
        if (!keep)
          active.RemoveAt(i);
      }
      if (active.Count == 0 && timer != null)
        timer.Enabled = false;
    }

    /// <summary>
    /// True when a control can actually be seen: created, visible along its
    /// parent chain, and its window not minimised. Motion for anything else is
    /// pure cost.
    /// </summary>
    public static bool IsOnScreen(Control control) {
      if (control.IsDisposed || !control.IsHandleCreated || !control.Visible)
        return false;
      Form? form = control.FindForm();
      return form == null || form.WindowState != FormWindowState.Minimized;
    }
  }

  /// <summary>
  /// A value that glides toward its target. Reading <see cref="Value"/> is
  /// cheap. While it moves, the owner is invalidated on each frame - only the
  /// region <see cref="InvalidateRegion"/> returns, when set.
  /// </summary>
  public sealed class AnimatedValue {

    private readonly Control owner;
    private readonly float durationSeconds;
    private readonly Func<float, float> easing;
    private float from;
    private float to;
    private double startTime;
    private bool running;
    private bool initialized;

    public AnimatedValue(Control owner, float durationSeconds = 0.45f,
      Func<float, float>? easing = null) {
      this.owner = owner;
      this.durationSeconds = durationSeconds;
      this.easing = easing ?? Easing.OutCubic;
    }

    /// <summary>The part of the owner that changes with this value.</summary>
    public Func<Rectangle>? InvalidateRegion { get; set; }

    public float Target {
      get { return to; }
    }

    public bool IsAnimating {
      get { return running; }
    }

    public float Value {
      get {
        if (!running)
          return to;
        float t = (float)((Animator.Now - startTime) / durationSeconds);
        return t >= 1 ? to : from + (to - from) * easing(t);
      }
    }

    /// <summary>
    /// Moves toward <paramref name="target"/>. The first call, and any call
    /// while motion is off or the owner cannot be seen, snaps.
    /// </summary>
    public void Set(float target, bool animate = true) {
      if (!initialized || !animate || float.IsNaN(target) || float.IsNaN(to) ||
        !Animator.Enabled || !Animator.IsOnScreen(owner)) {
        bool changed = !initialized || to != target || running;
        initialized = true;
        from = to = target;
        running = false;
        if (changed && initialized)
          InvalidateOwner();
        return;
      }
      if (Math.Abs(target - to) < 0.0001f)
        return;
      from = Value;
      to = target;
      startTime = Animator.Now;
      if (!running) {
        running = true;
        Animator.Run(Frame);
      }
    }

    private void InvalidateOwner() {
      if (owner.IsDisposed || !owner.IsHandleCreated)
        return;
      if (InvalidateRegion != null)
        owner.Invalidate(InvalidateRegion());
      else
        owner.Invalidate();
    }

    private bool Frame() {
      if (owner.IsDisposed) {
        running = false;
        return false;
      }
      InvalidateOwner();
      if (Animator.Now - startTime >= durationSeconds) {
        running = false;
        return false;
      }
      return true;
    }
  }

  public static class ColorMath {

    public static Color Lerp(Color a, Color b, float t) {
      t = t < 0 ? 0 : t > 1 ? 1 : t;
      return Color.FromArgb(
        (int)Math.Round(a.A + (b.A - a.A) * t),
        (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t),
        (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color color, float alpha) {
      int a = (int)Math.Round(color.A * (alpha < 0 ? 0 : alpha > 1 ? 1 : alpha));
      return Color.FromArgb(a, color.R, color.G, color.B);
    }
  }
}
