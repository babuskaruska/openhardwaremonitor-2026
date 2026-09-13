/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using OpenHardwareMonitor.Collections;
using OpenHardwareMonitor.Hardware;
using Xunit;
using Xunit.Abstractions;

namespace OpenHardwareMonitor.Tests {

  /// <summary>
  /// Sensor history: retention and restore, the stored format, and the
  /// downsampling behind the history chart.
  /// </summary>
  public class SensorHistoryTests {

    private static readonly DateTime T0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly ITestOutputHelper output;

    public SensorHistoryTests(ITestOutputHelper output) {
      this.output = output;
    }

    private static SensorValue At(double seconds, float value) {
      return new SensorValue(value, T0.AddSeconds(seconds));
    }

    private sealed class MemorySettings : ISettings {
      private readonly Dictionary<string, string> values = new Dictionary<string, string>();

      public bool Contains(string name) {
        return values.ContainsKey(name);
      }

      public void SetValue(string name, string value) {
        values[name] = value;
      }

      public string GetValue(string name, string value) {
        return values.TryGetValue(name, out string? stored) ? stored : value;
      }

      public void Remove(string name) {
        values.Remove(name);
      }
    }

    private sealed class TestHardware : Hardware.Hardware {
      public TestHardware(ISettings settings)
        : base("Test board", new Identifier("test"), settings) { }

      public override HardwareType HardwareType {
        get { return HardwareType.Mainboard; }
      }

      public override void Update() { }
    }

    /// <summary>The format used before 2026: GZip of time deltas and raw floats.</summary>
    private static string EncodeVersion1(IEnumerable<SensorValue> values) {
      using (MemoryStream memory = new MemoryStream()) {
        using (GZipStream gzip = new GZipStream(memory, CompressionMode.Compress))
        using (BinaryWriter writer = new BinaryWriter(gzip)) {
          long previous = 0;
          foreach (SensorValue value in values) {
            long time = value.Time.ToBinary();
            writer.Write(time - previous);
            previous = time;
            writer.Write(value.Value);
          }
        }
        return Convert.ToBase64String(memory.ToArray());
      }
    }

    // ---- collection -------------------------------------------------------------

    [Fact]
    public void RingCollectionCopiesAcrossTheWrapAround() {
      RingCollection<int> ring = new RingCollection<int>(4);
      for (int i = 0; i < 4; i++)
        ring.Append(i);
      ring.Remove();
      ring.Remove();
      ring.Append(4);
      ring.Append(5);

      int[] copy = new int[6];
      ring.CopyTo(copy, 1);
      Assert.Equal(new[] { 0, 2, 3, 4, 5, 0 }, copy);
      IReadOnlyList<int> list = ring;
      Assert.Equal(4, list.Count);
      Assert.Equal(4, list[2]);
    }

    [Fact]
    public void FindFirstLocatesTheStartOfARange() {
      SensorValue[] values = { At(0, 1), At(4, 2), At(8, 3), At(12, 4) };
      Assert.Equal(0, SensorHistory.FindFirst(values, T0.AddSeconds(-1)));
      Assert.Equal(1, SensorHistory.FindFirst(values, T0.AddSeconds(4)));
      Assert.Equal(2, SensorHistory.FindFirst(values, T0.AddSeconds(5)));
      Assert.Equal(4, SensorHistory.FindFirst(values, T0.AddSeconds(13)));
    }

    // ---- storage ------------------------------------------------------------------

    [Fact]
    public void RoundTripKeepsTimesToTheSecondAndReadingsToTheStoredPrecision() {
      SensorValue[] values = {
        new SensorValue(45.25f, T0.AddMilliseconds(4003)),
        new SensorValue(45.5f, T0.AddMilliseconds(8001)),
        new SensorValue(47.126f, T0.AddMilliseconds(11998)),
        new SensorValue(-3.5f, T0.AddMilliseconds(16010))
      };
      List<SensorValue> decoded = SensorHistory.Decode(
        SensorHistory.Encode(values, values.Length, SensorType.Temperature));

      Assert.Equal(values.Length, decoded.Count);
      for (int i = 0; i < values.Length; i++) {
        Assert.Equal(DateTimeKind.Utc, decoded[i].Time.Kind);
        Assert.True(Math.Abs((decoded[i].Time - values[i].Time).TotalSeconds) < 1);
        Assert.True(decoded[i].Time <= values[i].Time);
        Assert.True(Math.Abs(values[i].Value - decoded[i].Value) <= 0.05f + 1e-4f);
      }
      Assert.Equal(47.1f, decoded[2].Value, 4);
    }

