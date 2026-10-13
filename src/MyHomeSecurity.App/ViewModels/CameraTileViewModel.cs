using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.App.Controls;
using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>One camera in the grid. Its stream exists only while the grid is on screen.</summary>
public sealed partial class CameraTileViewModel : ObservableObject
{
    private readonly CamerasViewModel _owner;

    public CameraTileViewModel(CameraProfile profile, CamerasViewModel owner)
    {
        _profile = profile;
        _owner = owner;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Enabled), nameof(StatusText), nameof(ShowStatus))]
    private CameraProfile _profile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowStatus))]
    private CameraStream? _stream;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ShowStatus))]
    private CameraStreamState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _message = "";

    /// <summary>
    /// The last picture from the grid stream, shown while the stream is stopped or connecting so
    /// that coming back to the tab, or starting the app, isn't a blank screen.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _lastFrame;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isTakingSnapshot;

    public string Name => Profile.DisplayName;
    public bool Enabled => Profile.Enabled;

    public bool ShowStatus => !Enabled || Stream is null || State != CameraStreamState.Playing;

    public string StatusText => !Enabled ? "Turned off"
        : Stream is null ? "Paused"
        : State switch
        {
            CameraStreamState.Connecting => "Connecting…",
            CameraStreamState.Error => string.IsNullOrWhiteSpace(Message) ? "Can't connect, retrying…" : Message,
            CameraStreamState.Playing => "",
            _ => "Stopped",
        };

    partial void OnStreamChanged(CameraStream? oldValue, CameraStream? newValue)
    {
        if (oldValue is not null)
            oldValue.StateChanged -= OnStreamStateChanged;
        if (newValue is not null)
        {
            newValue.StateChanged += OnStreamStateChanged;
            State = newValue.State;
            Message = newValue.StatusMessage;
        }
        else
        {
            State = CameraStreamState.Stopped;
            Message = "";
        }
    }

    /// <summary>
    /// Stops the grid stream. Unless <paramref name="keepPicture"/> is false, its last picture is kept
    /// in <see cref="LastFrame"/> and saved for next time.
    /// </summary>
    public void StopStream(bool keepPicture = true)
    {
        if (Stream is not { } stream)
            return;
        if (keepPicture && VideoView.LatestFrameOf(stream) is { } picture)
        {
            LastFrame = picture;
            _owner.SavePicture(Profile.Id, picture);
        }
        Stream = null;
        stream.Dispose();
    }

    // Raised on the stream's thread.
    private void OnStreamStateChanged()
    {
        var stream = Stream;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(stream, Stream) || stream is null)
                return;
            State = stream.State;
            Message = stream.StatusMessage;
        });
    }

    [RelayCommand]
    private void OpenLive() => _owner.OpenLive(this);

    [RelayCommand]
    private Task SnapshotAsync() => _owner.TakeSnapshotAsync(this);

    [RelayCommand]
    private Task ToggleRecordingAsync() => _owner.ToggleRecordingAsync(this);

    [RelayCommand]
    private void Edit() => _owner.EditCamera(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteCameraAsync(this);

    [RelayCommand]
    private void MoveEarlier() => _owner.MoveCamera(this, -1);

    [RelayCommand]
    private void MoveLater() => _owner.MoveCamera(this, +1);
}
