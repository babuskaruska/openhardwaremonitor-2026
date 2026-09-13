/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2014 Michael Möller <mmoeller@openhardwaremonitor.org>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using OpenHardwareMonitor.Hardware;

namespace OpenHardwareMonitor {

  /// <summary>
  /// Key/value settings stored as an XML appSettings file.
  ///
  /// Thread-safe: sensors update on a background thread (fan curves and
  /// controls read and write their settings there) while the interface reads
  /// and writes settings on the UI thread.
  /// </summary>
  public class PersistentSettings : ISettings {

    private readonly object sync = new object();

    private readonly IDictionary<string, string> settings =
      new Dictionary<string, string>();

    public void Load(string fileName) {
      XmlDocument doc = new XmlDocument();
      try {
        doc.Load(fileName);
      } catch {
        try {
          File.Delete(fileName);
        } catch { }

        string backupFileName = fileName + ".backup";
        try {
          doc.Load(backupFileName);
        } catch {
          try {
            File.Delete(backupFileName);
          } catch { }

          return;
        }
      }

      XmlNodeList list = doc.GetElementsByTagName("appSettings");
      lock (sync) {
        foreach (XmlNode node in list) {
          XmlNode parent = node.ParentNode;
          if (parent != null && parent.Name == "configuration" &&
            parent.ParentNode is XmlDocument) {
            foreach (XmlNode child in node.ChildNodes) {
              if (child.Name == "add") {
                XmlAttributeCollection attributes = child.Attributes;
                XmlAttribute keyAttribute = attributes["key"];
                XmlAttribute valueAttribute = attributes["value"];
                if (keyAttribute != null && valueAttribute != null &&
                  keyAttribute.Value != null) {
                  settings[keyAttribute.Value] = valueAttribute.Value;
                }
              }
            }
          }
        }
      }
    }

    public void Save(string fileName) {
      // Copy under the lock, write the file outside it, so a slow disk never
      // holds up a reader on another thread.
      List<KeyValuePair<string, string>> snapshot;
      lock (sync)
        snapshot = new List<KeyValuePair<string, string>>(settings);

      XmlDocument doc = new XmlDocument();
      doc.AppendChild(doc.CreateXmlDeclaration("1.0", "utf-8", null));
      XmlElement configuration = doc.CreateElement("configuration");
      doc.AppendChild(configuration);
      XmlElement appSettings = doc.CreateElement("appSettings");
      configuration.AppendChild(appSettings);
      foreach (KeyValuePair<string, string> keyValuePair in snapshot) {
        XmlElement add = doc.CreateElement("add");
        add.SetAttribute("key", keyValuePair.Key);
        add.SetAttribute("value", keyValuePair.Value);
        appSettings.AppendChild(add);
      }

      byte[] file;
      using (var memory = new MemoryStream()) {
        using (var writer = new StreamWriter(memory, Encoding.UTF8)) {
          doc.Save(writer);
        }
        file = memory.ToArray();
      }

      string backupFileName = fileName + ".backup";
      if (File.Exists(fileName)) {
        try {
          File.Delete(backupFileName);
        } catch { }
        try {
          File.Move(fileName, backupFileName);
        } catch { }
      }

      using (var stream = new FileStream(fileName,
        FileMode.Create, FileAccess.Write))
      {
        stream.Write(file, 0, file.Length);
      }

      try {
        File.Delete(backupFileName);
      } catch { }
    }

    public bool Contains(string name) {
      lock (sync)
        return settings.ContainsKey(name);
    }

    public void SetValue(string name, string value) {
      lock (sync)
        settings[name] = value;
    }

    public string GetValue(string name, string value) {
      lock (sync) {
        string result;
        return settings.TryGetValue(name, out result) ? result : value;
      }
    }

    public void Remove(string name) {
      lock (sync)
        settings.Remove(name);
    }

    public void SetValue(string name, int value) {
      SetValue(name, value.ToString(CultureInfo.InvariantCulture));
    }

    public int GetValue(string name, int value) {
      string str;
      lock (sync) {
        if (!settings.TryGetValue(name, out str))
          return value;
      }
      int parsedValue;
      return int.TryParse(str, out parsedValue) ? parsedValue : value;
    }

    public void SetValue(string name, float value) {
      SetValue(name, value.ToString(CultureInfo.InvariantCulture));
    }

    public float GetValue(string name, float value) {
      string str;
      lock (sync) {
        if (!settings.TryGetValue(name, out str))
          return value;
      }
      float parsedValue;
      return float.TryParse(str, NumberStyles.Float,
        CultureInfo.InvariantCulture, out parsedValue) ? parsedValue : value;
    }

    public void SetValue(string name, bool value) {
      SetValue(name, value ? "true" : "false");
    }

    public bool GetValue(string name, bool value) {
      string str;
      lock (sync) {
        if (!settings.TryGetValue(name, out str))
          return value;
      }
      return str == "true";
    }

    public void SetValue(string name, Color color) {
      SetValue(name, color.ToArgb().ToString("X8"));
    }

    public Color GetValue(string name, Color value) {
      string str;
      lock (sync) {
        if (!settings.TryGetValue(name, out str))
          return value;
      }
      int parsedValue;
      return int.TryParse(str, NumberStyles.HexNumber,
        CultureInfo.InvariantCulture, out parsedValue)
        ? Color.FromArgb(parsedValue) : value;
    }
  }
}
