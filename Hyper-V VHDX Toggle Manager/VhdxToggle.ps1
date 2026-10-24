#Requires -Version 5.1
<#
  Hyper-V VHDX Toggle Manager
  Click the USB stick to move a VHDX between a Hyper-V VM (as a SCSI hard drive)
  and the host (mounted as Z:\).
#>

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -STA -File `"$PSCommandPath`""
    exit
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms
Import-Module Hyper-V

$HostLetter   = 'Z'
$SettingsDir  = Join-Path $env:APPDATA 'VhdxToggle'
$SettingsFile = Join-Path $SettingsDir 'settings.json'

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="VHDX Toggle" Width="420" Height="560" ResizeMode="CanMinimize"
        WindowStartupLocation="CenterScreen" Background="#1E1E1E">
  <Grid Margin="16">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <TextBlock Grid.Row="0" Text="Virtual machine" Foreground="#CCC" Margin="0,0,0,4"/>
    <DockPanel Grid.Row="1" Margin="0,0,0,12">
      <Button x:Name="BtnRefresh" DockPanel.Dock="Right" Content="&#x21BB;" Width="32" Margin="6,0,0,0"/>
      <ComboBox x:Name="CmbVm" Height="26"/>
    </DockPanel>

    <TextBlock Grid.Row="2" Text="VHDX file" Foreground="#CCC" Margin="0,0,0,4"/>
    <DockPanel Grid.Row="3" Margin="0,0,0,12">
      <Button x:Name="BtnBrowse" DockPanel.Dock="Right" Content="Browse..." Width="70" Margin="6,0,0,0"/>
      <TextBox x:Name="TxtVhdx" Height="26" IsReadOnly="True" VerticalContentAlignment="Center"/>
    </DockPanel>

    <!-- The flash drive -->
    <Viewbox Grid.Row="4" Margin="20">
      <Canvas x:Name="Usb" Width="140" Height="300" Cursor="Hand" Background="Transparent"
              ToolTip="Click to toggle between VM and host">
        <!-- metal connector -->
        <Rectangle Canvas.Left="38" Canvas.Top="0" Width="64" Height="70" RadiusX="3" RadiusY="3"
                   Stroke="#888" StrokeThickness="2">
          <Rectangle.Fill>
            <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
              <GradientStop Color="#9A9A9A" Offset="0"/>
              <GradientStop Color="#F2F2F2" Offset="0.5"/>
              <GradientStop Color="#9A9A9A" Offset="1"/>
            </LinearGradientBrush>
          </Rectangle.Fill>
        </Rectangle>
        <Rectangle Canvas.Left="52" Canvas.Top="16" Width="12" Height="12" Fill="#333"/>
        <Rectangle Canvas.Left="76" Canvas.Top="16" Width="12" Height="12" Fill="#333"/>
        <!-- body -->
        <Rectangle x:Name="UsbBody" Canvas.Left="10" Canvas.Top="66" Width="120" Height="230"
                   RadiusX="18" RadiusY="18" Fill="#555" Stroke="#222" StrokeThickness="3"/>
        <Rectangle Canvas.Left="22" Canvas.Top="80" Width="20" Height="200" RadiusX="8" RadiusY="8"
                   Fill="White" Opacity="0.15"/>
        <!-- LED -->
        <Ellipse x:Name="UsbLed" Canvas.Left="62" Canvas.Top="84" Width="16" Height="16" Fill="#333"
                 Stroke="#111" StrokeThickness="1"/>
        <!-- lanyard hole -->
        <Ellipse Canvas.Left="58" Canvas.Top="262" Width="24" Height="24" Fill="#1E1E1E"/>
        <!-- label -->
        <TextBlock x:Name="UsbLabel" Canvas.Left="10" Canvas.Top="160" Width="120" TextAlignment="Center"
                   FontSize="20" FontWeight="Bold" Foreground="White" Text="?"/>
      </Canvas>
    </Viewbox>

    <TextBlock Grid.Row="5" x:Name="TxtStatus" Foreground="#DDD" TextWrapping="Wrap"
               TextAlignment="Center" FontSize="13" MinHeight="40"/>
  </Grid>
</Window>
'@

