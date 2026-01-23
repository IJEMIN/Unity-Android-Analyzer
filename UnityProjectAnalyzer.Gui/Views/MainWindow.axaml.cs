using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using UnityProjectAnalyzer.Gui.ViewModels;

namespace UnityProjectAnalyzer.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void SelectApk_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select APK File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Android Package")
                {
                    Patterns = new[] { "*.apk" }
                }
            }
        });

        if (files is { Count: > 0 })
        {
            vm.ApkPath = files[0].Path.LocalPath;
        }
    }

    private async void Analyze_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        await vm.AnalyzeLocalAsync();
    }

    private async void ChangeDownloadPath_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Download Root Path",
            AllowMultiple = false
        });

        if (folders is { Count: > 0 })
        {
            vm.DownloadRootPath = folders[0].Path.LocalPath;
        }
    }
}