    [Fact]
    public void RoundTripKeepsGapMarkersAndReadingsThatDoNotFit() {
      SensorValue[] values = {
        At(0, 1200), At(4, float.NaN), At(8, float.PositiveInfinity), At(12, 3e30f), At(16, 1210)
      };
      List<SensorValue> decoded = SensorHistory.Decode(
        SensorHistory.Encode(values, values.Length, SensorType.Fan));

      Assert.Equal(5, decoded.Count);
      Assert.Equal(1200f, decoded[0].Value);
      Assert.True(float.IsNaN(decoded[1].Value));
      Assert.Equal(float.PositiveInfinity, decoded[2].Value);
      Assert.Equal(3e30f, decoded[3].Value);
      Assert.Equal(1210f, decoded[4].Value);
      Assert.Equal(T0.AddSeconds(16), decoded[4].Time);
    }

    [Fact]
    public void EncodesOnlyTheGivenCount() {
      SensorValue[] buffer = { At(0, 1), At(4, 2), At(8, 3) };
      Assert.Equal(2, SensorHistory.Decode(SensorHistory.Encode(buffer, 2, SensorType.Load)).Count);
      Assert.Equal("", SensorHistory.Encode(buffer, 0, SensorType.Load));
    }

    [Fact]
    public void ReadsTheOriginalFormat() {
      SensorValue[] values = {
        new SensorValue(1.234f, T0.AddTicks(12345)), new SensorValue(float.NaN, T0.AddSeconds(4)),
        new SensorValue(5.5f, T0.AddSeconds(8.5))
      };
      List<SensorValue> decoded = SensorHistory.Decode(EncodeVersion1(values));

      Assert.Equal(3, decoded.Count);
      Assert.Equal(values[0].Time, decoded[0].Time);
      Assert.Equal(1.234f, decoded[0].Value);
      Assert.True(float.IsNaN(decoded[1].Value));
      Assert.Equal(values[2].Time, decoded[2].Time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("2:AAAA")]
    [InlineData("H4sIAAAAAAAACg==")]
    public void DamagedHistoryYieldsNoSamples(string? text) {
      Assert.Empty(SensorHistory.Decode(text));
    }

    /// <summary>
    /// A day of four-second samples with realistic noise. The original format
    /// needed about 90 KB for each varying sensor; with around 200 sensors a
    /// settings file of tens of megabytes would be rewritten every save.
    /// </summary>
    [Fact]
    public void ADayOfVaryingReadingsStaysSmall() {
      Random random = new Random(7);
      int count = (int)(SensorHistory.Retention.TotalSeconds / 4);
      SensorValue[] temperature = new SensorValue[count];
      SensorValue[] load = new SensorValue[count];
      SensorValue[] voltage = new SensorValue[count];
      SensorValue[] fan = new SensorValue[count];
      double heat = 45, work = 20;
      for (int i = 0; i < count; i++) {
        DateTime time = T0.AddMilliseconds(4000.0 * i + random.Next(-15, 16));
        work = Math.Max(0, Math.Min(100, work + (random.NextDouble() - 0.5) * 12));
        heat += ((35 + work * 0.5) - heat) * 0.05;
        // Four whole-degree readings averaged.
        float degrees = (float)(Math.Round(4 * (heat + random.NextDouble() - 0.5)) / 4);
        temperature[i] = new SensorValue(degrees, time);
        load[i] = new SensorValue((float)(work + random.NextDouble() * 3), time);
        voltage[i] = new SensorValue((float)(1.2 + random.Next(-6, 7) * 0.001 / 4 * 4), time);
        fan[i] = new SensorValue((float)(1150 + work * 4 + random.Next(-12, 13)), time);
      }

      int total = 0, totalVersion1 = 0;
      foreach ((string name, SensorValue[] values, SensorType type) in new[] {
        ("temperature", temperature, SensorType.Temperature), ("load", load, SensorType.Load),
        ("voltage", voltage, SensorType.Voltage), ("fan", fan, SensorType.Fan) }) {
        int size = SensorHistory.Encode(values, count, type).Length;
        int sizeVersion1 = EncodeVersion1(values).Length;
        output.WriteLine("{0}: {1:N0} characters, originally {2:N0}", name, size, sizeVersion1);
        total += size;
        totalVersion1 += sizeVersion1;
      }
      output.WriteLine("average per sensor: {0:N0}, originally {1:N0}", total / 4, totalVersion1 / 4);

      Assert.True(total / 4 < 40000, "A day of one sensor should take under 40 KB.");
      Assert.True(total * 3 < totalVersion1, "Should be at least three times smaller.");
    }

    // ---- downsampling -------------------------------------------------------------

    [Fact]
    public void BucketsCollectMinimumAverageAndMaximum() {
      SensorValue[] values = { At(1, 10), At(5, 20), At(9, 30), At(21, 5), At(25, 7) };
      HistoryBucket[] buckets = new HistoryBucket[4];
      HistoryStatistics statistics = SensorHistory.Downsample(values,
        T0, T0.AddSeconds(40), buckets, 3);

      // Buckets of 13.3 s: [1, 5, 9], [21, 25], nothing.
      Assert.Equal(3, buckets[0].Count);
      Assert.Equal(10f, buckets[0].Minimum);
      Assert.Equal(30f, buckets[0].Maximum);
      Assert.Equal(20f, buckets[0].Average);
      Assert.False(buckets[0].Continues);
      Assert.Equal(2, buckets[1].Count);
      Assert.Equal(6f, buckets[1].Average);
      Assert.True(buckets[1].Continues);
      Assert.True(buckets[2].IsEmpty);
      Assert.True(float.IsNaN(buckets[2].Average));

      Assert.True(statistics.HasData);
      Assert.Equal(5f, statistics.Minimum);
      Assert.Equal(T0.AddSeconds(21), statistics.MinimumTime);
      Assert.Equal(30f, statistics.Maximum);
      Assert.Equal(T0.AddSeconds(9), statistics.MaximumTime);
      Assert.Equal(7f, statistics.Last);
    }

    [Fact]
    public void AGapMarkerBreaksTheLine() {
      // Recording, the application closed, recording again after a restart.
      SensorValue[] values = { At(0, 1), At(4, 2), At(10, float.NaN), At(14, 3), At(18, 4) };
      HistoryBucket[] buckets = new HistoryBucket[5];
      SensorHistory.Downsample(values, T0, T0.AddSeconds(20), buckets, 5);

      Assert.Equal(2f, buckets[1].Average);
      Assert.True(buckets[1].Continues);
      Assert.True(buckets[2].IsEmpty);
      Assert.Equal(3f, buckets[3].Average);
      Assert.False(buckets[3].Continues);
      Assert.True(buckets[4].Continues);
    }

    [Fact]
    public void ASteadyReadingIsJoinedAcrossEmptyBucketsAndTheRangeEdges() {
      // A reading that did not change for two hours is stored as two samples.
      SensorValue[] values = { At(-3600, 800), At(3600, 800), At(3604, 900) };
      HistoryBucket[] buckets = new HistoryBucket[60];
      HistoryStatistics statistics = SensorHistory.Downsample(values,
        T0, T0.AddSeconds(600), buckets, 60);

      Assert.Equal(800f, buckets[0].Average);
      Assert.False(buckets[0].Continues);
      Assert.True(buckets.Skip(1).Take(58).All(b => b.IsEmpty));
      Assert.Equal(800f, buckets[59].Average);
      Assert.True(buckets[59].Continues);
      Assert.Equal(800f, statistics.Minimum);
      Assert.Equal(800f, statistics.Maximum);
      Assert.Equal(800f, statistics.Average);
    }

    [Fact]
    public void ALongPauseWithChangingReadingsIsAGap() {
      // The computer slept for an hour between two ordinary samples.
      List<SensorValue> values = new List<SensorValue>();
      for (int i = 0; i < 20; i++)
        values.Add(At(i * 4, 40 + i % 3));
      for (int i = 0; i < 20; i++)
        values.Add(At(3600 + i * 4, 50 + i % 3));
      HistoryBucket[] buckets = new HistoryBucket[400];
      SensorHistory.Downsample(values, T0, T0.AddSeconds(4000), buckets, 400);

      int resumed = (int)(3600 / 10.0);
      Assert.False(buckets[resumed].Continues);
      Assert.True(buckets[resumed + 1].Continues);
    }

    [Fact]
    public void ReadingsOfSlowSensorsAreJoinedAtTheirOwnSpacing() {
      // Drives are read every 30 seconds: a sample every two minutes.
      List<SensorValue> values = new List<SensorValue>();
      for (int i = 0; i < 30; i++)
        values.Add(At(i * 120, 35 + i % 2));
      HistoryBucket[] buckets = new HistoryBucket[360];
      SensorHistory.Downsample(values, T0, T0.AddHours(1), buckets, 360);

      for (int i = 1; i < 30; i++)
        Assert.True(buckets[i * 12].Continues);
    }

    [Fact]
    public void AverageIsWeightedByTime() {
      // 0 for 90 seconds, then 10 for 30 seconds, the first stored compactly.
      SensorValue[] values = { At(0, 0), At(90, 0), At(90, 10), At(120, 10) };
      HistoryStatistics statistics = SensorHistory.Downsample(values,
        T0, T0.AddSeconds(120), new HistoryBucket[12], 12);

      Assert.Equal(2.5f, statistics.Average, 3);
    }

    [Fact]
    public void AnEmptyHistoryHasNoStatistics() {
      HistoryStatistics statistics = SensorHistory.Downsample(Array.Empty<SensorValue>(),
        T0, T0.AddMinutes(10), new HistoryBucket[8], 8);
      Assert.False(statistics.HasData);
    }

    // ---- sensor ------------------------------------------------------------------------

    [Fact]
    public void SensorWritesItsHistoryWhenTheHardwareCloses() {
      MemorySettings settings = new MemorySettings();
      TestHardware hardware = new TestHardware(settings);
      Sensor sensor = new Sensor("Package", 0, SensorType.Temperature, hardware, settings);
      for (int i = 1; i <= 9; i++)
        sensor.Value = i;
      hardware.Close();

      string key = new Identifier(sensor.Identifier, "values").ToString();
      string stored = settings.GetValue(key, "");
      Assert.StartsWith("2:", stored);
      List<SensorValue> decoded = SensorHistory.Decode(stored);
      Assert.Equal(new[] { 2.5f, 6.5f }, decoded.Select(v => v.Value));
      Assert.True(sensor.IsClosed);
    }

    [Fact]
    public void SensorRestoresRecentHistoryAfterARestartWithAGapMarker() {
      MemorySettings settings = new MemorySettings();
      TestHardware hardware = new TestHardware(settings);
      string key = new Identifier(new Identifier("test"), "load", "3", "values").ToString();
      DateTime now = DateTime.UtcNow;
      SensorValue[] stored = {
        new SensorValue(10, now.AddHours(-30)),
        new SensorValue(40, now.AddMinutes(-10)),
        new SensorValue(41, now.AddMinutes(-9)),
        new SensorValue(99, now.AddHours(2))
      };
      settings.SetValue(key, EncodeVersion1(stored));

      Sensor sensor = new Sensor("Core", 3, SensorType.Load, hardware, settings);
      List<SensorValue> restored = sensor.Values.ToList();

      Assert.Equal(3, restored.Count);
      Assert.Equal(40f, restored[0].Value);
      Assert.Equal(41f, restored[1].Value);
      Assert.True(float.IsNaN(restored[2].Value));
      Assert.True(restored[2].Time >= now);
      // Kept, so saving the settings before the next write does not lose it.
      Assert.True(settings.Contains(key));
    }

    [Fact]
    public void CopyValuesGrowsTheBuffer() {
      MemorySettings settings = new MemorySettings();
      Sensor sensor = new Sensor("Fan", 0, SensorType.Fan, new TestHardware(settings), settings);
      for (int i = 0; i < 400; i++)
        sensor.Value = 1000 + i;

      SensorValue[] buffer = new SensorValue[2];
      int count = sensor.CopyValues(ref buffer);
      Assert.Equal(100, count);
      Assert.True(buffer.Length >= 100);
      Assert.Equal(1001.5f, buffer[0].Value);
    }
  }
}
