using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.App.ViewModels;
using MyHomeSecurity.App.Views;
using MyHomeSecurity.Core.Models;
using MyHomeSecurity.Core.Security;

namespace MyHomeSecurity.App.Tests;

/// <summary>
/// Drives the real window off-screen. Screenshots go to $MYHOMESECURITY_SCREENSHOT_DIR (default: temp).
/// </summary>
public sealed class UiTests : IDisposable
{
    private const string Password = "correct-horse-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhs-ui-" + Guid.NewGuid().ToString("N"));
    private readonly List<MainWindow> _windows = [];

    private string VaultPath => Path.Combine(_dir, "vault.json");

    public void Dispose()
    {
        foreach (var w in _windows)
            (w.DataContext as MainWindowViewModel)?.Lock();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [AvaloniaFact]
    public async Task FirstRunAsksForThePasswordTwice()
    {
        var (window, vm) = Create();
        Assert.True(vm.IsSetupRequired);
        Assert.True(vm.IsLocked);
        Save(window, "01-setup");

        vm.Password = Password;
        vm.ConfirmPassword = "something-else";
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.True(vm.IsLocked);
        Assert.Equal("The passwords don't match.", vm.LockError);
        Assert.False(File.Exists(VaultPath));

        await CreateVault(vm);
        Assert.False(vm.IsLocked);
        Assert.True(File.Exists(VaultPath));
        Save(window, "02-cameras-empty");
    }

    [AvaloniaFact]
    public async Task LockingForgetsSecretsAndUnlockingBringsThemBack()
    {
        var (window, vm) = Create();
        await CreateVault(vm);
        vm.Data!.Cameras.Add(new CameraProfile { Name = "Porch", Host = "127.0.0.1", Port = 1, Username = "admin", Password = "cam-secret" });
        vm.Data.HomeAssistant.ServerUrl = "http://homeassistant.local:8123";
        vm.Data.HomeAssistant.Token = "ha-token-secret";
        Assert.True(vm.SaveVault());

        vm.Lock();
        Assert.True(vm.IsLocked);
        Assert.Null(vm.Data);
        Assert.Equal("", vm.SettingsPage.HaToken);
        Save(window, "03-locked");

        vm.Password = "wrong-password";
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.True(vm.IsLocked);
        Assert.NotNull(vm.LockError);
        Assert.Equal("", vm.Password); // never left lying in the box

        vm.Password = Password;
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.False(vm.IsLocked);
        Assert.Equal("cam-secret", Assert.Single(vm.Data!.Cameras).Password);
        Assert.Equal("ha-token-secret", vm.SettingsPage.HaToken);

        var raw = File.ReadAllText(VaultPath);
        Assert.DoesNotContain("cam-secret", raw);
        Assert.DoesNotContain("ha-token-secret", raw);
        Assert.DoesNotContain("Porch", raw);
    }

    [AvaloniaFact]
    public async Task SettingsAreEditedAsADraftAndSaved()
    {
        var (window, vm) = Create();
        await CreateVault(vm);
        vm.SelectedTabIndex = (int)MainTab.Settings;

        var page = vm.SettingsPage;
        Assert.False(page.HasChanges);
        page.LiveFps = 0;
        page.GridKeyframesOnly = true;
        page.HaServerUrl = "http://ha.example.com";
        Assert.True(page.HasChanges);
        Assert.True(page.IsInsecure);
        Save(window, "04-settings");

        page.SaveCommand.Execute(null);
        Assert.False(page.HasChanges);
        Assert.Equal(0, vm.Settings.LiveFps);
        Assert.True(vm.Settings.GridKeyframesOnly);

        page.GridFps = 12;
        page.RevertCommand.Execute(null);
        Assert.Equal(vm.Settings.GridFps, (int)page.GridFps!);
    }

    [AvaloniaFact]
    public async Task ForgottenPasswordSetsTheVaultAsideAndStartsAgain()
    {
        var (_, vm) = Create();
        await CreateVault(vm);
        vm.Lock();

        var aside = vm.ResetForgottenVault();

        Assert.True(File.Exists(aside));
        Assert.False(File.Exists(VaultPath));
        Assert.True(vm.IsSetupRequired);
        await CreateVault(vm);
        Assert.False(vm.IsLocked);
        Assert.Empty(vm.Data!.Cameras);
    }

    [AvaloniaFact]
    public async Task OtherTabsRenderWhenEmpty()
    {
        var (window, vm) = Create();
        await CreateVault(vm);
        vm.SelectedTabIndex = (int)MainTab.Home;
        Save(window, "05-home-empty");
        vm.SelectedTabIndex = (int)MainTab.Captures;
        Save(window, "06-captures");
    }

    [AvaloniaFact]
    public async Task NoTilesButtonOpensTheTileEditor()
    {
        var (window, vm) = Create();
        await CreateVault(vm);
        // Nothing listens here; the tile editor still opens.
        vm.Data!.HomeAssistant.ServerUrl = "http://127.0.0.1:1";
        vm.Data.HomeAssistant.Token = "ha-token";
        Assert.True(vm.SaveVault());
        vm.Home.Reload();
        vm.SelectedTabIndex = (int)MainTab.Home;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        // Click it with the mouse, as a person would, so anything drawn over it would catch the click.
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("accent") && Equals(b.Content, "Add tile…") && b.IsEffectivelyVisible);
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Home.Editor);
    }

    private (MainWindow Window, MainWindowViewModel Vm) Create()
    {
        Directory.CreateDirectory(_dir);
        var window = new MainWindow { Width = 1280, Height = 800 };
        var vm = new MainWindowViewModel(new Vault(VaultPath), new DialogService(window));
        window.DataContext = vm;
        window.Show();
        _windows.Add(window);
        return (window, vm);
    }

    private static async Task CreateVault(MainWindowViewModel vm)
    {
        vm.Password = Password;
        vm.ConfirmPassword = Password;
        await vm.UnlockCommand.ExecuteAsync(null);
        Assert.Null(vm.LockError);
    }

    private static void Save(MainWindow window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("MYHOMESECURITY_SCREENSHOT_DIR")
            ?? Path.Combine(Path.GetTempPath(), "myhomesecurity-screenshots");
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"));
    }
}
