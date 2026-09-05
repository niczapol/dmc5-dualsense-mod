# Portability and failure-path audit tests

These are diagnostic regression tests added during the 2026-09-05 audit of
release 1.7.3. Some assertions intentionally FAIL on that release. A passing
normal-build test suite does not supersede these findings.

The 1.7.4-rc1 candidate passed the installer and runtime regressions for both packages. See
`docs/CANDIDATE_1.7.4.md` for the tested commit, CI run and hardware limits.

Run on Windows x64 with PowerShell 7. No game or real controller is required.
The installer test also invokes the OS-bundled Windows PowerShell 5.1, which is
what the distributed CMD entrypoint actually uses. Close DMC5 and its Bridge
first: the existing uninstaller stops Bridge processes by name.

All fixtures go into uniquely named directories below the system temporary
directory (or `-WorkingRoot`). Results and fixture files are retained for
inspection. No real game files, Steam settings, drivers or account settings are
changed. Run only with trusted release packages: testing an installer executes it.

From the repository root, replacing sample paths with real download locations:

```powershell
pwsh -File Tests/Portability/Test-InstallerAdversarial.ps1 -PackageZip C:/Downloads/DMC5DualSense-Native-1.7.3-win-x64.zip
pwsh -File Tests/Portability/Test-InstallerAdversarial.ps1 -PackageZip C:/Downloads/DMC5DualSense-Managed-1.7.3-win-x64.zip
pwsh -File Tests/Portability/Test-RuntimeIsolation.ps1 -NativePackageDirectory C:/Downloads/native-extracted -ManagedPackageDirectory C:/Downloads/managed-extracted
```

The extracted package directory means the directory containing `Install.ps1`
and `DMC5DualSense.Bridge.exe`, not its parent directory.

## What is exercised

- Installer: missing executable during update, corrupt executable with an
  unchanged release manifest, conflict with an already-invalidated PAK entry,
  retry after removing that conflict, edited configuration preservation,
  repair after Steam verification, and a spaces/brackets/Unicode game path.
- Released Bridge executables: absent Steam API, singleton false-success,
  termination with the parent process, ready-file cleanup, and malformed
  configuration. Audio is disabled and there is no real Steam API DLL in the
  fixtures. Native malformed-config execution is skipped only for historical
  1.7.3 packages, whose fallback defaults would enable audio; the candidate
  rejects invalid configuration and is tested on this path too.

`test_framework_dependency.cpp` is a separate optional, historical 1.7.3
hostfxr probe. The 1.7.4 candidate no longer ships this C++/CLI plugin. Compile
with the SDK's `hostfxr.h` include directory and `-municode`; pass three args:
the real hostfxr DLL path, the packaged `REFramework.NET.runtimeconfig.json`,
and the .NET root to resolve frameworks from. Comparing an installed .NET 10
root against the extracted mod directory demonstrates that the C++/CLI plugin
requires a shared runtime not provided by the standalone Bridge bundle.
This probe resolves configuration only; it does not load DMC5 or initialize
REFramework. It is not a substitute for a clean Windows VM test.

## Limits

Fixtures establish code behavior, not controller firmware behavior or whether
Steam restores the game's input after a USB disconnect. These tests do not
measure physical resistance, verify Bluetooth/Edge, render the game's UI, or
cover every third-party loader. See `docs/AUDIT_2026-09-05.md` for findings and
the remaining hardware acceptance matrix. macOS can inspect/edit these files;
the executable tests need a Windows x64 host/CI runner.
