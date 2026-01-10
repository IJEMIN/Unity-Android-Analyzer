using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UnityAndroidAnalyzer.Core;
using UnityAndroidAnalyzer.Gui.ViewModels;
using UnityAndroidAnalyzer.Gui.Views;

namespace UnityAndroidAnalyzer.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var analyzer = new UnityAnalyzer();               // Core 구현체
            var vm       = new MainWindowViewModel(analyzer); // ViewModel

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}