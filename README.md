# Pitwall

The foundation of a native Windows diagnostics app. It reads a PC's hardware and system state, stores versioned snapshots locally and flags data it could not read reliably. The current version contains the data and scanning engine and an empty WinUI 3 shell. The user interface is not built yet.

Built with C# on .NET 10 and WinUI 3.

## What is in it

- **Hardware inventory probes** (`src/Pitwall.Infrastructure.Windows`). These read the operating system, CPU, memory, GPU, motherboard, BIOS, disks, volumes and displays through WMI and Win32 APIs. Every probe lists the Microsoft documentation it is based on in `PROVENANCE.md`.
- **Quick scan engine** (`src/Pitwall.Scanning`). Runs the probes in parallel with a per-probe timeout and combines the results into one snapshot.
- **Honest data model** (`src/Pitwall.Domain`). Every value carries a quality state: valid, estimated, stale, unavailable, unsupported, permission denied or invalid. Missing data is stored as missing, never as a fake zero. Device matching uses hardware identity and asks for confirmation when the match is uncertain.
- **Portable storage** (`src/Pitwall.Storage`). SQLite with migrations and a backup before each upgrade, plus SHA-256 content-addressed files. All state lives under one folder you choose, with no registry or hidden profile data.
- **German and English** (`src/Pitwall.Resources`). Falls back to English when a translation is missing.

## Quality checks

- `tests/Pitwall.Tests`: unit and integration tests for storage, migrations, portability, device identity, scanning and localisation.
- `tools/Pitwall.ResourceLint`: fails the build if user-facing text is hard-coded instead of coming from the language files.
- `tools/Pitwall.CovenantHarness`: launches the app and checks its idle memory and CPU against fixed budgets.
- GitHub Actions CI restores with locked packages, builds with warnings treated as errors, and runs the tests and both checks.

## Building

Requires Windows x64 and the .NET SDK version pinned in `global.json`.

```powershell
dotnet restore Pitwall.slnx --locked-mode
dotnet build Pitwall.slnx -c Release -warnaserror
dotnet test tests/Pitwall.Tests -c Release
```

`tools/Verify.ps1` (PowerShell 7) runs the full check suite.
