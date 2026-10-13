using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Models;
using MyHomeSecurity.Core.Security;

namespace MyHomeSecurity.App.ViewModels;

public enum MainTab
{
    Cameras,
    Home,
    Captures,
    Settings,
}

/// <summary>
/// Owns the vault lifecycle (setup → unlock → auto-lock), the four tabs and the running recordings.
///
/// Locking hides everything behind the password screen, stops every live stream and the Home
/// Assistant polling, and drops the decrypted settings from memory. Recordings already running
/// carry on (each has its own copy of its camera's details) so locking never loses footage.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Vault _vault;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _autoLockTimer;
    private readonly DispatcherTimer _recordingTimer;
    private VaultData? _data;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public MainWindowViewModel(Vault vault, DialogService dialogs)
    {
        _vault = vault;
        _dialogs = dialogs;
        _isSetupRequired = !vault.Exists;
        Cameras = new CamerasViewModel(this, dialogs);
        Home = new HomeViewModel(this, dialogs);
        Captures = new CapturesViewModel(this, dialogs);
        SettingsPage = new SettingsViewModel(this, dialogs);

        _autoLockTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => CheckAutoLock());
        _autoLockTimer.Start();
        _recordingTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var r in Recordings)
                r.Tick();
        });
    }

    public CamerasViewModel Cameras { get; }
    public HomeViewModel Home { get; }
    public CapturesViewModel Captures { get; }
    public SettingsViewModel SettingsPage { get; }
    public DialogService Dialogs => _dialogs;

    /// <summary>Recordings in progress (they keep going while locked).</summary>
    public ObservableCollection<RecordingViewModel> Recordings { get; } = new();

    public AppSettings Settings => _data?.Settings ?? new AppSettings();
    internal VaultData? Data => _data;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockTitle), nameof(LockPrompt), nameof(UnlockButtonText))]
    private bool _isSetupRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordingNote))]
    private bool _isLocked = true;

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _confirmPassword = "";

    [ObservableProperty]
    private string? _lockError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private bool _isBusy;

    /// <summary>Full screen: the top bar and tab headers are hidden.</summary>
    [ObservableProperty]
    private bool _isFullScreen;

    /// <summary>False while the window is minimised: nothing is streamed that nobody can see.</summary>
    [ObservableProperty]
    private bool _isWindowVisible = true;

    /// <summary>The latest message for the status bar ("Snapshot saved…").</summary>
    [ObservableProperty]
    private string? _statusText;

    public MainTab SelectedTab => (MainTab)SelectedTabIndex;

    public string LockTitle => IsSetupRequired ? "Welcome to My Home Security" : "My Home Security is locked";

    public string LockPrompt => IsSetupRequired
        ? $"Create a password. It encrypts your cameras' logins, your Home Assistant token and your settings, and is needed to open the app. Minimum {Vault.MinimumPasswordLength} characters. It cannot be recovered if forgotten."
        : "Enter your password to view the cameras and your home.";

    public string UnlockButtonText => IsSetupRequired ? "Create password" : "Unlock";

    public string AppVersion => AppInfo.DisplayVersion;
    public string AppVersionNumber => AppInfo.Version;

    public bool HasRecordings => Recordings.Count > 0;
    public string RecordingsLabel => Recordings.Count == 1 ? "● 1 recording" : $"● {Recordings.Count} recordings";

    public string? RecordingNote => IsLocked && Recordings.Count > 0
        ? $"{Recordings.Count} recording(s) still running in the background."
        : null;

    public int RunningRecordingCount => Recordings.Count;

    /// <summary>Called by the window on any keyboard or pointer input.</summary>
    public void RecordActivity() => _lastActivityUtc = DateTime.UtcNow;

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        LockError = null;
        var password = Password;
        if (IsSetupRequired && password != ConfirmPassword)
        {
            LockError = "The passwords don't match.";
            return;
        }

        IsBusy = true;
        try
        {
            // Key derivation is deliberately slow; keep it off the UI thread.
            var setup = IsSetupRequired;
            var data = await Task.Run(() => setup ? _vault.Create(password) : _vault.Unlock(password));
            OnUnlocked(data);
        }
        catch (VaultException ex)
        {
            LockError = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("vault", "Unlock failed.", ex);
            LockError = "Could not open the vault: " + ex.Message;
        }
        finally
        {
            Password = "";
            ConfirmPassword = "";
            IsBusy = false;
        }
    }

    private bool CanUnlock() => !IsBusy;

    private void OnUnlocked(VaultData data)
    {
        _data = data;
        IsSetupRequired = false;
        IsLocked = false;
        RecordActivity();
        Cameras.Load(data);
        Home.Load(data);
        SettingsPage.Load(data);
        Log.Info("vault", "Vault unlocked.");
        // A new user has nothing to see yet: start them where they can add a camera.
        UpdateActivity();
    }

    /// <summary>Raised after locking.</summary>
    public event Action? Locked;

    /// <summary>
    /// "Forgot the password?": after a stark warning and typing RESET, sets the old vault aside
    /// (renamed, not deleted) and starts again with a new, empty one.
    /// </summary>
    [RelayCommand]
    private async Task ForgotPasswordAsync()
    {
        if (!await _dialogs.ConfirmAsync("Forgot the password?",
                "The password can't be recovered: it's the key that encrypts everything My Home Security stores.\n\n" +
                "Starting again means losing ALL saved cameras and their logins, the Home Assistant connection and " +
                "tiles, and settings. You'll need to add them again. Snapshots and recordings aren't affected.\n\n" +
                "The old vault isn't deleted: it's renamed and kept, encrypted, in the same folder. If you remember " +
                "the password later, it can be restored by renaming it back to vault.json.",
                "I understand, continue", danger: true))
            return;
        var typed = await _dialogs.PromptTextAsync("Confirm reset", "Type RESET to set the old vault aside and start again:", "");
        if (typed?.Trim() != "RESET")
            return;
        try
        {
            var aside = ResetForgottenVault();
            await _dialogs.AlertAsync("Vault set aside",
                $"The old vault was kept as:\n{aside}\n\nCreate a new password to continue.");
        }
        catch (Exception ex)
        {
            Log.Error("vault", "Setting the vault aside failed.", ex);
            LockError = "Couldn't set the old vault aside: " + ex.Message;
        }
    }

    /// <summary>Sets the vault aside and returns to first-run setup. Returns where it was kept.</summary>
    internal string ResetForgottenVault()
    {
        var aside = _vault.SetAsideForgotten();
        Log.Warn("vault", $"Password forgotten: vault set aside as {aside}; starting a new vault.");
        LockError = null;
        Password = ConfirmPassword = "";
        IsSetupRequired = true;
        return aside;
    }

    [RelayCommand]
    public void Lock()
    {
        if (IsLocked)
            return;
        Cameras.Unload();
        Home.Unload();
        Captures.Unload();
        SettingsPage.Unload();
        _vault.Lock();
        _data = null;
        IsFullScreen = false;
        StatusText = null;
        IsLocked = true;
        Locked?.Invoke();
        Log.Info("vault", "Vault locked.");
    }

    private void CheckAutoLock()
    {
        var minutes = _data?.Settings.AutoLockMinutes ?? 0;
        if (!IsLocked && minutes > 0 && DateTime.UtcNow - _lastActivityUtc > TimeSpan.FromMinutes(minutes))
            Lock();
    }

    /// <summary>A key for another encrypted file, derived from the vault's. Null while locked.</summary>
    internal byte[]? DeriveKey(string purpose) => _vault.DeriveSubkey(purpose);

    public bool SaveVault()
    {
        if (_data == null)
            return false;
        try
        {
            _vault.Save(_data);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("vault", "Save failed.", ex);
            _ = _dialogs.AlertAsync("Save failed", "Could not save the vault: " + ex.Message);
            return false;
        }
    }

    public void ChangePassword(string current, string replacement)
    {
        if (_data == null)
            throw new VaultException("The vault is locked.");
        _vault.ChangePassword(current, replacement, _data);
    }

    partial void OnSelectedTabIndexChanged(int value) => UpdateActivity();

    partial void OnIsWindowVisibleChanged(bool value) => UpdateActivity();

    /// <summary>Starts or stops the work behind each tab so only what's on screen runs.</summary>
    private void UpdateActivity()
    {
        var shown = !IsLocked && IsWindowVisible;
        Cameras.SetActive(shown && SelectedTab == MainTab.Cameras);
        Home.SetActive(shown && SelectedTab == MainTab.Home);
        if (shown && SelectedTab == MainTab.Captures)
            Captures.Refresh();
    }

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    public void Notify(string message)
    {
        StatusText = $"{DateTime.Now:HH:mm:ss}  {message}";
        Log.Info("app", message);
    }

    // ---- Recordings ----

    public bool IsRecording(Guid cameraId) => Recordings.Any(r => r.CameraId == cameraId);

    /// <summary>Raised when a camera starts or stops recording.</summary>
    public event Action<Guid>? RecordingChanged;

    public void StartRecording(CameraProfile camera)
    {
        if (IsRecording(camera.Id) || _data is null)
            return;
        Recorder recorder;
        try
        {
            recorder = Recorder.Start(camera, _data.Settings);
        }
        catch (Exception ex)
        {
            Log.Error("record", "Couldn't start recording.", ex);
            _ = _dialogs.AlertAsync("Recording", $"Couldn't start recording {camera.DisplayName}: {ex.Message}");
            return;
        }
        var item = new RecordingViewModel(recorder, this);
        recorder.Finished += r => Dispatcher.UIThread.Post(() => OnRecordingFinished(item));
        Recordings.Add(item);
        if (!recorder.IsRunning)
        {
            OnRecordingFinished(item);
            return;
        }
        _recordingTimer.Start();
        RecordingsUpdated(camera.Id);
        Notify($"Recording {camera.DisplayName}…");
    }

    public async Task StopRecordingAsync(Guid cameraId)
    {
        var item = Recordings.FirstOrDefault(r => r.CameraId == cameraId);
        if (item is not null)
            await item.Recorder.StopAsync();
    }

    private void OnRecordingFinished(RecordingViewModel item)
    {
        if (!Recordings.Remove(item))
            return;
        if (Recordings.Count == 0)
            _recordingTimer.Stop();
        var r = item.Recorder;
        r.Dispose();
        RecordingsUpdated(r.CameraId);
        var saved = File.Exists(r.FilePath) && new FileInfo(r.FilePath).Length > 0;
        if (r.Error is not null && !saved)
        {
            Notify($"Recording of {r.CameraName} failed: {r.Error}");
            if (!IsLocked)
                _ = _dialogs.AlertAsync("Recording failed", $"The recording of {r.CameraName} failed:\n\n{r.Error}");
        }
        else if (saved)
        {
            Notify($"Recording saved: {Path.GetFileName(r.FilePath)}");
            if (!IsLocked && SelectedTab == MainTab.Captures)
                Captures.Refresh();
        }
        else
        {
            Notify($"Recording of {r.CameraName} ended with nothing saved.");
        }
    }

    private void RecordingsUpdated(Guid cameraId)
    {
        OnPropertyChanged(nameof(HasRecordings));
        OnPropertyChanged(nameof(RecordingsLabel));
        OnPropertyChanged(nameof(RecordingNote));
        OnPropertyChanged(nameof(RunningRecordingCount));
        RecordingChanged?.Invoke(cameraId);
    }

    /// <summary>Closing: stop the streams and finish every recording properly so the files play.</summary>
    public async Task ShutdownAsync()
    {
        _autoLockTimer.Stop();
        Cameras.Unload();
        Home.Unload();
        var stopping = Recordings.Select(r => r.Recorder.StopAsync()).Append(Cameras.FinishPictureWorkAsync()).ToList();
        try
        {
            await Task.WhenAll(stopping).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("record", "A recording didn't stop cleanly.", ex);
        }
        Log.Info("app", "Shut down.");
    }
}

/// <summary>One recording in progress, shown in the recordings menu and on its camera's tile.</summary>
public sealed partial class RecordingViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public RecordingViewModel(Recorder recorder, MainWindowViewModel owner)
    {
        Recorder = recorder;
        _owner = owner;
    }

    public Recorder Recorder { get; }
    public Guid CameraId => Recorder.CameraId;
    public string CameraName => Recorder.CameraName;
    public string FileName => Path.GetFileName(Recorder.FilePath);
    public string ElapsedText => Recorder.Elapsed.ToString(Recorder.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    [ObservableProperty]
    private bool _isStopping;

    public void Tick() => OnPropertyChanged(nameof(ElapsedText));

    [RelayCommand]
    private async Task StopAsync()
    {
        IsStopping = true;
        await Recorder.StopAsync();
    }
}
