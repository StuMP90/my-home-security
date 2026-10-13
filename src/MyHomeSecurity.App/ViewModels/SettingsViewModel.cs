using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.Core;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.HomeAssistant;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;
using MyHomeSecurity.Core.Security;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>
/// Everything the user can change, edited as a draft and applied with Save. Nothing here is
/// written anywhere except the encrypted vault.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly DialogService _dialogs;
    private VaultData? _data;
    private bool _loading;

    public SettingsViewModel(MainWindowViewModel main, DialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
    }

    public static int[] LiveHeights { get; } = [480, 720, 1080, 1440, 2160];
    public string[] LiveHeightNames { get; } = ["480p (lightest)", "720p", "1080p", "1440p", "2160p (4K)"];
    public string[] RecordingFormats { get; } = ["MP4 (plays everywhere)", "MKV (keeps the camera's audio as-is)"];
    public string[] SnapshotFormats { get; } = ["JPEG", "PNG (lossless, larger)"];

    // ---- Home Assistant ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsecure))]
    private string _haServerUrl = "";

    [ObservableProperty]
    private string _haToken = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenChar))]
    private bool _showToken;

    [ObservableProperty]
    private decimal? _haPollSeconds = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHaResult))]
    private string? _haResult;

    [ObservableProperty]
    private bool _haResultIsError;

    [ObservableProperty]
    private bool _isTesting;

    public char TokenChar => ShowToken ? '\0' : '●';
    public bool IsInsecure => HaClient.IsInsecure(HaServerUrl);
    public bool HasHaResult => HaResult is not null;

    // ---- Video ----

    [ObservableProperty]
    private bool _hardwareDecoding;

    [ObservableProperty]
    private decimal? _gridFps;

    [ObservableProperty]
    private bool _gridKeyframesOnly;

    [ObservableProperty]
    private decimal? _liveFps;

    [ObservableProperty]
    private int _liveHeightIndex;

    [ObservableProperty]
    private bool _keepGridRunning;

    // ---- Captures ----

    [ObservableProperty]
    private string _snapshotFolder = "";

    [ObservableProperty]
    private string _recordingFolder = "";

    [ObservableProperty]
    private int _recordingFormatIndex;

    [ObservableProperty]
    private int _snapshotFormatIndex;

    [ObservableProperty]
    private decimal? _maxRecordingMinutes;

    public string DefaultSnapshotFolder => AppPaths.DefaultSnapshotDirectory;
    public string DefaultRecordingFolder => AppPaths.DefaultRecordingDirectory;

    // ---- Security ----

    [ObservableProperty]
    private decimal? _autoLockMinutes;

    // ---- State ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _hasChanges;

    [ObservableProperty]
    private string? _saveMessage;

    public bool IsDirty => HasChanges;

    public string FfmpegStatus => FfmpegLocator.Default.IsAvailable
        ? $"ffmpeg {FfmpegLocator.Default.Version} — {FfmpegLocator.Default.FfmpegPath}"
        : $"ffmpeg wasn't found. Install it (Linux: your package manager; Windows: put ffmpeg.exe beside the app) or set {FfmpegLocator.DirEnvironmentVariable}.";

    public string DataFolder => AppPaths.DataDirectory;
    public string VersionText => AppInfo.DisplayVersion;

    public void Load(VaultData data)
    {
        _data = data;
        Revert();
    }

    public void Unload()
    {
        _data = null;
        _loading = true;
        HaToken = "";
        HaServerUrl = "";
        HaResult = null;
        _loading = false;
        HasChanges = false;
        SaveMessage = null;
    }

    [RelayCommand]
    private void Revert()
    {
        if (_data is null)
            return;
        _loading = true;
        var ha = _data.HomeAssistant;
        HaServerUrl = ha.ServerUrl;
        HaToken = ha.Token;
        HaPollSeconds = (decimal)ha.PollSeconds;
        HaResult = null;

        var s = _data.Settings;
        HardwareDecoding = s.HardwareDecoding;
        GridFps = s.GridFps;
        GridKeyframesOnly = s.GridKeyframesOnly;
        LiveFps = s.LiveFps;
        var heightIndex = Array.IndexOf(LiveHeights, s.LiveHeight);
        LiveHeightIndex = heightIndex >= 0 ? heightIndex : 2;
        KeepGridRunning = s.KeepGridRunning;
        SnapshotFolder = s.SnapshotFolder ?? "";
        RecordingFolder = s.RecordingFolder ?? "";
        RecordingFormatIndex = (int)s.RecordingFormat;
        SnapshotFormatIndex = (int)s.SnapshotFormat;
        MaxRecordingMinutes = s.MaxRecordingMinutes;
        AutoLockMinutes = s.AutoLockMinutes;
        _loading = false;
        HasChanges = false;
        SaveMessage = null;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
            return;
        switch (e.PropertyName)
        {
            case nameof(HaServerUrl) or nameof(HaToken) or nameof(HaPollSeconds)
                or nameof(HardwareDecoding) or nameof(GridFps) or nameof(GridKeyframesOnly) or nameof(LiveFps)
                or nameof(LiveHeightIndex) or nameof(KeepGridRunning) or nameof(SnapshotFolder) or nameof(RecordingFolder)
                or nameof(RecordingFormatIndex) or nameof(SnapshotFormatIndex) or nameof(MaxRecordingMinutes)
                or nameof(AutoLockMinutes):
                HasChanges = true;
                SaveMessage = null;
                break;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (_data is null)
            return;
        var ha = _data.HomeAssistant;
        var url = HaServerUrl.Trim();
        if (url.Length > 0 && HaClient.NormaliseServerUrl(url) is null)
        {
            SaveMessage = "The Home Assistant address isn't valid. It should look like http://homeassistant.local:8123.";
            return;
        }
        var haChanged = ha.ServerUrl != url || ha.Token != HaToken.Trim() || ha.PollSeconds != (double)(HaPollSeconds ?? 2);
        ha.ServerUrl = url;
        ha.Token = HaToken.Trim();
        ha.PollSeconds = Math.Max(HomeAssistantSettings.MinimumPollSeconds, (double)(HaPollSeconds ?? 2));

        var s = _data.Settings;
        var streamsChanged = s.HardwareDecoding != HardwareDecoding || s.GridFps != (int)(GridFps ?? 5)
            || s.GridKeyframesOnly != GridKeyframesOnly || s.LiveFps != (int)(LiveFps ?? 15)
            || s.LiveHeight != LiveHeights[Math.Clamp(LiveHeightIndex, 0, LiveHeights.Length - 1)]
            || s.KeepGridRunning != KeepGridRunning;
        s.HardwareDecoding = HardwareDecoding;
        s.GridFps = Math.Clamp((int)(GridFps ?? 5), 1, 30);
        s.GridKeyframesOnly = GridKeyframesOnly;
        s.LiveFps = Math.Clamp((int)(LiveFps ?? 15), 0, 60);
        s.LiveHeight = LiveHeights[Math.Clamp(LiveHeightIndex, 0, LiveHeights.Length - 1)];
        s.KeepGridRunning = KeepGridRunning;
        s.SnapshotFolder = string.IsNullOrWhiteSpace(SnapshotFolder) ? null : SnapshotFolder.Trim();
        s.RecordingFolder = string.IsNullOrWhiteSpace(RecordingFolder) ? null : RecordingFolder.Trim();
        s.RecordingFormat = (RecordingFormat)Math.Clamp(RecordingFormatIndex, 0, 1);
        s.SnapshotFormat = (SnapshotFormat)Math.Clamp(SnapshotFormatIndex, 0, 1);
        s.MaxRecordingMinutes = Math.Clamp((int)(MaxRecordingMinutes ?? 60), 0, 24 * 60);
        s.AutoLockMinutes = Math.Clamp((int)(AutoLockMinutes ?? 30), 0, 24 * 60);

        if (!_main.SaveVault())
            return;
        if (streamsChanged)
            _main.Cameras.RestartStreams();
        if (haChanged)
            _main.Home.Reload();
        Revert();
        SaveMessage = "Saved.";
        _main.Notify("Settings saved.");
    }

    [RelayCommand]
    private async Task TestHomeAssistantAsync()
    {
        HaResult = null;
        if (string.IsNullOrWhiteSpace(HaServerUrl) || string.IsNullOrWhiteSpace(HaToken))
        {
            HaResultIsError = true;
            HaResult = "Enter the address and the access token first.";
            return;
        }
        IsTesting = true;
        try
        {
            using var client = new HaClient(HaServerUrl.Trim(), HaToken.Trim(), TimeSpan.FromSeconds(10));
            var message = await client.TestAsync();
            HaResultIsError = false;
            HaResult = "Connected: " + message;
        }
        catch (Exception ex)
        {
            HaResultIsError = true;
            HaResult = ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task BrowseSnapshotFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Snapshot folder",
            string.IsNullOrWhiteSpace(SnapshotFolder) ? DefaultSnapshotFolder : SnapshotFolder);
        if (folder is not null)
            SnapshotFolder = folder;
    }

    [RelayCommand]
    private async Task BrowseRecordingFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Recording folder",
            string.IsNullOrWhiteSpace(RecordingFolder) ? DefaultRecordingFolder : RecordingFolder);
        if (folder is not null)
            RecordingFolder = folder;
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            CapturesViewModel.ShellOpen(AppPaths.DataDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Couldn't open the data folder: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        var current = await _dialogs.PromptSecretAsync("Change password", "Current password:", null, "Current password");
        if (current is null)
            return;
        string? error = null;
        string? replacement;
        while (true)
        {
            replacement = await _dialogs.PromptSecretAsync("Change password",
                $"New password (at least {Vault.MinimumPasswordLength} characters):", error, "New password");
            if (replacement is null)
                return;
            if (replacement.Length < Vault.MinimumPasswordLength)
            {
                error = $"It must be at least {Vault.MinimumPasswordLength} characters.";
                continue;
            }
            var again = await _dialogs.PromptSecretAsync("Change password", "Type the new password again:", null, "New password");
            if (again is null)
                return;
            if (again != replacement)
            {
                error = "The passwords didn't match. Try again.";
                continue;
            }
            break;
        }
        try
        {
            var (c, r) = (current, replacement);
            await Task.Run(() => _main.ChangePassword(c, r));
            await _dialogs.AlertAsync("Change password", "The password was changed.");
        }
        catch (VaultException ex)
        {
            await _dialogs.AlertAsync("Change password", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("vault", "Changing the password failed.", ex);
            await _dialogs.AlertAsync("Change password", "The password couldn't be changed: " + ex.Message);
        }
    }
}
