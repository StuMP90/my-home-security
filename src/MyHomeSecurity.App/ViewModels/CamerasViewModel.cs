using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.App.Controls;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.Core;
using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>
/// The Cameras tab: the grid of every camera, one camera full size ("live"), and the camera editor
/// and discovery panels that open over them.
///
/// To keep the load down, streams run only while they're on screen: nothing while locked, minimised
/// or on another tab, and the grid pauses while one camera is shown full size (unless "keep the grid
/// running" is set). The grid uses each camera's low-resolution substream, scaled down and at a few
/// frames a second; only the full-size view decodes the main stream.
/// </summary>
public sealed partial class CamerasViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly DialogService _dialogs;
    private readonly PictureStore _pictures = new(AppPaths.PictureDirectory);
    private VaultData? _data;
    private bool _active;

    // The key for the saved pictures while unlocked, and the saving and loading, done one at a
    // time off the UI thread.
    private byte[]? _pictureKey;
    private Task _pictureWork = Task.CompletedTask;

    public CamerasViewModel(MainWindowViewModel main, DialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        _main.RecordingChanged += OnRecordingChanged;
        Tiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(GridColumns));
            OnPropertyChanged(nameof(HasCameras));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(IsGrid));
        };
    }

    internal MainWindowViewModel Main => _main;

    public ObservableCollection<CameraTileViewModel> Tiles { get; } = new();

    public bool HasCameras => Tiles.Count > 0;
    public bool ShowEmpty => Tiles.Count == 0 && Editor is null && Discovery is null;

    /// <summary>Columns for the grid: 1, 2, 2, 2, 3, 3, 3, 3, 3, 4…</summary>
    public int GridColumns => Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Tiles.Count)));

    /// <summary>The camera shown full size, or null for the grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(IsGrid), nameof(LiveLastFrame))]
    private CameraTileViewModel? _liveTile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveStatusText), nameof(ShowLiveStatus))]
    private CameraStream? _liveStream;

    // The last full-size picture, and which camera it's from.
    private (Guid CameraId, Bitmap Frame)? _liveLastFrame;

    /// <summary>
    /// Shown until the full-size stream's first frame: the camera's last full-size picture, or
    /// failing that its last grid picture.
    /// </summary>
    public Bitmap? LiveLastFrame =>
        LiveTile is null ? null
        : _liveLastFrame is { } last && last.CameraId == LiveTile.Profile.Id ? last.Frame
        : LiveTile.LastFrame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveStatusText), nameof(ShowLiveStatus))]
    private CameraStreamState _liveState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveStatusText))]
    private string _liveMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGrid), nameof(ShowEmpty), nameof(IsPanelOpen))]
    private CameraEditorViewModel? _editor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGrid), nameof(ShowEmpty), nameof(IsPanelOpen))]
    private DiscoveryViewModel? _discovery;

    /// <summary>Shows the reorder and edit buttons on each tile.</summary>
    [ObservableProperty]
    private bool _isArranging;

    public bool IsPanelOpen => Editor is not null || Discovery is not null;
    public bool IsLive => LiveTile is not null && !IsPanelOpen;
    public bool IsGrid => LiveTile is null && !IsPanelOpen && Tiles.Count > 0;

    public bool ShowLiveStatus => LiveStream is null || LiveState != CameraStreamState.Playing;

    public string LiveStatusText => LiveStream is null ? ""
        : LiveState switch
        {
            CameraStreamState.Connecting => "Connecting to the main stream…",
            CameraStreamState.Error => string.IsNullOrWhiteSpace(LiveMessage) ? "Can't connect, retrying…" : LiveMessage,
            _ => "",
        };

    public bool FfmpegMissing => !FfmpegLocator.Default.IsAvailable;

    public string FfmpegMissingText =>
        "ffmpeg wasn't found, so cameras can't be shown. Install it (for example \"sudo apt install ffmpeg\" " +
        $"or from ffmpeg.org) or set {FfmpegLocator.DirEnvironmentVariable} to the folder that contains it, then restart.";

    // ---- Lifecycle ----

    public void Load(VaultData data)
    {
        Unload();
        _data = data;
        foreach (var camera in data.Cameras)
            Tiles.Add(NewTile(camera));
        OnPropertyChanged(nameof(IsGrid));
        _pictureKey = _main.DeriveKey(PictureStore.KeyPurpose);
        LoadPictures();
        UpdateStreams();
    }

    public void Unload()
    {
        _active = false;
        Discovery?.Cancel();
        Discovery = null;
        Editor?.Close();
        Editor = null;
        LiveTile = null;
        StopAll();
        Tiles.Clear();
        _liveLastFrame = null;
        if (_pictureKey is not null)
            CryptographicOperations.ZeroMemory(_pictureKey);
        _pictureKey = null;
        IsArranging = false;
        _data = null;
    }

    /// <summary>The tab is on screen (unlocked, selected, window not minimised) or not.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        UpdateStreams();
        Editor?.SetActive(active);
    }

    /// <summary>Stream settings changed: start again with the new ones.</summary>
    public void RestartStreams()
    {
        StopAll();
        UpdateStreams();
    }

    private CameraTileViewModel NewTile(CameraProfile camera) => new(camera, this)
    {
        IsRecording = _main.IsRecording(camera.Id),
    };

    /// <summary>Starts the streams that are visible and stops the rest.</summary>
    private void UpdateStreams()
    {
        var settings = _data?.Settings;
        var shown = _active && settings is not null && !IsPanelOpen;
        var gridShown = shown && (LiveTile is null || settings!.KeepGridRunning);
        if (settings is not null && !FfmpegLocator.Default.IsAvailable)
            gridShown = shown = false;

        foreach (var tile in Tiles)
        {
            var want = gridShown && tile.Enabled;
            if (want && tile.Stream is null)
            {
                var stream = new CameraStream(tile.Profile, StreamOptions.ForGrid(settings!));
                tile.Stream = stream;
                stream.Start();
            }
            else if (!want)
            {
                tile.StopStream();
            }
        }

        var liveWanted = shown && LiveTile is { Enabled: true };
        if (liveWanted && (LiveStream is null || LiveStream.CameraId != LiveTile!.Profile.Id))
        {
            SetLiveStream(null);
            var stream = new CameraStream(LiveTile!.Profile, StreamOptions.ForLive(settings!));
            SetLiveStream(stream);
            stream.Start();
        }
        else if (!liveWanted)
        {
            SetLiveStream(null);
        }
    }

    private void SetLiveStream(CameraStream? stream)
    {
        var old = LiveStream;
        if (ReferenceEquals(old, stream))
            return;
        if (old is not null)
        {
            old.StateChanged -= OnLiveStateChanged;
            if (VideoView.LatestFrameOf(old) is { } frame)
            {
                _liveLastFrame = (old.CameraId, frame);
                OnPropertyChanged(nameof(LiveLastFrame));
            }
        }
        LiveStream = stream;
        old?.Dispose();
        if (stream is not null)
        {
            stream.StateChanged += OnLiveStateChanged;
            LiveState = stream.State;
            LiveMessage = stream.StatusMessage;
        }
    }

    private void OnLiveStateChanged()
    {
        var stream = LiveStream;
        Dispatcher.UIThread.Post(() =>
        {
            if (stream is null || !ReferenceEquals(stream, LiveStream))
                return;
            LiveState = stream.State;
            LiveMessage = stream.StatusMessage;
        });
    }

    private void StopAll()
    {
        SetLiveStream(null);
        foreach (var tile in Tiles)
            tile.StopStream();
    }

    // ---- Last pictures, kept between runs ----

    /// <summary>Saves a camera's last grid picture, encrypted, for next time.</summary>
    internal void SavePicture(Guid cameraId, Bitmap picture) => QueuePictureWork(key =>
    {
        using var png = new MemoryStream();
        picture.Save(png);
        _pictures.Save(key, cameraId, png.ToArray());
    });

    private void ForgetPicture(Guid cameraId) => QueuePictureWork(_ => _pictures.Delete(cameraId));

    /// <summary>Shows each camera's saved picture until its stream starts.</summary>
    private void LoadPictures()
    {
        var cameraIds = Tiles.Select(t => t.Profile.Id).ToList();
        QueuePictureWork(key =>
        {
            foreach (var cameraId in cameraIds)
            {
                if (_pictures.Load(key, cameraId) is not { } png)
                    continue;
                using var stream = new MemoryStream(png);
                var picture = new Bitmap(stream);
                Dispatcher.UIThread.Post(() =>
                {
                    if (Tiles.FirstOrDefault(t => t.Profile.Id == cameraId) is { LastFrame: null } tile)
                        tile.LastFrame = picture;
                });
            }
        });
    }

    /// <summary>Done when the pictures queued so far are saved.</summary>
    public Task FinishPictureWorkAsync() => _pictureWork;

    // Each job gets its own copy of the key, so locking (which zeroes ours) doesn't affect it.
    private void QueuePictureWork(Action<byte[]> work)
    {
        if (_pictureKey is null)
            return;
        var key = (byte[])_pictureKey.Clone();
        _pictureWork = _pictureWork.ContinueWith(_ =>
        {
            try
            {
                work(key);
            }
            catch (Exception ex)
            {
                Log.Warn("camera", "Saving or loading a camera's last picture failed.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void OnRecordingChanged(Guid cameraId)
    {
        foreach (var tile in Tiles.Where(t => t.Profile.Id == cameraId))
            tile.IsRecording = _main.IsRecording(cameraId);
        OnPropertyChanged(nameof(LiveIsRecording));
    }

    // ---- Live (one camera full size) ----

    public bool LiveIsRecording => LiveTile is not null && _main.IsRecording(LiveTile.Profile.Id);

    partial void OnLiveTileChanged(CameraTileViewModel? value)
    {
        OnPropertyChanged(nameof(LiveIsRecording));
        UpdateStreams();
    }

    public void OpenLive(CameraTileViewModel tile)
    {
        if (!tile.Enabled || IsArranging)
            return;
        LiveTile = tile;
    }

    [RelayCommand]
    private void CloseLive()
    {
        LiveTile = null;
        _main.IsFullScreen = false;
    }

    /// <summary>Next or previous camera while one is shown full size.</summary>
    [RelayCommand]
    private void NextCamera() => StepLive(+1);

    [RelayCommand]
    private void PreviousCamera() => StepLive(-1);

    private void StepLive(int step)
    {
        if (LiveTile is null)
            return;
        var enabled = Tiles.Where(t => t.Enabled).ToList();
        if (enabled.Count < 2)
            return;
        var index = enabled.IndexOf(LiveTile);
        LiveTile = enabled[((index + step) % enabled.Count + enabled.Count) % enabled.Count];
    }

    [RelayCommand]
    private Task LiveSnapshotAsync() => LiveTile is null ? Task.CompletedTask : TakeSnapshotAsync(LiveTile);

    [RelayCommand]
    private Task LiveToggleRecordingAsync() => LiveTile is null ? Task.CompletedTask : ToggleRecordingAsync(LiveTile);

    // ---- Snapshots and recordings ----

    public async Task TakeSnapshotAsync(CameraTileViewModel tile)
    {
        if (_data is null || tile.IsTakingSnapshot)
            return;
        tile.IsTakingSnapshot = true;
        var camera = tile.Profile.Clone();
        var settings = _data.Settings;
        try
        {
            // Taken from the main stream at full resolution, not from the scaled picture on screen.
            var path = await Snapshot.TakeAsync(camera, settings);
            _main.Notify($"Snapshot saved: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error("snapshot", $"Snapshot of {camera.DisplayName} failed.", ex);
            _main.Notify($"Snapshot of {camera.DisplayName} failed: {ex.Message}");
        }
        finally
        {
            tile.IsTakingSnapshot = false;
        }
    }

    public async Task ToggleRecordingAsync(CameraTileViewModel tile)
    {
        if (_main.IsRecording(tile.Profile.Id))
            await _main.StopRecordingAsync(tile.Profile.Id);
        else
            _main.StartRecording(tile.Profile.Clone());
    }

    // ---- Adding, editing, arranging ----

    [RelayCommand]
    private void AddCamera() => OpenEditor(new CameraProfile(), isNew: true, onvifAddress: null);

    public void EditCamera(CameraTileViewModel tile)
    {
        LiveTile = null;
        OpenEditor(tile.Profile.Clone(), isNew: false, onvifAddress: null);
    }

    internal void OpenEditor(CameraProfile profile, bool isNew, string? onvifAddress)
    {
        Editor?.Close();
        Discovery?.Cancel();
        Discovery = null;
        Editor = new CameraEditorViewModel(this, _dialogs, profile, isNew, onvifAddress);
        Editor.SetActive(_active);
        UpdateStreams();
    }

    internal void CloseEditor()
    {
        Editor?.Close();
        Editor = null;
        UpdateStreams();
    }

    /// <summary>Saves the editor's camera into the vault (adding it if new). Returns false if the save failed.</summary>
    internal bool SaveCamera(CameraProfile profile, bool isNew)
    {
        if (_data is null)
            return false;
        var index = _data.Cameras.FindIndex(c => c.Id == profile.Id);
        if (index >= 0)
            _data.Cameras[index] = profile;
        else
            _data.Cameras.Add(profile);
        if (!_main.SaveVault())
            return false;

        var tile = Tiles.FirstOrDefault(t => t.Profile.Id == profile.Id);
        if (tile is null)
        {
            Tiles.Add(NewTile(profile));
        }
        else
        {
            // Restart its stream with the new details. The old picture may be from another camera.
            tile.StopStream(keepPicture: false);
            tile.LastFrame = null;
            ForgetPicture(profile.Id);
            tile.Profile = profile;
        }
        _main.Notify(isNew ? $"Added {profile.DisplayName}." : $"Saved {profile.DisplayName}.");
        return true;
    }

    public async Task DeleteCameraAsync(CameraTileViewModel tile)
    {
        if (_data is null)
            return;
        if (!await _dialogs.ConfirmAsync("Remove camera",
                $"Remove {tile.Name} and its saved login from My Home Security?\n\nSnapshots and recordings already taken are kept.",
                "Remove", danger: true))
            return;
        if (_main.IsRecording(tile.Profile.Id))
            await _main.StopRecordingAsync(tile.Profile.Id);
        if (ReferenceEquals(LiveTile, tile))
            LiveTile = null;
        tile.StopStream(keepPicture: false);
        ForgetPicture(tile.Profile.Id);
        _data.Cameras.RemoveAll(c => c.Id == tile.Profile.Id);
        Tiles.Remove(tile);
        _main.SaveVault();
        if (Editor is { } editor && editor.CameraId == tile.Profile.Id)
            CloseEditor();
        _main.Notify($"Removed {tile.Name}.");
    }

    public void MoveCamera(CameraTileViewModel tile, int step)
    {
        if (_data is null)
            return;
        var from = Tiles.IndexOf(tile);
        var to = from + step;
        if (from < 0 || to < 0 || to >= Tiles.Count)
            return;
        Tiles.Move(from, to);
        var camera = _data.Cameras.FirstOrDefault(c => c.Id == tile.Profile.Id);
        if (camera is not null)
        {
            _data.Cameras.Remove(camera);
            _data.Cameras.Insert(Math.Min(to, _data.Cameras.Count), camera);
        }
        _main.SaveVault();
    }

    [RelayCommand]
    private void ToggleArranging() => IsArranging = !IsArranging;

    // ---- Discovery ----

    [RelayCommand]
    private void FindCameras()
    {
        LiveTile = null;
        CloseEditor();
        Discovery?.Cancel();
        Discovery = new DiscoveryViewModel(this, _data?.Cameras.Select(c => c.Host) ?? []);
        UpdateStreams();
        _ = Discovery.SearchAsync();
    }

    internal void CloseDiscovery()
    {
        Discovery?.Cancel();
        Discovery = null;
        UpdateStreams();
    }

    partial void OnEditorChanged(CameraEditorViewModel? value) => OnPropertyChanged(nameof(IsLive));

    partial void OnDiscoveryChanged(DiscoveryViewModel? value) => OnPropertyChanged(nameof(IsLive));
}