$window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in 'CmbVm','BtnRefresh','TxtVhdx','BtnBrowse','Usb','UsbBody','UsbLed','UsbLabel','TxtStatus') {
    $ui[$n] = $window.FindName($n)
}

function Brush($hex) { (New-Object Windows.Media.BrushConverter).ConvertFromString($hex) }

function Save-Settings {
    if (-not (Test-Path $SettingsDir)) { New-Item -ItemType Directory -Path $SettingsDir | Out-Null }
    @{ Vm = [string]$ui.CmbVm.SelectedItem; Vhdx = $ui.TxtVhdx.Text } | ConvertTo-Json | Set-Content $SettingsFile
}

function Load-Settings {
    if (Test-Path $SettingsFile) { try { return Get-Content $SettingsFile -Raw | ConvertFrom-Json } catch {} }
    return $null
}

function Refresh-Vms {
    $current = $ui.CmbVm.SelectedItem
    $ui.CmbVm.Items.Clear()
    Get-VM | Sort-Object Name | ForEach-Object { [void]$ui.CmbVm.Items.Add($_.Name) }
    if ($current -and $ui.CmbVm.Items.Contains($current)) { $ui.CmbVm.SelectedItem = $current }
}

function Get-VmDrive($vmName, $path) {
    Get-VMHardDiskDrive -VMName $vmName | Where-Object { $_.Path -and ($_.Path -ieq $path) }
}

# Path of the VHD/VHDX currently mounted as the host letter, or $null
function Get-HostLetterImage {
    $part = Get-Partition -DriveLetter $HostLetter -ErrorAction SilentlyContinue
    if (-not $part) { return $null }
    $disk = $part | Get-Disk
    if ($disk.BusType -eq 'File Backed Virtual' -and $disk.Location -match '\.vhdx?$') { return $disk.Location }
    return $null
}

# Returns 'VM', 'Host', 'None', or 'Unknown'
function Get-State {
    $vm = $ui.CmbVm.SelectedItem; $path = $ui.TxtVhdx.Text
    if (-not $vm -or -not $path -or -not (Test-Path $path)) { return 'Unknown' }
    if (Get-VmDrive $vm $path) { return 'VM' }
    if ((Get-HostLetterImage) -ieq $path) { return 'Host' }
    $img = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
    if ($img -and $img.Attached) { return 'Host' }
    return 'None'
}

function Update-View {
    $state = try { Get-State } catch { 'Unknown' }
    $vm = $ui.CmbVm.SelectedItem
    switch ($state) {
        'VM'      { $ui.UsbBody.Fill = Brush '#1565C0'; $ui.UsbLed.Fill = Brush '#40C4FF'; $ui.UsbLabel.Text = 'VM'
                    $ui.TxtStatus.Text = "Attached to '$vm' as a hard drive.`nClick to unplug and mount on this PC as ${HostLetter}:\" }
        'Host'    { $ui.UsbBody.Fill = Brush '#2E7D32'; $ui.UsbLed.Fill = Brush '#69F0AE'; $ui.UsbLabel.Text = "${HostLetter}:\"
                    $ui.TxtStatus.Text = "Mounted on this PC.`nClick to plug it back into '$vm'." }
        'None'    { $ui.UsbBody.Fill = Brush '#555555'; $ui.UsbLed.Fill = Brush '#333333'; $ui.UsbLabel.Text = 'OFF'
                    $ui.TxtStatus.Text = "Not attached anywhere.`nClick to plug into '$vm'." }
        default   { $ui.UsbBody.Fill = Brush '#555555'; $ui.UsbLed.Fill = Brush '#333333'; $ui.UsbLabel.Text = '?'
                    $ui.TxtStatus.Text = 'Select a VM and a VHDX file.' }
    }
    return $state
}

function Attach-ToVm($vm, $path) {
    $img = Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue
    if ($img -and $img.Attached) { Dismount-DiskImage -ImagePath $path | Out-Null }

    $vmObj = Get-VM -Name $vm
    if (-not (Get-VMScsiController -VMName $vm)) {
        if ($vmObj.State -ne 'Off') { throw "VM '$vm' has no SCSI controller. Shut it down once so one can be added." }
        Add-VMScsiController -VMName $vm
    }
    Add-VMHardDiskDrive -VMName $vm -ControllerType SCSI -Path $path
}

function Mount-OnHost($vm, $path) {
    $used = Get-PSDrive -PSProvider FileSystem | Where-Object Name -eq $HostLetter
    if ($used) {
        $other = Get-HostLetterImage
        if (-not $other) { throw "Drive ${HostLetter}: is already in use on this PC (not by a VHDX). Free it up and try again." }
        $answer = [Windows.MessageBox]::Show($window,
            "${HostLetter}:\ is currently used by:`n$other`n`nEject it so this VHDX can take ${HostLetter}:\ ?",
            'VHDX Toggle', 'YesNo', 'Question')
        if ($answer -ne 'Yes') { throw 'Cancelled.' }
        Dismount-DiskImage -ImagePath $other | Out-Null
    }

    Get-VmDrive $vm $path | Remove-VMHardDiskDrive

    Mount-DiskImage -ImagePath $path -NoDriveLetter | Out-Null
    $disk = Get-DiskImage -ImagePath $path | Get-Disk
    if ($disk.IsOffline)  { Set-Disk -Number $disk.Number -IsOffline $false }
    if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }

    $part = Get-Partition -DiskNumber $disk.Number |
        Where-Object { $_.Type -notin 'Reserved','System','Recovery' -and -not $_.IsHidden } |
        Sort-Object Size -Descending | Select-Object -First 1
    if (-not $part) { throw 'No usable partition found on the VHDX (is it initialized/formatted?).' }
    Set-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber -NewDriveLetter $HostLetter
}

