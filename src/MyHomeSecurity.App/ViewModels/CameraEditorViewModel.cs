using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Discovery;
using MyHomeSecurity.Core.Media;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>Adding or changing a camera, with a connection test, a preview and ONVIF stream lookup.</summary>
public sealed partial class CameraEditorViewModel : ObservableObject
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    private readonly CamerasViewModel _owner;
    private readonly DialogService _dialogs;
    private readonly CameraProfile _original;
    private readonly string? _onvifAddress;
    private CancellationTokenSource? _work;
    private bool _active;
    private bool _previewWanted;

    public CameraEditorViewModel(CamerasViewModel owner, DialogService dialogs, CameraProfile profile, bool isNew, string? onvifAddress)
    {
        _owner = owner;
        _dialogs = dialogs;
        _original = profile;
        _onvifAddress = onvifAddress;
        IsNew = isNew;
        _name = profile.Name;
        _kindIndex = profile.Kind == CameraKind.Mjpeg ? 1 : 0;
        _host = profile.Host;
        _port = profile.Port;
        _username = profile.Username;
        _password = profile.Password;
        _mainPath = profile.MainPath;
        _subPath = profile.SubPath;
        _mjpegPath = profile.MjpegPath;
        _enabled = profile.Enabled;
        Manufacturer = profile.Manufacturer;
        Model = profile.Model;
    }

    public bool IsNew { get; }
    public Guid CameraId => _original.Id;
    public string Title => IsNew ? "Add camera" : $"Edit {_original.DisplayName}";
    public string? Manufacturer { get; private set; }
    public string? Model { get; private set; }

    public string? DeviceText
    {
        get
        {
            var text = string.Join(' ', new[] { Manufacturer, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return text.Length == 0 ? null : text;
        }
    }

    public string[] Kinds { get; } = ["RTSP (most cameras, including Tapo)", "MJPEG over HTTP (older cameras)"];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRtsp), nameof(IsMjpeg))]
    private int _kindIndex;

    [ObservableProperty]
    private string _host;

    [ObservableProperty]
    private decimal? _port;

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _password;

    [ObservableProperty]
    private string _mainPath;

    [ObservableProperty]
    private string _subPath;

    [ObservableProperty]
    private string _mjpegPath;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _showPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isWorking;

    /// <summary>Result of the last test or lookup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty]
    private bool _resultIsError;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private CameraStream? _previewStream;

    [ObservableProperty]
    private string? _previewStatus;

    public bool IsRtsp => KindIndex == 0;
    public bool IsMjpeg => KindIndex == 1;
    public bool IsIdle => !IsWorking;
    public bool HasResult => !string.IsNullOrEmpty(ResultText);
    public char PasswordChar => ShowPassword ? '\0' : '●';

    partial void OnShowPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordChar));

    partial void OnKindIndexChanged(int value)
    {
        // Switch the port to the usual one for the new kind, unless it was set to something else.
        if (value == 1 && Port == 554)
            Port = 80;
        else if (value == 0 && Port == 80)
            Port = 554;
        StopPreview();
    }

    /// <summary>The camera as currently entered, or null (with <see cref="Error"/> set) if it isn't valid.</summary>
    private CameraProfile? Build()
    {
        Error = null;
        var host = (Host ?? "").Trim();
        if (host.Length == 0)
        {
            Error = "Enter the camera's address (for example 192.168.1.50).";
            return null;
        }
        if (host.Contains("://") || host.Contains('/') || host.Contains('@'))
        {
            Error = "Enter just the address (like 192.168.1.50 or camera.local), without rtsp://, a path or a login.";
            return null;
        }
        if (Port is not { } port || port < 1 || port > 65535)
        {
            Error = "The port must be between 1 and 65535.";
            return null;
        }
        var profile = _original.Clone();
        profile.Name = (Name ?? "").Trim();
        profile.Kind = IsMjpeg ? CameraKind.Mjpeg : CameraKind.Rtsp;
        profile.Host = host;
        profile.Port = (int)port;
        profile.Username = (Username ?? "").Trim();
        profile.Password = Password ?? "";
        profile.MainPath = NormalisePath(MainPath, "/stream1");
        profile.SubPath = string.IsNullOrWhiteSpace(SubPath) ? "" : NormalisePath(SubPath, "");
        profile.MjpegPath = NormalisePath(MjpegPath, "/videostream.cgi");
        profile.Enabled = Enabled;
        profile.Manufacturer = Manufacturer;
        profile.Model = Model;
        return profile;
    }

    private static string NormalisePath(string? path, string fallback)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0)
            return fallback;
        return path.StartsWith('/') ? path : "/" + path;
    }

    private CancellationToken BeginWork()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        IsWorking = true;
        ResultText = null;
        ResultIsError = false;
        return _work.Token;
    }

    private void Report(string text, bool isError)
    {
        ResultText = text;
        ResultIsError = isError;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        var profile = Build();
        if (profile is null)
            return;
        var ct = BeginWork();
        try
        {
            if (profile.Kind == CameraKind.Mjpeg)
            {
                var frame = await MjpegSource.GrabFrameAsync(profile, TestTimeout, ct);
                Report($"Connected: received a {frame.Length / 1024} KB picture.", false);
                return;
            }

            var main = await RtspProbe.DescribeAsync(profile.Host, profile.Port, profile.Username, profile.Password, profile.MainPath, TestTimeout, ct);
            var lines = new List<string>();
            if (main.StatusCode == 404)
            {
                // The login was accepted but this camera uses other paths: try the ones the common
                // brands use. Only paths are guessed, so no extra refused logins reach the camera.
                var found = await RtspProbe.FindPathsAsync(profile.Host, profile.Port, profile.Username, profile.Password,
                    TestTimeout, profile.MainPath, ct);
                if (found is not null)
                {
                    lines.Add($"No stream at {profile.MainPath}; this looks like a {found.Brand} camera, so its paths have been filled in.");
                    MainPath = profile.MainPath = found.MainPath;
                    SubPath = profile.SubPath = found.SubPath;
                    main = await RtspProbe.DescribeAsync(profile.Host, profile.Port, profile.Username, profile.Password, profile.MainPath, TestTimeout, ct);
                }
            }
            if (!main.Success)
            {
                Report(Explain(main, "main stream"), true);
                return;
            }
            lines.Add($"Main stream ({profile.MainPath}): OK{(main.HasVideo ? "" : ", but no video was described")}.");
            if (!string.IsNullOrEmpty(profile.SubPath))
            {
                var sub = await RtspProbe.DescribeAsync(profile.Host, profile.Port, profile.Username, profile.Password, profile.SubPath, TestTimeout, ct);
                lines.Add(sub.Success
                    ? $"Grid stream ({profile.SubPath}): OK."
                    : $"Grid stream ({profile.SubPath}): {Explain(sub, "grid stream")} The grid will use the main stream instead, which takes more work to show.");
            }
            if (!string.IsNullOrWhiteSpace(main.Server))
                lines.Add($"Camera software: {main.Server}");
            if (!FfmpegLocator.Default.IsAvailable)
                lines.Add("ffmpeg isn't installed, so the picture can't be shown yet.");
            Report(string.Join('\n', lines), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Report("The camera didn't answer in time. Check the address and that the camera is on.", true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Report(ExplainException(ex), true);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsWorking = false;
        }
    }

    private static string Explain(RtspProbeResult result, string what) => result.StatusCode switch
    {
        401 or 403 => $"The camera refused the login for the {what}. For Tapo cameras, use the camera account set in the Tapo app " +
                      "(Camera settings → Advanced → Camera account), not your TP-Link account.",
        404 when what == "main stream" => "The camera doesn't have a stream at that path, nor at the paths the common brands use. " +
                                          "Check the camera's manual for its RTSP path.",
        404 => $"The camera doesn't have a {what} at that path.",
        0 => result.Message,
        _ => $"The camera answered {result.StatusCode}: {result.Message}",
    };

    private static string ExplainException(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
            "The camera refused the connection on that port. Check the port, and that RTSP / ONVIF is turned on in the camera's settings.",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "That address couldn't be found.",
        SocketException => "Couldn't reach the camera: " + ex.Message,
        TimeoutException => "The camera didn't answer in time. Check the address and that the camera is on.",
        _ => ex.Message,
    };

    /// <summary>Asks the camera over ONVIF for its stream paths (fills in main and grid paths).</summary>
    [RelayCommand]
    private async Task LookupStreamsAsync()
    {
        var profile = Build();
        if (profile is null)
            return;
        var ct = BeginWork();
        try
        {
            // Discovery may have found the exact address; otherwise try Tapo's ONVIF port, then the usual one.
            var addresses = _onvifAddress is not null
                ? new[] { _onvifAddress }
                : new[] { OnvifClient.DeviceAddressFor(profile.Host, 2020), OnvifClient.DeviceAddressFor(profile.Host) };
            StreamPaths? found = null;
            Exception? last = null;
            foreach (var address in addresses)
            {
                try
                {
                    found = await CameraDiscovery.GetStreamPathsAsync(address, profile.Username, profile.Password, ct);
                    if (found is not null)
                        break;
                }
                catch (OnvifException ex) when (ex.NotAuthorized)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    last = ex;
                }
            }
            if (found is null)
            {
                Report("The camera didn't answer ONVIF requests" + (last is null ? "." : $" ({last.Message}).") +
                       " Enter the stream paths from the camera's manual instead.", true);
                return;
            }
            KindIndex = 0;
            MainPath = found.MainPath;
            SubPath = found.SubPath;
            Port = found.Port;
            Manufacturer = found.Manufacturer ?? Manufacturer;
            Model = found.Model ?? Model;
            OnPropertyChanged(nameof(DeviceText));
            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Model))
                Name = Model!;
            Report($"Found the streams: main {found.MainPath}" +
                   (string.IsNullOrEmpty(found.SubPath) ? " (no separate low-resolution stream)." : $", grid {found.SubPath}."), false);
        }
        catch (OnvifException ex) when (ex.NotAuthorized)
        {
            Report("The camera refused the login. For Tapo cameras, use the camera account set in the Tapo app " +
                   "(Camera settings → Advanced → Camera account).", true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Report(ExplainException(ex), true);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsWorking = false;
        }
    }

    [RelayCommand]
    private void TogglePreview()
    {
        if (PreviewStream is not null)
        {
            _previewWanted = false;
            StopPreview();
            return;
        }
        var profile = Build();
        if (profile is null)
            return;
        if (!FfmpegLocator.Default.IsAvailable)
        {
            Report("ffmpeg isn't installed, so the picture can't be shown.", true);
            return;
        }
        _previewWanted = true;
        StartPreview(profile);
    }

    private void StartPreview(CameraProfile profile)
    {
        StopPreview();
        if (!_active)
            return;
        var stream = new CameraStream(profile, StreamOptions.ForGrid(_owner.Main.Settings));
        stream.StateChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(stream, PreviewStream))
                PreviewStatus = stream.State switch
                {
                    CameraStreamState.Connecting => "Connecting…",
                    CameraStreamState.Error => stream.StatusMessage,
                    _ => null,
                };
        });
        PreviewStatus = "Connecting…";
        PreviewStream = stream;
        stream.Start();
    }

    private void StopPreview()
    {
        var stream = PreviewStream;
        PreviewStream = null;
        PreviewStatus = null;
        stream?.Dispose();
    }

    /// <summary>The tab is on screen or not: the preview pauses with it.</summary>
    internal void SetActive(bool active)
    {
        _active = active;
        if (!active)
            StopPreview();
        else if (_previewWanted && PreviewStream is null && Build() is { } profile)
            StartPreview(profile);
    }

    [RelayCommand]
    private void Save()
    {
        var profile = Build();
        if (profile is null)
            return;
        if (_owner.SaveCamera(profile, IsNew))
            _owner.CloseEditor();
    }

    [RelayCommand]
    private void Cancel() => _owner.CloseEditor();

    /// <summary>Stops anything still running (the preview, a test).</summary>
    internal void Close()
    {
        _previewWanted = false;
        _work?.Cancel();
        StopPreview();
        Password = "";
    }
}
