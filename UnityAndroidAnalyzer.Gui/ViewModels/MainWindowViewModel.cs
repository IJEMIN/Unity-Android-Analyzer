using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnityAndroidAnalyzer.Core;

namespace UnityAndroidAnalyzer.Gui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IUnityAnalyzer _analyzer;
    private readonly AdbHelper _adb = new();

    [ObservableProperty]
    private string? apkPath;

    [ObservableProperty]
    private string markdown = "";

    [ObservableProperty]
    private string adbAddress = "127.0.0.1:5555";

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private ObservableCollection<string> devices = new();

    [ObservableProperty]
    private string? selectedDevice;

    [ObservableProperty]
    private string searchText = "";

    [ObservableProperty]
    private ObservableCollection<PackageInfo> packages = new();

    [ObservableProperty]
    private PackageInfo? selectedPackage;

    public MainWindowViewModel(IUnityAnalyzer analyzer)
    {
        _analyzer = analyzer;
        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SearchPackagesCommand = new AsyncRelayCommand(SearchPackagesAsync);
        AnalyzeDeviceCommand = new AsyncRelayCommand(AnalyzeDeviceAsync);
        RunLogcatCommand = new AsyncRelayCommand(RunLogcatAsync);
    }

    public IAsyncRelayCommand RefreshDevicesCommand { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SearchPackagesCommand { get; }
    public IAsyncRelayCommand AnalyzeDeviceCommand { get; }
    public IAsyncRelayCommand RunLogcatCommand { get; }

    public async Task RefreshDevicesAsync()
    {
        var list = await Task.Run(() => _adb.GetDevices());
        Devices.Clear();
        foreach (var d in list) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();
        StatusMessage = $"Found {Devices.Count} devices.";
    }

    public async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(AdbAddress)) return;
        StatusMessage = $"Connecting to {AdbAddress}...";
        var success = await Task.Run(() => _adb.Connect(AdbAddress));
        if (success)
        {
            StatusMessage = $"Connected to {AdbAddress}";
            await RefreshDevicesAsync();
            SelectedDevice = AdbAddress;
        }
        else
        {
            StatusMessage = $"Failed to connect to {AdbAddress}";
        }
    }

    public async Task SearchPackagesAsync()
    {
        if (string.IsNullOrEmpty(SelectedDevice))
        {
            StatusMessage = "Select a device first.";
            return;
        }

        _adb.SetSerial(SelectedDevice);
        StatusMessage = $"Searching packages for '{SearchText}'...";
        var list = await Task.Run(() => _adb.SearchPackages(SearchText));
        Packages.Clear();
        foreach (var p in list) Packages.Add(p);
        StatusMessage = $"Found {Packages.Count} packages.";
    }

    public async Task AnalyzeLocalAsync()
    {
        if (string.IsNullOrWhiteSpace(ApkPath))
            return;

        StatusMessage = "Analyzing local APK...";
        var result = await _analyzer.AnalyzeLocalAsync(ApkPath, Array.Empty<string>());
        Markdown = result.Markdown;
        StatusMessage = "Analysis complete.";
    }

    public async Task AnalyzeDeviceAsync()
    {
        if (string.IsNullOrEmpty(SelectedDevice) || SelectedPackage == null)
        {
            StatusMessage = "Select device and package first.";
            return;
        }

        StatusMessage = $"Analyzing {SelectedPackage.PackageName} on {SelectedDevice}...";
        var result = await _analyzer.AnalyzeDeviceAsync(SelectedDevice, SelectedPackage.PackageName);
        Markdown = result.Markdown;
        StatusMessage = "Analysis complete.";
    }

    public async Task RunLogcatAsync()
    {
        if (string.IsNullOrEmpty(SelectedDevice) || SelectedPackage == null)
        {
            StatusMessage = "Select device and package first.";
            return;
        }

        StatusMessage = $"Running {SelectedPackage.PackageName} and capturing logs...";
        _adb.SetSerial(SelectedDevice);
        var (exit, stdout, stderr) = await Task.Run(() => _adb.RunLogcat(SelectedPackage.PackageName));

        if (exit == 0)
        {
            Markdown = $"# Logcat Output for {SelectedPackage.PackageName}\n\n```\n{stdout}\n```";
            StatusMessage = "Logs captured.";
        }
        else
        {
            Markdown = $"# Logcat Error\n\n```\n{stderr}\n```";
            StatusMessage = "Failed to capture logs.";
        }
    }
}