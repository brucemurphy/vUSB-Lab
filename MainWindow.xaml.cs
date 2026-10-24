using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics;
using Forms = System.Windows.Forms;

namespace vUSB_LAB;

public sealed partial class MainWindow : Window
{
    private readonly UsbTray _tray;
    private ToggleSettings _settings = new();
    private bool _busy;
    private bool _exiting;
    private bool _pickingFile;
    private string? _loadError;
    private AvailableRelease? _availableRelease;
    private string _diskState = "Unknown";

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "vUSB Lab " + ReleaseUpdater.DisplayVersion;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
        for (char letter = 'A'; letter <= 'Z'; letter++) LetterChoice.Items.Add(letter + ":");
        try { _settings = VhdxService.Load(); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { _loadError = "Could not load settings: " + error.Message; }
        DiskPath.Text = _settings.Vhdx;
        LetterChoice.SelectedItem = _settings.Letter + ":";
        _tray = new UsbTray();
        _tray.ToggleRequested += async (_, _) => await ToggleAsync();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.ExitRequested += (_, _) => Quit();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && !_pickingFile) AppWindow.Hide();
        };
        Closed += (_, args) =>
        {
            if (!_exiting) { args.Handled = true; AppWindow.Hide(); }
        };
        _ = RefreshAsync();
        _ = CheckUpdatesAsync(true);
    }

    private void ShowSettings()
    {
        var anchor = _tray.Anchor;
        var work = Forms.Screen.FromPoint(anchor).WorkingArea;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Move(new PointInt32(work.Right - 440, work.Bottom - 600));
        double scale = GetDpiForWindow(hwnd) / 96d;
        int width = Math.Min((int)(440 * scale), work.Width - 16);
        int height = Math.Min((int)(610 * scale), work.Height - 16);
        int x = Math.Clamp(anchor.X - width / 2, work.Left + 8, Math.Max(work.Left + 8, work.Right - width - 8));
        int y = anchor.Y <= work.Top ? work.Top + 8 : work.Bottom - height - 8;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        AppWindow.Show();
        Activate();
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        SettingsFields.IsEnabled = !value;
        ExitButton.IsEnabled = !value;
        UpdateButton.IsEnabled = !value;
        BusyProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        if (value)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Message = "Working...";
            _tray.UpdateState("Unknown", "Working...", true);
        }
    }

    private void ShowState(string state)
    {
        _diskState = state;
        string message = state switch
        {
            "VM" => $"Attached to {_settings.Vm}. Click the USB to mount on this PC as {_settings.Letter}:.",
            "Host" => $"Mounted on this PC. Click the USB to attach to {_settings.Vm}.",
            "None" => $"Not attached. Click the USB to attach to {_settings.Vm}.",
            _ => "Choose a virtual machine and a disk using the gear."
        };
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Message = message;
        _tray.UpdateState(state, message, false);
    }

    private void ReportError(Exception error)
    {
        _diskState = "Unknown";
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Message = error.Message;
        _tray.UpdateState("Unknown", error.Message, false);
        _tray.Notify(error.Message, true);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            string selected = VmChoice.SelectedItem as string ?? _settings.Vm;
            var result = await VhdxService.RunAsync("List", _settings);
            VmChoice.Items.Clear();
            foreach (var vm in result.GetProperty("Vms").EnumerateArray()) VmChoice.Items.Add(vm.GetString()!);
            if (VmChoice.Items.Contains(selected)) VmChoice.SelectedItem = selected;
            var state = await VhdxService.RunAsync("State", _settings);
            ShowState(state.GetProperty("State").GetString()!);
            if (VmChoice.Items.Count == 0) throw new InvalidOperationException("No Hyper-V virtual machines were found on this PC.");
            if (_loadError is not null)
            {
                string message = _loadError;
                _loadError = null;
                throw new InvalidDataException(message);
            }
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }

    private async Task ToggleAsync()
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_settings.Vm) || !File.Exists(_settings.Vhdx))
        {
            ReportError(new InvalidOperationException("Select a virtual machine and a valid VHDX file, then save settings."));
            ShowSettings();
            return;
        }
        SetBusy(true);
        try
        {
            var result = await VhdxService.RunAsync("Toggle", _settings);
            ShowState(result.GetProperty("State").GetString()!);
            _tray.Notify(StatusBar.Message);
        }
        catch (Exception error) { ReportError(error); ShowSettings(); }
        finally { SetBusy(false); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            if (VmChoice.SelectedItem is not string vm || string.IsNullOrWhiteSpace(vm))
                throw new InvalidOperationException("Select a virtual machine first.");
            if (!File.Exists(DiskPath.Text) || !new[] { ".vhd", ".vhdx" }.Contains(Path.GetExtension(DiskPath.Text), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose an existing VHDX or VHD file.");
            var settings = new ToggleSettings(vm, DiskPath.Text, ((string)LetterChoice.SelectedItem)[..1]);
            var result = await VhdxService.RunAsync("State", settings);
            VhdxService.Save(settings);
            _settings = settings;
            ShowState(result.GetProperty("State").GetString()!);
            AppWindow.Hide();
            _tray.ShowUsb(false);
        }
        catch (Exception error) { ReportError(error); }
        finally { SetBusy(false); }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        _pickingFile = true;
        try
        {
            using var dialog = new Forms.OpenFileDialog
            {
                Filter = "Virtual hard disks (*.vhdx;*.vhd)|*.vhdx;*.vhd",
                CheckFileExists = true,
                FileName = DiskPath.Text,
                Title = "Choose your virtual USB disk"
            };
            if (dialog.ShowDialog(new DialogOwner(WinRT.Interop.WindowNative.GetWindowHandle(this))) == Forms.DialogResult.OK)
                DiskPath.Text = dialog.FileName;
        }
        catch (Exception error) { ReportError(error); }
        finally { _pickingFile = false; Activate(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string previousMessage = StatusBar.Message;
        var previousSeverity = StatusBar.Severity;
        SetBusy(true);
        UpdateStatus.IsOpen = true;
        UpdateStatus.Severity = InfoBarSeverity.Informational;
        try
        {
            if (_availableRelease is null)
            {
                await CheckUpdatesAsync(false);
            }
            else
            {
                UpdateStatus.Message = "Downloading and verifying update...";
                string path = await ReleaseUpdater.DownloadAsync(_availableRelease);
                UpdateStatus.Message = "Installing update and restarting vUSB...";
                ReleaseUpdater.StartInstaller(path);
                SetBusy(false);
                Quit();
            }
        }
        catch (Exception error)
        {
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = "Could not update: " + error.Message;
        }
        finally
        {
            if (!_exiting)
            {
                SetBusy(false);
                StatusBar.Message = previousMessage;
                StatusBar.Severity = previousSeverity;
                _tray.UpdateState(_diskState, previousMessage, false);
            }
        }
    }

    private bool _checkingUpdates;
    private async Task CheckUpdatesAsync(bool notify)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        try
        {
            _availableRelease = await ReleaseUpdater.CheckAsync();
            UpdateStatus.IsOpen = true;
            UpdateStatus.Severity = InfoBarSeverity.Informational;
            UpdateStatus.Message = _availableRelease is null
                ? "You're running the latest release."
                : $"vUSB Lab {_availableRelease.Version.ToString(3)} is ready. Click Update to download, install and restart.";
            UpdateButton.Content = _availableRelease is null ? "Check for updates" : "Update";
            if (notify && _availableRelease is not null) _tray.Notify(UpdateStatus.Message);
        }
        catch (Exception error)
        {
            UpdateStatus.IsOpen = true;
            UpdateStatus.Severity = InfoBarSeverity.Warning;
            UpdateStatus.Message = "Could not check for updates: " + error.Message;
        }
        finally { _checkingUpdates = false; }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();
    private void Exit_Click(object sender, RoutedEventArgs e) => Quit();

    private void Settings_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; AppWindow.Hide(); }
    }

    private void Quit()
    {
        if (_busy) { _tray.Notify("Wait for the current operation to finish before exiting."); return; }
        _exiting = true;
        _tray.Dispose();
        Close();
        Application.Current.Exit();
    }

    private sealed record DialogOwner(IntPtr Handle) : Forms.IWin32Window;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
