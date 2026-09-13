/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2012 Prince Samuel <prince.samuel@gmail.com>
  Copyright (C) 2026 Open Hardware Monitor contributors

*/

using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using OpenHardwareMonitor.Utilities;

namespace OpenHardwareMonitor.GUI {
  public partial class PortForm : Form {
    private readonly MainForm parent;
    private readonly string localIP;

    public PortForm(MainForm m) {
      InitializeComponent();
      parent = m;

      localIP = GetLocalIP();

      label2.Text = "Port number for the web server:";
      label5.Text = "Start the server with Options > Web Server > Run.";

      // Let the note wrap onto a second line rather than being clipped.
      label1.AutoSize = true;
      label1.MaximumSize = new Size(ClientSize.Width - 2 * label1.Left, 0);
    }

    private void portTextBox_TextChanged(object sender, EventArgs e) {

    }

    private static string GetLocalIP() {
      try {
        foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList) {
          if (ip.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(ip))
            return ip.ToString();
        }
      } catch (SocketException) {
      }
      return "localhost";
    }

    private void portNumericUpDn_ValueChanged(object sender, EventArgs e) {
      HttpServer server = parent.Server;
      int port = (int)portNumericUpDn.Value;

      string url;
      if (server.AllowRemoteConnections) {
        url = server.GetUrl(localIP, port, true);
        label1.Text = "Anyone on your network who has this link can read your " +
          "sensors. The port must also be allowed through the firewall.";
      } else {
        url = server.GetUrl("localhost", port, false);
        label1.Text = "Only this computer can connect. Turn on Allow Remote " +
          "Connections in the Web Server menu to share it on your network.";
      }

      webServerLinkLabel.Text = url;
      webServerLinkLabel.Links.Clear();
      webServerLinkLabel.Links.Add(0, url.Length, url);
    }

    private void portOKButton_Click(object sender, EventArgs e) {
      parent.Server.ListenerPort = (int)portNumericUpDn.Value;
      this.Close();
    }

    private void portCancelButton_Click(object sender, EventArgs e) {
      this.Close();
    }

    private void PortForm_Load(object sender, EventArgs e) {
      portNumericUpDn.Value = parent.Server.ListenerPort;
      portNumericUpDn_ValueChanged(null, null);
    }

    private void webServerLinkLabel_LinkClicked(object sender,
      LinkLabelLinkClickedEventArgs e) {
      // .NET defaults UseShellExecute to false, under which a URL is not a
      // runnable file; without this the link silently did nothing.
      try {
        Process.Start(new ProcessStartInfo(e.Link.LinkData.ToString()) {
          UseShellExecute = true
        });
      } catch (Exception) {
      }
    }

  }
}
