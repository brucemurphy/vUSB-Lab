using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace vUSB_LAB;

internal sealed record ToggleSettings(string Vm = "", string Vhdx = "", string Letter = "Z");

internal static class VhdxService
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VhdxToggle", "settings.json");

    public static ToggleSettings Load()
    {
        if (!File.Exists(SettingsFile)) return new();
        var settings = JsonSerializer.Deserialize<ToggleSettings>(File.ReadAllText(SettingsFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The saved settings are empty.");
        if (settings.Vm is null || settings.Vhdx is null || settings.Letter is not { Length: 1 } || settings.Letter[0] < 'A' || settings.Letter[0] > 'Z')
            throw new InvalidDataException("The saved settings must contain a VM, disk path and drive letter between A and Z.");
        return settings;
    }

    public static void Save(ToggleSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        string temporary = SettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, SettingsFile, true);
    }

    public static async Task<JsonElement> RunAsync(string action, ToggleSettings settings)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)));
        start.Environment["VUSB_ACTION"] = action;
        start.Environment["VUSB_VM"] = settings.Vm;
        start.Environment["VUSB_VHDX"] = settings.Vhdx;
        start.Environment["VUSB_LETTER"] = settings.Letter;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PowerShell.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string text = await output;
        string failure = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(failure) ? "The disk operation failed." : failure.Trim());
        using var result = JsonDocument.Parse(text);
        return result.RootElement.Clone();
    }

    // Environment variables keep file paths and VM names out of executable PowerShell text.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $Action = $env:VUSB_ACTION
        $Vm = $env:VUSB_VM
        $Vhdx = $env:VUSB_VHDX
        $Letter = $env:VUSB_LETTER

        function Get-Attachments {
            @(Get-VM | Get-VMHardDiskDrive | Where-Object { $_.Path -and $_.Path -ieq $Vhdx })
        }
        function Get-Machine {
            $machines = @(Get-VM | Where-Object Name -eq $Vm)
            if ($machines.Count -ne 1) { throw 'The selected VM is unavailable or its name is not unique. Refresh the VM list.' }
            return $machines[0]
        }
        function Get-ImageState {
            if (-not $Vm -or -not $Vhdx) { return 'Unknown' }
            if (-not (Test-Path -LiteralPath $Vhdx -PathType Leaf)) { throw "Virtual disk not found: $Vhdx" }
            $null = Get-Machine
            $attachments = @(Get-Attachments)
            if ($attachments | Where-Object VMName -ne $Vm) { throw 'This disk belongs to another VM. Detach it there first.' }
            if ($attachments.Count) { return 'VM' }
            if ((Get-DiskImage -ImagePath $Vhdx).Attached) { return 'Host' }
            return 'None'
        }
        function Assert-LetterFree {
            if ((Get-PSDrive -PSProvider FileSystem | Where-Object Name -eq $Letter) -or
                (Get-Volume | Where-Object DriveLetter -eq $Letter)) {
                throw "Drive ${Letter}: is already in use. Choose a free drive letter in settings."
            }
        }
        function Mount-Host {
            $null = Mount-DiskImage -ImagePath $Vhdx -NoDriveLetter
            $disk = Get-DiskImage -ImagePath $Vhdx | Get-Disk
            if ($disk.IsOffline) { $null = Set-Disk -Number $disk.Number -IsOffline $false }
            if ($disk.IsReadOnly) { $null = Set-Disk -Number $disk.Number -IsReadOnly $false }
            $part = Get-Partition -DiskNumber $disk.Number |
                Where-Object { $_.Type -notin 'Reserved', 'System', 'Recovery' -and -not $_.IsHidden } |
                Sort-Object Size -Descending | Select-Object -First 1
            if (-not $part) { throw 'No usable partition found. Initialize and format the VHDX first.' }
            $null = Set-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber -NewDriveLetter $Letter
        }
        try {
            Import-Module Hyper-V
            if ($Letter -notmatch '^[A-Z]$') { throw 'Choose a drive letter between A and Z.' }
            if ($Action -eq 'List') {
                $result = [pscustomobject]@{ Vms = @(Get-VM | Sort-Object Name | Select-Object -ExpandProperty Name) }
            } else {
                $state = Get-ImageState
                if ($Action -eq 'Toggle') {
                    if ($state -eq 'Unknown') { throw 'Select a virtual machine and a VHDX file in settings first.' }
                    $machine = Get-Machine
                    if ($state -eq 'VM') {
                        Assert-LetterFree
                        $drives = @(Get-Attachments)
                        $null = $drives | Remove-VMHardDiskDrive
                        try { Mount-Host } catch {
                            $failure = $_.Exception.Message
                            try {
                                if ((Get-DiskImage -ImagePath $Vhdx).Attached) { $null = Dismount-DiskImage -ImagePath $Vhdx }
                                foreach ($drive in $drives) {
                                    $null = Add-VMHardDiskDrive -VM $machine -Path $Vhdx -ControllerType $drive.ControllerType -ControllerNumber $drive.ControllerNumber -ControllerLocation $drive.ControllerLocation
                                }
                            } catch { throw "$failure Recovery also failed: $($_.Exception.Message). Check the disk attachment before retrying." }
                            throw "$failure The disk was restored to the VM."
                        }
                    } else {
                        if (-not (Get-VMScsiController -VM $machine)) {
                            if ($machine.State -ne 'Off') { throw "VM '$Vm' has no SCSI controller. Shut it down once so one can be added." }
                            $null = Add-VMScsiController -VM $machine
                        }
                        $wasMounted = $state -eq 'Host'
                        $accessPaths = @()
                        if ($wasMounted) {
                            $disk = Get-DiskImage -ImagePath $Vhdx | Get-Disk
                            $accessPaths = @(Get-Partition -DiskNumber $disk.Number | ForEach-Object {
                                [pscustomobject]@{ Number = $_.PartitionNumber; Paths = @($_.AccessPaths) }
                            })
                            $null = Dismount-DiskImage -ImagePath $Vhdx
                        }
                        try { $null = Add-VMHardDiskDrive -VM $machine -ControllerType SCSI -Path $Vhdx } catch {
                            $failure = $_.Exception.Message
                            if ($wasMounted) {
                                try {
                                    $null = Mount-DiskImage -ImagePath $Vhdx -NoDriveLetter
                                    $disk = Get-DiskImage -ImagePath $Vhdx | Get-Disk
                                    foreach ($part in $accessPaths) {
                                        foreach ($path in $part.Paths) {
                                            if ($path -match '^[A-Za-z]:\\$') {
                                                $null = Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $part.Number -AccessPath $path
                                            }
                                        }
                                    }
                                } catch { throw "$failure Recovery also failed: $($_.Exception.Message). Check the disk attachment before retrying." }
                            }
                            throw $failure
                        }
                    }
                    $state = Get-ImageState
                }
                $result = [pscustomobject]@{ State = $state }
            }
            $result | ConvertTo-Json -Compress -Depth 4
        } catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;
}
