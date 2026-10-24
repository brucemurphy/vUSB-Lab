<div align="center">
  <img src="Hyper-V%20VHDX%20Toggle%20Manager/vhdx-toggle-flash-drive.png" alt="vUSB Lab virtual USB flash drive" width="180">
  <h1>vUSB Lab</h1>
  <p><strong>One disk. Your VM or your PC.</strong></p>
  <p>A tray-only virtual USB companion for Hyper-V, with a floating flash drive and Fluent settings.</p>
  <p><a href="https://github.com/brucemurphy/vUSB-Lab/releases/latest">Download the latest release</a></p>
</div>

## No window. Just your USB.

Click the notification-area icon and the USB glides into view: no title bar, no rectangular background, and no taskbar button. Click the drive to move your VHDX between a Hyper-V VM and the host.

The small gear opens an acrylic Fluent settings flyout:

- Choose a local Hyper-V virtual machine.
- Browse for an existing `.vhdx` or `.vhd`.
- Pick a host drive letter, with **Z:** selected by default.
- Save settings once; they persist between launches.

The drive's LED is blue for VM attachment, green for host attachment, and gold while working. Click elsewhere or press **Esc** to dismiss. Press **S** on the USB to open settings; **Enter** or **Space** toggles the disk. Right-click the tray icon for settings or exit.

## Get started

1. Download the **win-x64-Setup.exe** for Intel/AMD Windows, or **win-arm64-Setup.exe** for Windows on ARM.
2. Run the installer and accept the administrator prompt.
3. Launch **vUSB Lab** from the Start menu.
4. Click its tray icon (it may be under the hidden-icons arrow), open the gear, choose your VM and disk, and save.

The EXE installer includes .NET and Windows App SDK dependencies, adds a Start menu shortcut, and supports uninstall through Windows Settings. There is no startup registration. Releases are currently unsigned, so Windows may display a publisher warning. Optional portable ZIPs are also available; extract the entire archive before running `vUSB LAB.exe`.

### Requirements and disk safety

Use a supported Windows 11 Pro/Enterprise/Education installation with Hyper-V, its PowerShell management tools, and Windows PowerShell 5.1 available. The app controls **local** VMs.

**Administrator access is required** for Hyper-V and storage operations. The application requests elevation at startup. Its PowerShell worker uses process-local `-ExecutionPolicy Bypass`, matching the original script's invocation; it does not change system-wide execution policy or override organization policy.

Use a separate, initialized and formatted **data disk**, not a VM boot disk or a checkpoint/differencing chain. Save work and close files on the host before moving it to the VM. Safely eject/offline it inside the guest before moving it back. Do not attach the same disk elsewhere while an operation is running.

An occupied host drive letter is rejected rather than evicting another drive. A VM needs a SCSI controller for hot attachment; if it has none, shut it down once so a controller can be added. Failed transfers attempt to restore the previous attachment and report recovery failures explicitly.

## Updates

The app checks stable GitHub releases at startup and shows a tray notification when a newer version is available. Click the notice to open settings, then click **Update**. You can also select **Check for updates** in the gear flyout.

The app downloads the matching EXE installer and verifies it against GitHub's SHA256 asset digest. It starts the installer, exits safely, and the installer extracts the updated files into the existing application folder and restarts vUSB. No manual download or extraction is needed. Update is disabled during disk operations; installing an update does not move your disks. If installation fails, the installer reports the problem rather than claiming success.

Settings stay in `%APPDATA%\VhdxToggle\settings.json` and survive upgrades and uninstall; downloads go to `%LOCALAPPDATA%\vUSB Lab\Updates`. Checks require internet access. GitHub rate limits or network errors appear in the settings flyout without blocking disk use.

## Build

Open `vUSB LAB.slnx` in Visual Studio with WinUI desktop development support and the .NET 8 SDK. Select **x64** or **ARM64** and the unpackaged launch profile. Debugging the elevated EXE may require running Visual Studio as administrator.

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then build EXE installers and portable ZIPs with the installed Visual Studio MSBuild:

```powershell
.\scripts\Publish-Release.ps1 -Version 1.0.0
```

Outputs are written to `artifacts`. Build both architectures on a Windows development machine with the corresponding SDK workloads. Do not trim or single-file publish WinUI.

For a new release, update the version properties in `vUSB LAB.csproj`, run the script with the matching version, commit the changes, and publish the EXE installers, ZIPs and `SHA256SUMS.txt` under tag `v<version>`. Keep asset names unchanged: the updater selects `vUSB-Lab-v<version>-win-<architecture>-Setup.exe`. GitHub supplies the SHA256 asset digest used for verification.

## Original PowerShell version

The original working UI remains in `Hyper-V VHDX Toggle Manager\VhdxToggle.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File ".\Hyper-V VHDX Toggle Manager\VhdxToggle.ps1"
```

The desktop app keeps the original VM/host toggle model while adding the transparent tray interface, configurable letters, and release updates.
