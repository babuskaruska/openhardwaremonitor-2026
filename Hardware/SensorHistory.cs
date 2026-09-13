/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace OpenHardwareMonitor.Hardware {

  /// <summary>The samples of a sensor history that fall into one slice of time.</summary>
  public struct HistoryBucket {

    internal float minimum;
    internal float maximum;
    internal double sum;
    internal int count;
    internal bool continues;

    public float Minimum { get { return minimum; } }
    public float Maximum { get { return maximum; } }
    public float Average { get { return count > 0 ? (float)(sum / count) : float.NaN; } }
    public int Count { get { return count; } }
    public bool IsEmpty { get { return count == 0; } }

    /// <summary>
    /// True when recording ran on without a break from the previous non-empty
    /// bucket into this one, so a chart joins the two.
    /// </summary>
    public bool Continues { get { return continues; } }
  }

  /// <summary>Summary of a sensor history over a time range.</summary>
  public struct HistoryStatistics {
    public bool HasData { get; internal set; }
    public float Minimum { get; internal set; }
    public DateTime MinimumTime { get; internal set; }
    public float Maximum { get; internal set; }
    public DateTime MaximumTime { get; internal set; }

    /// <summary>
    /// Weighted by time, so an hour of a steady reading (stored as two
    /// samples) counts as an hour rather than as two samples.
    /// </summary>
    public float Average { get; internal set; }

    /// <summary>The most recent reading in the range.</summary>
    public float Last { get; internal set; }
    public DateTime LastTime { get; internal set; }
  }

  /// <summary>
  /// The recorded history of a sensor: how long and how finely it is kept,
  /// the compact form it is saved in, and downsampling for charts.
  ///
  /// A sensor averages every <see cref="ReadingsPerSample"/> readings into one
  /// sample and keeps the samples of the last <see cref="Retention"/>. At the
  /// default one-second update interval that is a sample every four seconds,
  /// 21,600 a day (two seconds at 0.5 s, 20 seconds at 5 s; drives, which are
  /// read every 30 seconds, one sample every two minutes). A reading that does
  /// not change is stored as just its first and last sample. A NaN sample marks
  /// where recording stopped, for example while the application was closed.
  /// </summary>
  public static class SensorHistory {

    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public const int ReadingsPerSample = 4;

    /// <summary>
    /// Samples further apart than this, and than a few times the usual spacing
    /// of the series, are a break in recording (sleep, hardware turned off)
    /// rather than a line. Equal samples are always joined, because a steady
    /// reading is stored as its first and last sample only.
    /// </summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(60);

    private const int GapSpacings = 5;

    /// <summary>Index of the first sample at or after <paramref name="time"/>, or Count.</summary>
    public static int FindFirst(IReadOnlyList<SensorValue> values, DateTime time) {
      if (values == null)
        throw new ArgumentNullException(nameof(values));
      int low = 0, high = values.Count;
      while (low < high) {
        int middle = low + (high - low) / 2;
        if (values[middle].Time < time)
          low = middle + 1;
        else
          high = middle;
      }
      return low;
    }

    // ---- downsampling ---------------------------------------------------------

    /// <summary>
    /// Splits [<paramref name="start"/>, <paramref name="end"/>) into
    /// <paramref name="bucketCount"/> equal slices and collects the lowest,
    /// highest and average sample of each, for drawing one bucket per pixel.
    ///
    /// A reading that runs across either edge of the range contributes its
    /// value at the edge, so a steady line does not stop short. Buckets that
    /// no sample falls into stay empty; a chart joins non-empty buckets whose
    /// <see cref="HistoryBucket.Continues"/> is set and leaves a gap otherwise.
    /// </summary>
    /// <param name="values">Samples in time order, for example
    /// <see cref="ISensor.Values"/> or a copy of it.</param>
    /// <returns>Statistics over the range.</returns>
    public static HistoryStatistics Downsample(IReadOnlyList<SensorValue> values,
      DateTime start, DateTime end, HistoryBucket[] buckets, int bucketCount) {
      if (values == null)
        throw new ArgumentNullException(nameof(values));
      if (buckets == null)
        throw new ArgumentNullException(nameof(buckets));
      if (bucketCount < 1 || bucketCount > buckets.Length)
        throw new ArgumentOutOfRangeException(nameof(bucketCount));
      if (end <= start)
        throw new ArgumentException("The range must not be empty.", nameof(end));

      Array.Clear(buckets, 0, bucketCount);
      Accumulator accumulator = new Accumulator(buckets, bucketCount, start, end);

      int first = FindFirst(values, start);
      int stop = FindFirst(values, end);
      long gap = GapTicks(values, Math.Max(0, first - 1), Math.Min(values.Count - 1, stop));

      bool hasPrevious = false;
      SensorValue previous = default;
      if (first > 0) {
        previous = values[first - 1];
        hasPrevious = float.IsFinite(previous.Value);
      }

      for (int i = first; i < stop; i++) {
        SensorValue sample = values[i];
        if (!float.IsFinite(sample.Value)) {
          hasPrevious = false;
          continue;
        }
        bool joined = hasPrevious && Joined(previous, sample, gap);
        if (joined && previous.Time < start)
          accumulator.Add(ValueAt(previous, sample, start), start, false);
        accumulator.Add(sample.Value, sample.Time, joined);
        previous = sample;
        hasPrevious = true;
      }

      // The reading runs on past the end of the range.
      if (hasPrevious && stop < values.Count) {
        SensorValue next = values[stop];
        if (float.IsFinite(next.Value) && Joined(previous, next, gap)) {
          if (previous.Time < start)
            accumulator.Add(ValueAt(previous, next, start), start, false);
          accumulator.Add(ValueAt(previous, next, end), end, true);
        }
      }

      return accumulator.Statistics();
    }

    private static bool Joined(SensorValue a, SensorValue b, long gapTicks) {
      return b.Time.Ticks - a.Time.Ticks <= gapTicks || a.Value == b.Value;
    }

    private static float ValueAt(SensorValue a, SensorValue b, DateTime time) {
      long span = b.Time.Ticks - a.Time.Ticks;
      if (span <= 0)
        return b.Value;
      double t = (double)(time.Ticks - a.Time.Ticks) / span;
      t = Math.Max(0, Math.Min(1, t));
      return (float)(a.Value + (b.Value - a.Value) * t);
    }

    /// <summary>
    /// The largest spacing still drawn as a line: a few times the median
    /// spacing of a handful of samples spread over the range, and at least
    /// <see cref="MinimumGap"/>. Pairs with equal readings are skipped, since
    /// those are joined anyway and would inflate the estimate.
    /// </summary>
    private static long GapTicks(IReadOnlyList<SensorValue> values, int from, int to) {
      Span<long> spacings = stackalloc long[15];
      int found = 0;
      int pairs = to - from;
      if (pairs > 0) {
        int step = Math.Max(1, pairs / spacings.Length);
        for (int i = from; i < to && found < spacings.Length; i += step) {
          SensorValue a = values[i], b = values[i + 1];
          long spacing = b.Time.Ticks - a.Time.Ticks;
          if (spacing > 0 && a.Value != b.Value &&
            float.IsFinite(a.Value) && float.IsFinite(b.Value))
            spacings[found++] = spacing;
        }
      }
      long typical = 0;
      if (found > 0) {
        Span<long> used = spacings.Slice(0, found);
        used.Sort();
        typical = used[found / 2];
      }
      return Math.Max(MinimumGap.Ticks, typical * GapSpacings);
    }

    private struct Accumulator {

      private readonly HistoryBucket[] buckets;
      private readonly int bucketCount;
      private readonly long startTicks;
      private readonly long spanTicks;

      private HistoryStatistics statistics;
      private double area, duration, sum;
      private int count;
      private long lastTicks;
      private float lastValue;

      public Accumulator(HistoryBucket[] buckets, int bucketCount, DateTime start,
        DateTime end) {
        this.buckets = buckets;
        this.bucketCount = bucketCount;
        startTicks = start.Ticks;
        spanTicks = end.Ticks - start.Ticks;
        statistics = default;
        area = duration = sum = 0;
        count = 0;
        lastTicks = 0;
        lastValue = 0;
      }

      public void Add(float value, DateTime time, bool continues) {
        long offset = time.Ticks - startTicks;
        int index = (int)Math.Max(0, Math.Min(bucketCount - 1,
          offset * bucketCount / spanTicks));
        ref HistoryBucket bucket = ref buckets[index];
        if (bucket.count == 0) {
          bucket.minimum = value;
          bucket.maximum = value;
          bucket.continues = continues;
        } else {
          if (value < bucket.minimum)
            bucket.minimum = value;
          if (value > bucket.maximum)
            bucket.maximum = value;
        }
        bucket.sum += value;
        bucket.count++;

        if (!statistics.HasData || value < statistics.Minimum) {
          statistics.Minimum = value;
          statistics.MinimumTime = time;
        }
        if (!statistics.HasData || value > statistics.Maximum) {
          statistics.Maximum = value;
          statistics.MaximumTime = time;
        }
        statistics.HasData = true;
        statistics.Last = value;
        statistics.LastTime = time;

        if (continues && count > 0) {
          double seconds = (time.Ticks - lastTicks) / (double)TimeSpan.TicksPerSecond;
          area += (lastValue + (double)value) / 2 * seconds;
          duration += seconds;
        }
        sum += value;
        count++;
        lastTicks = time.Ticks;
        lastValue = value;
      }

      public HistoryStatistics Statistics() {
        if (statistics.HasData)
          statistics.Average = (float)(duration > 0 ? area / duration : sum / count);
        return statistics;
      }
    }

    // ---- storage --------------------------------------------------------------

    private const string Version2Prefix = "2:";
    private const byte Version2 = 2;
    private const int MaximumStoredSamples = 10000000;

    /// <summary>
    /// Readings are stored rounded to about the precision the interface shows
    /// them with: millivolts, whole RPM and MHz, 10 KB/s, a tenth of anything
    /// else. Anything finer is noise, which does not compress; a day of a busy
    /// sensor measured 25-45 KB this way against 90-140 KB unrounded.
    /// </summary>
    internal static int StoredDecimals(SensorType sensorType) {
      switch (sensorType) {
        case SensorType.Voltage:
        case SensorType.Factor:
          return 3;
        case SensorType.Throughput:
          return 2;
        case SensorType.Fan:
        case SensorType.Flow:
        case SensorType.Clock:
          return 0;
        default:
          return 1;
      }
    }

    /// <summary>
    /// Compresses samples into a settings string.
    ///
    /// Format 2 is "2:" and Base64 of GZip data: version, decimals, count, the
    /// first time in whole seconds, then every time as a delta in seconds, then
    /// every reading as a zig-zag delta of a fixed-point number (odd codes mark
    /// NaN, or a raw float for values that do not fit). Times and readings are
    /// kept in separate runs because each is far more regular on its own. The
    /// original format (Base64 of GZip of 12 bytes per sample) spent most of
    /// its size on sub-millisecond time jitter and float noise.
    /// </summary>
    public static string Encode(SensorValue[] values, int count, SensorType sensorType) {
      if (values == null)
        throw new ArgumentNullException(nameof(values));
      if (count < 0 || count > values.Length)
        throw new ArgumentOutOfRangeException(nameof(count));
      if (count == 0)
        return "";

      int decimals = StoredDecimals(sensorType);
      double scale = Math.Pow(10, decimals);
      double limit = (double)(1L << 50);

      ByteWriter writer = new ByteWriter(count * 3 + 32);
      writer.WriteByte(Version2);
      writer.WriteByte((byte)decimals);
      writer.WriteVarint((ulong)count);

      long previousSecond = 0;
      for (int i = 0; i < count; i++) {
        long second = Math.Max(0, values[i].Time.Ticks / TimeSpan.TicksPerSecond);
        if (i == 0) {
          writer.WriteVarint((ulong)second);
        } else {
          writer.WriteVarint((ulong)Math.Max(0, second - previousSecond));
          second = Math.Max(second, previousSecond);
        }
        previousSecond = second;
      }

      long previousFixed = 0;
      for (int i = 0; i < count; i++) {
        float value = values[i].Value;
        double scaled = value * scale;
        if (float.IsNaN(value)) {
          writer.WriteVarint(1);
        } else if (double.IsInfinity(scaled) || Math.Abs(scaled) > limit) {
          writer.WriteVarint(3);
          writer.WriteSingle(value);
        } else {
          long fixedValue = (long)Math.Round(scaled);
          long delta = fixedValue - previousFixed;
          writer.WriteVarint(((ulong)((delta << 1) ^ (delta >> 63))) << 1);
          previousFixed = fixedValue;
        }
      }

      using (MemoryStream memory = new MemoryStream(writer.Length / 2 + 64)) {
        using (GZipStream gzip = new GZipStream(memory, CompressionLevel.Optimal, true))
          gzip.Write(writer.Buffer, 0, writer.Length);
        return Version2Prefix + Convert.ToBase64String(memory.GetBuffer(), 0,
          (int)memory.Length);
      }
    }

    /// <summary>
    /// Reads a settings string written by <see cref="Encode"/> or by the
    /// original format. Damaged data yields the samples that could be read,
    /// or none.
    /// </summary>
    public static List<SensorValue> Decode(string? text) {
      List<SensorValue> result = new List<SensorValue>();
      if (string.IsNullOrEmpty(text))
        return result;
      try {
        if (text.StartsWith(Version2Prefix, StringComparison.Ordinal))
          DecodeVersion2(Convert.FromBase64String(text.Substring(Version2Prefix.Length)), result);
        else
          DecodeVersion1(Convert.FromBase64String(text), result);
      } catch (Exception) {
        // A damaged history is not worth failing a sensor for.
      }
      return result;
    }

    private static void DecodeVersion1(byte[] data, List<SensorValue> result) {
      using (MemoryStream memory = new MemoryStream(data))
      using (GZipStream gzip = new GZipStream(memory, CompressionMode.Decompress))
      using (BinaryReader reader = new BinaryReader(gzip)) {
        long time = 0;
        try {
          while (result.Count < MaximumStoredSamples) {
            time += reader.ReadInt64();
            DateTime stamp = DateTime.FromBinary(time);
            float value = reader.ReadSingle();
            result.Add(new SensorValue(value, stamp));
          }
        } catch (EndOfStreamException) {
        }
      }
    }

    private static void DecodeVersion2(byte[] data, List<SensorValue> result) {
      byte[] payload;
      using (MemoryStream memory = new MemoryStream(data))
      using (GZipStream gzip = new GZipStream(memory, CompressionMode.Decompress))
      using (MemoryStream output = new MemoryStream()) {
        gzip.CopyTo(output);
        payload = output.ToArray();
      }

      ByteReader reader = new ByteReader(payload);
      if (reader.ReadByte() != Version2)
        return;
      int decimals = reader.ReadByte();
      ulong count = reader.ReadVarint();
      if (decimals > 9 || count > MaximumStoredSamples)
        return;
      double scale = Math.Pow(10, decimals);

      long[] seconds = new long[count];
      long second = 0;
      for (ulong i = 0; i < count; i++) {
        second = i == 0 ? (long)reader.ReadVarint() : second + (long)reader.ReadVarint();
        seconds[i] = second;
      }

      long maximumSecond = DateTime.MaxValue.Ticks / TimeSpan.TicksPerSecond;
      result.Capacity = (int)count;
      long fixedValue = 0;
      for (ulong i = 0; i < count; i++) {
        ulong code = reader.ReadVarint();
        float value;
        if ((code & 1) == 0) {
          ulong zigzag = code >> 1;
          fixedValue += (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
          value = (float)(fixedValue / scale);
        } else if (code == 1) {
          value = float.NaN;
        } else {
          value = reader.ReadSingle();
        }
        if (seconds[i] < 0 || seconds[i] > maximumSecond)
          return;
        result.Add(new SensorValue(value,
          new DateTime(seconds[i] * TimeSpan.TicksPerSecond, DateTimeKind.Utc)));
      }
    }

    private sealed class ByteWriter {

      private byte[] buffer;
      private int length;

      public ByteWriter(int capacity) {
        buffer = new byte[Math.Max(16, capacity)];
      }

      public byte[] Buffer { get { return buffer; } }
      public int Length { get { return length; } }

      private void Reserve(int bytes) {
        if (length + bytes > buffer.Length)
          Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + bytes));
      }

      public void WriteByte(byte value) {
        Reserve(1);
        buffer[length++] = value;
      }

      public void WriteVarint(ulong value) {
        Reserve(10);
        while (value >= 0x80) {
          buffer[length++] = (byte)(value | 0x80);
          value >>= 7;
        }
        buffer[length++] = (byte)value;
      }

      public void WriteSingle(float value) {
        Reserve(4);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, length, 4), value);
        length += 4;
      }
    }

    private struct ByteReader {

      private readonly byte[] data;
      private int position;

      public ByteReader(byte[] data) {
        this.data = data;
        position = 0;
      }

      public byte ReadByte() {
        if (position >= data.Length)
          throw new EndOfStreamException();
        return data[position++];
      }

      public ulong ReadVarint() {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 7) {
          byte b = ReadByte();
          result |= (ulong)(b & 0x7F) << shift;
          if (b < 0x80)
            return result;
        }
        throw new InvalidDataException();
      }

      public float ReadSingle() {
        if (position + 4 > data.Length)
          throw new EndOfStreamException();
        float value = BitConverter.ToSingle(data, position);
        position += 4;
        return value;
      }
    }
  }
}
