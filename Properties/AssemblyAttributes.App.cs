/*

  This Source Code Form is subject to the terms of the Mozilla Public
  License, v. 2.0. If a copy of the MPL was not distributed with this
  file, You can obtain one at http://mozilla.org/MPL/2.0/.

  Copyright (C) 2009-2020 Michael Möller <mmoeller@openhardwaremonitor.org>

*/

using System.Runtime.InteropServices;

// Identity comes from MSBuild properties in Directory.Build.props. Only
// attributes with no MSBuild equivalent belong here.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
[assembly: ComVisible(false)]
