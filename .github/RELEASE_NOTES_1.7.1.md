# DMC5 DualSense 1.7.1

This maintenance release makes installation safe and straightforward when
another mod has already placed `dinput8.dll` in the DMC5 directory.

- Existing recognized REFramework installations are preserved, keeping their
  plugins intact.
- An unknown `dinput8.dll` now triggers a simple `Y/N` choice in the normal
  installer. `Y` creates an exact backup and replaces it; `N` cancels without
  changing the existing DLL.
- Uninstall restores a replaced loader exactly and will not overwrite a file
  that another tool changed after DMC5DualSense was installed.
- Installer and uninstaller messages are now consistently in English.
- The native plugin supports the stable REFramework Plugin API 1.10 prefix,
  improving compatibility with existing official REFramework builds.
- Added isolated install, coexistence, replacement, rollback, and plugin API
  verification tests.

Download the **Native C++** archive first. Use the **Managed C# fallback** only
if the recommended build does not start correctly on your PC. Installation is
identical for both archives and requires no Steam Launch Options.
