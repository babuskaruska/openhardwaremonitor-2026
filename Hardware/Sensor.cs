/*
 
  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.
 
  Copyright (C) 2009-2012 Michael Möller <mmoeller@openhardwaremonitor.org>
	
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using OpenHardwareMonitor.Collections;

namespace OpenHardwareMonitor.Hardware {

  internal class Sensor : ISensor {

    private readonly string defaultName;
    private string name;
    private readonly int index;
    private readonly bool defaultHidden;
    private readonly SensorType sensorType;
    private readonly Hardware hardware;
    private readonly ReadOnlyArray<IParameter> parameters;
    private float? currentValue;
    private float? minValue;
    private float? maxValue;
    private readonly RingCollection<SensorValue> 
      values = new RingCollection<SensorValue>();
    private readonly ISettings settings;
    private IControl control;
    
    private float sum;
    private int count;
   
    public Sensor(string name, int index, SensorType sensorType,
      Hardware hardware, ISettings settings) : 
      this(name, index, sensorType, hardware, null, settings) { }

    public Sensor(string name, int index, SensorType sensorType,
      Hardware hardware, ParameterDescription[] parameterDescriptions, 
      ISettings settings) :
      this(name, index, false, sensorType, hardware,
        parameterDescriptions, settings) { }

    public Sensor(string name, int index, bool defaultHidden, 
      SensorType sensorType, Hardware hardware, 
      ParameterDescription[] parameterDescriptions, ISettings settings) 
    {           
      this.index = index;
      this.defaultHidden = defaultHidden;
      this.sensorType = sensorType;
      this.hardware = hardware;
      Parameter[] parameters = new Parameter[parameterDescriptions == null ?
        0 : parameterDescriptions.Length];
      for (int i = 0; i < parameters.Length; i++ ) 
        parameters[i] = new Parameter(parameterDescriptions[i], this, settings);
      this.parameters = parameters;

      this.settings = settings;
      this.defaultName = name; 
      this.name = settings.GetValue(
        new Identifier(Identifier, "name").ToString(), name);

      GetSensorValuesFromSettings();      

      hardware.Closing += delegate(IHardware h) {
        SensorValue[] copy = new SensorValue[values.Count];
        values.CopyTo(copy, 0);
        StoreHistory(SensorHistory.Encode(copy, copy.Length, sensorType));
        closed = true;
      };
    }

    private bool closed;

    /// <summary>
    /// True once the hardware has closed and written the final history.
    /// Read under the hardware lock.
    /// </summary>
    internal bool IsClosed {
      get { return closed; }
    }

    /// <summary>
    /// Copies the samples into <paramref name="buffer"/>, growing it when
    /// needed, and returns how many there are. Call under the hardware lock.
    /// </summary>
    internal int CopyValues(ref SensorValue[] buffer) {
      if (buffer == null || buffer.Length < values.Count)
        buffer = new SensorValue[values.Count + values.Count / 8 + 16];
      values.CopyTo(buffer, 0);
      return values.Count;
    }

    /// <summary>Stores a string from <see cref="SensorHistory.Encode"/>.</summary>
    internal void StoreHistory(string encoded) {
      string name = new Identifier(Identifier, "values").ToString();
      if (string.IsNullOrEmpty(encoded))
        settings.Remove(name);
      else
        settings.SetValue(name, encoded);
    }

    private void GetSensorValuesFromSettings() {
      string name = new Identifier(Identifier, "values").ToString();
      DateTime now = DateTime.UtcNow;
      DateTime oldest = now - SensorHistory.Retention;
      foreach (SensorValue value in SensorHistory.Decode(settings.GetValue(name, null))) {
        if (value.Time > now)
          break;
        if (value.Time >= oldest)
          AppendValue(value.Value, value.Time);
      }
      if (values.Count > 0)
        AppendValue(float.NaN, now);

      // The stored string stays in the settings. It used to be removed to save
      // memory, but the settings are now also saved while running, and a save
      // before this sensor next writes its history would have dropped it.
    }

    private void AppendValue(float value, DateTime time) {
      if (values.Count >= 2 && values.Last.Value == value && 
        values[values.Count - 2].Value == value) {
        values.Last = new SensorValue(value, time);
        return;
      } 

      values.Append(new SensorValue(value, time));
    }

    public IHardware Hardware {
      get { return hardware; }
    }

    public SensorType SensorType {
      get { return sensorType; }
    }

    public Identifier Identifier {
      get {
        return new Identifier(hardware.Identifier,
          sensorType.ToString().ToLowerInvariant(),
          index.ToString(CultureInfo.InvariantCulture));
      }
    }

    public string Name {
      get { 
        return name; 
      }
      set {
        if (!string.IsNullOrEmpty(value)) 
          name = value;          
        else 
          name = defaultName;
        settings.SetValue(new Identifier(Identifier, "name").ToString(), name);
      }
    }

    public int Index {
      get { return index; }
    }

    public bool IsDefaultHidden {
      get { return defaultHidden; }
    }

    public IReadOnlyArray<IParameter> Parameters {
      get { return parameters; }
    }

    public float? Value {
      get { 
        return currentValue; 
      }
      set {
        DateTime now = DateTime.UtcNow;
        while (values.Count > 0 && now - values.First.Time > SensorHistory.Retention)
          values.Remove();

        if (value.HasValue) {
          sum += value.Value;
          count++;
          if (count == SensorHistory.ReadingsPerSample) {
            AppendValue(sum / count, now);
            sum = 0;
            count = 0;
          }
        }

        this.currentValue = value;
        if (minValue > value || !minValue.HasValue)
          minValue = value;
        if (maxValue < value || !maxValue.HasValue)
          maxValue = value;
      }
    }

    public float? Min { get { return minValue; } }
    public float? Max { get { return maxValue; } }

    public void ResetMin() {
      minValue = null;
    }

    public void ResetMax() {
      maxValue = null;
    }

    public IEnumerable<SensorValue> Values {
      get { return values; }
    }    

    public void Accept(IVisitor visitor) {
      if (visitor == null)
        throw new ArgumentNullException("visitor");
      visitor.VisitSensor(this);
    }

    public void Traverse(IVisitor visitor) {
      foreach (IParameter parameter in parameters)
        parameter.Accept(visitor);
    }

    public IControl Control {
      get {
        return control;
      }
      internal set {
        this.control = value;
      }
    }
  }
}
