Third-party components included in Qidu previews

The .NET 10 self-contained build includes the .NET / Windows Desktop runtimes below.
The .NET Framework compatibility ZIP uses the system's Framework 4.8 or later
instead. Its installer bundles the unmodified Microsoft Framework 4.8 offline
redistributable, which has its own Microsoft license displayed by its installer:
https://learn.microsoft.com/dotnet/framework/deployment/deployment-guide-for-developers
https://dotnet.microsoft.com/download/dotnet-framework/net48
The project MIT license does not relicense Microsoft's redistributable.

Compatibility builds also include Microsoft System.* and Microsoft.Bcl.* libraries
under the .NET MIT license; see dotnet-runtime-LICENSE.txt and notices.
PolySharp is used at build time to generate compatibility language attributes.
See PolySharp-LICENSE.txt for its MIT license.

.NET Runtime / Windows Desktop Runtime 10.0.13
Copyright (c) .NET Foundation and Contributors
See dotnet-runtime-LICENSE.txt, windowsdesktop-LICENSE.txt,
and dotnet-runtime-NOTICES.txt copied from the official runtime packages.

Microsoft.Data.Sqlite 10.0.13
Copyright (c) Microsoft Corporation. All rights reserved.
MIT License. See Microsoft.Data.Sqlite-LICENSE.txt.

SQLitePCLRaw 3.0.5
Copyright 2014-2025 SourceGear, LLC
Apache-2.0. See Apache-2.0.txt.
License source: https://www.apache.org/licenses/LICENSE-2.0.txt
Package source: https://github.com/ericsink/SQLitePCL.raw

SQLite 3.53.4
Public domain. See sqlite-LICENSE.txt copied from the SQLite NuGet package.

Versions and copyright metadata are recorded in the packages' nuspec files.
This directory describes third-party components, not the license of TXT Reader.

Mac / Avalonia desktop preview additionally includes:
Avalonia 12.1.4 (MIT), MicroCom.Runtime 0.11.6 (MIT),
Tmds.DBus.Protocol 0.94.1 (MIT, transitive desktop dependency),
SkiaSharp 3.119.4 and HarfBuzzSharp 8.3.1.3 (MIT plus native notices).
See Avalonia-LICENSE.txt, MicroCom-LICENSE.txt, Tmds.DBus-LICENSE.txt,
SkiaSharp-LICENSE.txt / SkiaSharp-NOTICES.txt, and
HarfBuzzSharp-LICENSE.txt / HarfBuzzSharp-NOTICES.txt.
The Mac self-contained build includes the .NET runtime but not Windows Desktop.