function Invoke-Toggle {
    $vm = $ui.CmbVm.SelectedItem; $path = $ui.TxtVhdx.Text
    $state = Get-State
    if ($state -eq 'Unknown') { $ui.TxtStatus.Text = 'Select a VM and a valid VHDX file first.'; return }

    $window.Cursor = [Windows.Input.Cursors]::Wait
    $ui.TxtStatus.Text = 'Working...'
    $window.Dispatcher.Invoke([action]{}, [Windows.Threading.DispatcherPriority]::Background)
    try {
        switch ($state) {
            'VM'   { Mount-OnHost $vm $path }
            default { Attach-ToVm $vm $path }   # 'Host' or 'None'
        }
        Update-View | Out-Null
    } catch {
        Update-View | Out-Null
        [Windows.MessageBox]::Show($window, $_.Exception.Message, 'VHDX Toggle', 'OK', 'Error') | Out-Null
    } finally {
        $window.Cursor = $null
    }
}

# --- Events ---
$ui.BtnRefresh.Add_Click({ Refresh-Vms; Update-View | Out-Null })
$ui.CmbVm.Add_SelectionChanged({ Save-Settings; Update-View | Out-Null })
$ui.BtnBrowse.Add_Click({
    $dlg = New-Object Microsoft.Win32.OpenFileDialog
    $dlg.Filter = 'Virtual hard disks (*.vhdx;*.vhd)|*.vhdx;*.vhd'
    if ($ui.TxtVhdx.Text) { $dlg.InitialDirectory = Split-Path $ui.TxtVhdx.Text -Parent }
    if ($dlg.ShowDialog($window)) { $ui.TxtVhdx.Text = $dlg.FileName; Save-Settings; Update-View | Out-Null }
})
$ui.Usb.Add_MouseLeftButtonUp({ Invoke-Toggle })

# --- Startup ---
try { Refresh-Vms } catch { $ui.TxtStatus.Text = "Could not list VMs: $($_.Exception.Message)" }
$s = Load-Settings
if ($s) {
    if ($s.Vhdx) { $ui.TxtVhdx.Text = $s.Vhdx }
    if ($s.Vm -and $ui.CmbVm.Items.Contains($s.Vm)) { $ui.CmbVm.SelectedItem = $s.Vm }
}
# If a VHDX is already mounted as Z:, pick it up automatically
$onZ = try { Get-HostLetterImage } catch { $null }
if ($onZ -and -not $ui.TxtVhdx.Text) { $ui.TxtVhdx.Text = $onZ }
if (-not $ui.CmbVm.SelectedItem -and $ui.CmbVm.Items.Count) { $ui.CmbVm.SelectedIndex = 0 }
Update-View | Out-Null

[void]$window.ShowDialog()
