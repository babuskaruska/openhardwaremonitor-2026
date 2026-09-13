/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>

*/

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Assembly identity (title, version, product, copyright) is generated from
// MSBuild properties in Directory.Build.props. Only attributes that cannot be
// expressed as MSBuild properties belong here.

// This library P/Invokes a large number of system DLLs. Constraining the probe
// path to System32 prevents DLL planting via the application or current
// directory, and is a real security control rather than a leftover.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly: CLSCompliant(true)]
[assembly: ComVisible(false)]

// Lets the unit tests exercise parsing code (SMBIOS, RAM naming) directly.
[assembly: InternalsVisibleTo("OpenHardwareMonitor.Tests")]
