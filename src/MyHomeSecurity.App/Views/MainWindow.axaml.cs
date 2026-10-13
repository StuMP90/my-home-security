using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MyHomeSecurity.App.ViewModels;

namespace MyHomeSecurity.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _closing;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        // Any input counts as activity for auto-lock.
        AddHandler(KeyDownEvent, (_, _) => ViewModel?.RecordActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => ViewModel?.RecordActivity(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => FocusPassword();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel != null)
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsLocked) when ViewModel?.IsLocked == true:
                FocusPassword();
                break;
            case nameof(MainWindowViewModel.IsFullScreen):
                ApplyFullScreen(ViewModel?.IsFullScreen == true);
                break;
        }
    }

    private void ApplyFullScreen(bool on)
    {
        if (on && WindowState != WindowState.FullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (!on && WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || ViewModel is not { } vm)
            return;
        // Nothing streams while minimised: nobody can see it.
        vm.IsWindowVisible = WindowState != WindowState.Minimized;
        // Leaving full screen from the window manager (or Esc handled by it) keeps the view model in step.
        if (WindowState != WindowState.FullScreen && vm.IsFullScreen)
            vm.IsFullScreen = false;
    }

    /// <summary>
    /// Application shortcuts:
    ///   Ctrl+1..4               Cameras / Home / Captures / Settings
    ///   F11                     full screen
    ///   Esc                     leave full screen, then back from one camera to the grid
    ///   Left / Right            previous / next camera, while one is shown full size
    ///   Ctrl+Shift+L            lock
    ///   Ctrl+Shift+H            help
    /// </summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        var mods = e.KeyModifiers;
        const KeyModifiers ctrlShift = KeyModifiers.Control | KeyModifiers.Shift;
        if (mods == ctrlShift && e.Key == Key.H)
        {
            ShowHelp();
            e.Handled = true;
            return;
        }
        if (vm == null || vm.IsLocked)
            return;

        var typing = FocusManager?.GetFocusedElement() is TextBox;
        var liveCamera = vm.SelectedTab == MainTab.Cameras && vm.Cameras.IsLive;

        if (mods == ctrlShift && e.Key == Key.L)
            vm.Lock();
        else if (mods == KeyModifiers.Control && e.Key is >= Key.D1 and <= Key.D4)
            vm.SelectedTabIndex = e.Key - Key.D1;
        else if (mods == KeyModifiers.None && e.Key == Key.F11)
            vm.ToggleFullScreenCommand.Execute(null);
        else if (mods == KeyModifiers.None && e.Key == Key.Escape && vm.IsFullScreen)
            vm.IsFullScreen = false;
        else if (mods == KeyModifiers.None && e.Key == Key.Escape && liveCamera)
            vm.Cameras.CloseLiveCommand.Execute(null);
        else if (mods == KeyModifiers.None && liveCamera && !typing && e.Key == Key.Right)
            vm.Cameras.NextCameraCommand.Execute(null);
        else if (mods == KeyModifiers.None && liveCamera && !typing && e.Key == Key.Left)
            vm.Cameras.PreviousCameraCommand.Execute(null);
        else
            return;
        e.Handled = true;
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp() => _ = new HelpWindow().ShowDialog(this);

    private void FocusPassword() => Dispatcher.UIThread.Post(() => PasswordBox.Focus(), DispatcherPriority.Input);

    /// <summary>
    /// Closing asks first if anything is recording, then stops the streams and finishes each
    /// recording properly (so the files play) before the window actually goes.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        var vm = ViewModel;
        if (_closeConfirmed || vm == null)
            return;
        e.Cancel = true;
        if (_closing)
            return;

        if (vm.RunningRecordingCount > 0)
        {
            var dialogs = new Services.DialogService(this);
            if (!await dialogs.ConfirmAsync("Quit My Home Security",
                    $"{vm.RunningRecordingCount} recording(s) are running. Stop them, save what's been recorded, and quit?",
                    "Stop and quit", danger: true))
                return;
        }

        _closing = true;
        Title = "My Home Security — closing…";
        try
        {
            await vm.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(12));
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn("app", "Shutdown didn't finish in time.", ex);
        }
        _closeConfirmed = true;
        Close();
    }
}
