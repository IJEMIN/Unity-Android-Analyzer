using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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

    [ObservableProperty]
    private string downloadRootPath = "";

    // 분석 결과 필드들
    [ObservableProperty] private string resultUnityVersion = "";
    [ObservableProperty] private string resultRenderPipeline = "";
    [ObservableProperty] private string resultEntitiesUsed = "";
    [ObservableProperty] private string resultEntitiesPhysicsUsed = "";
    [ObservableProperty] private string resultNguiUsed = "";
    [ObservableProperty] private string resultAddressablesUsed = "";
    [ObservableProperty] private string resultHavokUsed = "";
    [ObservableProperty] private string resultUiToolkitUsed = "";
    [ObservableProperty] private ObservableCollection<string> resultMajorScriptInsights = new();

    private string? _currentMetadataPath;
    private string? _currentScriptingPath;

    public MainWindowViewModel(IUnityAnalyzer analyzer)
    {
        _analyzer = analyzer;
        downloadRootPath = _analyzer.DownloadRootPath;

        RefreshDevicesCommand = new AsyncRelayCommand(RefreshDevicesAsync);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        SearchPackagesCommand = new AsyncRelayCommand(SearchPackagesAsync);
        AnalyzeDeviceCommand = new AsyncRelayCommand(AnalyzeDeviceAsync);
        OpenMetadataCommand = new RelayCommand(OpenMetadata);
        OpenAssembliesCommand = new RelayCommand(OpenAssemblies);
        ChangeDownloadPathCommand = new AsyncRelayCommand(ChangeDownloadPathAsync);
    }

    public IAsyncRelayCommand RefreshDevicesCommand { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand SearchPackagesCommand { get; }
    public IAsyncRelayCommand AnalyzeDeviceCommand { get; }
    public IRelayCommand OpenMetadataCommand { get; }
    public IRelayCommand OpenAssembliesCommand { get; }
    public IAsyncRelayCommand ChangeDownloadPathCommand { get; }

    partial void OnDownloadRootPathChanged(string value)
    {
        _analyzer.DownloadRootPath = value;
    }

    public async Task ChangeDownloadPathAsync()
    {
        // View에서 처리하도록 이벤트를 주거나, 직접 FileDialog를 띄울 수 없으므로 
        // 일단 ViewModel에서는 로직만 준비하고 View에서 호출하게 함.
        // 하지만 여기서는 CommunityToolkit.Mvvm을 쓰고 있으므로, 
        // 실제 구현은 View의 비하인드 코드에서 처리하는 것이 일반적임.
    }

    private void UpdateResult(AnalysisResult result)
    {
        ResultUnityVersion = result.UnityVersion;
        ResultRenderPipeline = result.RenderPipeline;
        ResultEntitiesUsed = result.EntitiesUsed;
        ResultEntitiesPhysicsUsed = result.EntitiesPhysicsUsed;
        ResultNguiUsed = result.NguiUsed;
        ResultAddressablesUsed = result.AddressablesUsed;
        ResultHavokUsed = result.HavokUsed;
        
        if (result.UiToolkitUsed.Contains("yes", StringComparison.OrdinalIgnoreCase))
        {
            ResultUiToolkitUsed = "Runtime UIToolkit Detected";
        }
        else
        {
            ResultUiToolkitUsed = result.UiToolkitUsed;
        }
        
        ResultMajorScriptInsights.Clear();
        foreach (var insight in result.MajorScriptInsights)
        {
            ResultMajorScriptInsights.Add($"{insight.Script} ({insight.Count})");
        }

        _currentMetadataPath = result.MetadataPath;
        _currentScriptingPath = result.ScriptingAssembliesPath;
    }

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
        UpdateResult(result);
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
        try
        {
            var result = await _analyzer.AnalyzeDeviceAsync(SelectedDevice, SelectedPackage.PackageName);
            UpdateResult(result);
            StatusMessage = "Analysis complete.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private void OpenMetadata()
    {
        if (string.IsNullOrEmpty(_currentMetadataPath) || !File.Exists(_currentMetadataPath))
        {
            StatusMessage = "Metadata file not available. Run analysis first.";
            return;
        }
        OpenFileWithDefaultEditor(_currentMetadataPath);
    }

    private void OpenAssemblies()
    {
        if (string.IsNullOrEmpty(_currentScriptingPath) || !File.Exists(_currentScriptingPath))
        {
            StatusMessage = "ScriptingAssemblies file not available. Run analysis first.";
            return;
        }
        OpenFileWithDefaultEditor(_currentScriptingPath);
    }

    private void OpenFileWithDefaultEditor(string path)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };
            process.Start();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open file: {ex.Message}";
        }
    }
}