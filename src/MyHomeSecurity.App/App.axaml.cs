using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.App.ViewModels;
using MyHomeSecurity.App.Views;
using MyHomeSecurity.Core;
using MyHomeSecurity.Core.Security;

namespace MyHomeSecurity.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var viewModel = new MainWindowViewModel(new Vault(AppPaths.VaultFile), dialogs);
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) =>
            {
                // Recordings are finished properly so the files play; streams just stop.
                viewModel.ShutdownAsync().Wait(TimeSpan.FromSeconds(12));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
