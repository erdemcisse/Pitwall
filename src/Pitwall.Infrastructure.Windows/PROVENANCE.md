# Pitwall.Infrastructure.Windows — interface provenance

Every native entry point and WMI class below names its primary Microsoft source, read 2026-09-03
unless dated otherwise. No Linux driver source, FanControl, OpenRGB, vendor-hub decompile or other
prohibited source was used, read, or referenced for any implementation in this
project. WMI shapes were taken from the CIMWin32 provider reference; struct layouts from the
Win32 API reference; semantics implemented are field copies and documented unit conversions only.

## NuGet dependency

- `System.Management` 10.0.11, MIT (`https://licenses.nuget.org/MIT`), Microsoft, repository
  `https://github.com/dotnet/dotnet` commit `e2f47b0110ed922f21a1522da67279133ce28f32`.
  Observed in the published nuspec via `https://api.nuget.org/v3-flatcontainer/system.management/10.0.11/system.management.nuspec`
  on 2026-09-03. Transitive: `System.CodeDom` 10.0.11 (MIT, no further dependencies, same observation).
  No licence question: MIT, same terms family as the existing tree.

## WMI classes (`root\cimv2` unless noted)

- `Win32_OperatingSystem` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-operatingsystem`
- `Win32_Processor` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-processor`
- `Win32_PhysicalMemory` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-physicalmemory`
- `Win32_ComputerSystem` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystem`
- `Win32_ComputerSystemProduct` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-computersystemproduct`
- `Win32_BaseBoard` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-baseboard`
- `Win32_BIOS` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-bios`
- `Win32_SystemEnclosure` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-systemenclosure`
- `Win32_VideoController` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-videocontroller`
- `Win32_DiskDrive` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-diskdrive`
- `Win32_Battery` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-battery`
- `Win32_TemperatureProbe` — `https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-temperatureprobe`
  (documented; ordinarily exposes no instances — absence is the expected case, reported `Unavailable`).
- `Win32_DeviceGuard`, namespace `root\Microsoft\Windows\DeviceGuard` — class and namespace confirmed
  by `https://learn.microsoft.com/en-us/windows/security/identity-protection/credential-guard/configure`
  (`SecurityServicesRunning` CIM query) and the Device Guard deployment reference. Only raw field
  values are preserved (`Version`, `VirtualizationBasedSecurityStatus`, `SecurityServicesConfigured`,
  `SecurityServicesRunning`); no bit semantics are decoded here.
- `MSAcpi_ThermalZoneTemperature`, namespace `root\WMI` — **no primary Microsoft reference was found**
  on 2026-09-03 (third-party corroboration only: tenths-of-Kelvin `CurrentTemperature`). Queried as an
  opportunistic read-only signal; absence is normal and reported `Unavailable`. Do not treat this row
  as primary-documented.

Queries are static `SELECT` reads with an explicit per-call timeout. `Win32_Product` is never queried.

## Native APIs (P/Invoke, `user32.dll` / `kernel32.dll`)

- `GetPhysicallyInstalledSystemMemory` — `https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getphysicallyinstalledsystemmemory`
- `GlobalMemoryStatusEx` — `https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex`
- `GetSystemPowerStatus` — `https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus`
- `GetLogicalDrives` — `https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getlogicaldrives`
- `GetVolumeInformationW` — `https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationw`
- `GetDiskFreeSpaceExW` — `https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw`
- `CreateFileW` / `DeviceIoControl` — `https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew`,
  `https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-deviceiocontrol`,
  with `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` (`0x00560000`). Volume handles are opened with zero
  desired access and shared read/write; ordinary users can query extents without elevation.
- `GetDisplayConfigBufferSizes` / `QueryDisplayConfig` / `DisplayConfigGetDeviceInfo` —
  `https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig` and
  siblings. `DISPLAYCONFIG_DEVICE_INFO_TYPE` values verified 2026-09-03 against
  `https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ne-wingdi-displayconfig_device_info_type`
  (page updated 2025-05-21): `GET_TARGET_NAME = 2`, `GET_ADVANCED_COLOR_INFO = 9`. Only the raw
  advanced-color `value` is preserved; its bit decode is intentionally not implemented here.
- CCD struct strides verified 2026-09-03 against the wingdi structure references:
  `DISPLAYCONFIG_MODE_INFO`
  (`https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_mode_info`;
  16-byte header plus 48-byte mode union — target mode is the 48-byte video-signal info —
  totalling 64, active size at union offset 24),
  `DISPLAYCONFIG_VIDEO_SIGNAL_INFO`
  (`https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_video_signal_info`;
  8+8+8+8+8+4+4 totalling 48),
  `DISPLAYCONFIG_TARGET_DEVICE_NAME`
  (`https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name`;
  header 20, flags struct 4, outputTechnology 4, edid ids 2+2, connectorInstance 4,
  friendly name at 36, device path at 164, totalling 420),
  `DISPLAYCONFIG_PATH_INFO` / `DISPLAYCONFIG_PATH_TARGET_INFO`
  (`https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_path_info`,
  `https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_path_target_info`;
  source 20, target 48 including the trailing `statusFlags`, path totalling 72),
  and `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO` (header 20, value union 4, colorEncoding 4,
  bitsPerColorChannel 4, totalling 32; corroborated by Microsoft's windows-rs binding).
  The first live run failed contained (`probe.unhandled` — in hindsight the same overrun
  manifesting mildly); the corrections-2 stride pass enlarged the mode elements past the true
  size (safe direction, wrong value: 80 vs 64) but missed the path target's trailing
  `statusFlags` (44 vs 48, path 68 vs 72), and the next live run died with heap corruption —
  a native write past the short path array.
  Corrections 3 sets every stride to the documented values above, sizes all `QueryDisplayConfig`
  buffers from `GetDisplayConfigBufferSizes` counts, validates returned counts before indexing,
  and reports stage-specific codes (`ccd.buffer_sizes_failed`, `ccd.query_failed`,
  `ccd.inconsistent_counts`, `ccd.access_denied`). `CcdLayoutTests` pins the derived sizes.
  No decompiled, GPL, or vendor-hub source was used for any correction.
- Elevation check uses inbox `System.Security.Principal.Windows`
  (`https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.windows`) — read-only role test.

## Deliberately not implemented (no source consulted)

DXGI adapter enumeration, SDR-white-level/HDR state decodes, VRR capability, P/E-core topology,
SMART/health counters, network, software/startup/services inventory. Listed in `PHASE-0E-A-REPORT.md`.
