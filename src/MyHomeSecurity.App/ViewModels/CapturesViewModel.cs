using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.Core.Cameras;
using MyHomeSecurity.Core.Diagnostics;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>Snapshots and recordings saved on this computer, newest first.</summary>
public sealed partial class CapturesViewModel : ObservableObject
{
    /// <summary>Thumbnails are decoded for this many files at most; the rest show a placeholder.</summary>
    private const int MaxThumbnails = 300;

    private readonly MainWindowViewModel _main;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _loading;

    public CapturesViewModel(MainWindowViewModel main, DialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
    }

    public ObservableCollection<CaptureItemViewModel> Items { get; } = new();

    public string[] Filters { get; } = ["Everything", "Snapshots", "Recordings"];

    [ObservableProperty]
    private int _filterIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = "";

    public bool ShowEmpty => !IsLoading && Items.Count == 0;

    partial void OnFilterIndexChanged(int value) => Refresh();

    public void Refresh()
    {
        if (_main.Data is not { } data)
            return;
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        var settings = data.Settings;
        var filter = FilterIndex;
        IsLoading = true;
        _ = Task.Run(() =>
        {
            var files = Captures.List(settings);
            return filter switch
            {
                1 => files.Where(f => f.Kind == CaptureKind.Snapshot).ToList(),
                2 => files.Where(f => f.Kind == CaptureKind.Recording).ToList(),
                _ => files,
            };
        }, cts.Token).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (cts.IsCancellationRequested)
                return;
            ClearItems();
            if (t.IsFaulted)
            {
                Log.Warn("captures", "Listing failed.", t.Exception?.GetBaseException());
                Summary = "Couldn't list the captures: " + t.Exception?.GetBaseException().Message;
            }
            else if (t.IsCompletedSuccessfully)
            {
                foreach (var f in t.Result)
                    Items.Add(new CaptureItemViewModel(f, this));
                var total = t.Result.Sum(f => f.Size);
                Summary = t.Result.Count == 0 ? "" : $"{t.Result.Count} file(s), {FormatSize(total)}";
                _ = LoadThumbnailsAsync(Items.Take(MaxThumbnails).ToList(), cts.Token);
            }
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }), TaskScheduler.Default);
    }

    private static async Task LoadThumbnailsAsync(List<CaptureItemViewModel> items, CancellationToken ct)
    {
        foreach (var item in items.Where(i => i.IsSnapshot))
        {
            if (ct.IsCancellationRequested)
                return;
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(item.File.Path);
                    return Bitmap.DecodeToWidth(stream, 320);
                }, ct);
                if (ct.IsCancellationRequested)
                {
                    bitmap.Dispose();
                    return;
                }
                item.Thumbnail = bitmap;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("captures", $"No thumbnail for {item.Name}: {ex.Message}");
            }
        }
    }

    private void ClearItems()
    {
        foreach (var item in Items)
            item.Thumbnail?.Dispose();
        Items.Clear();
    }

    public void Unload()
    {
        _loading?.Cancel();
        _loading = null;
        ClearItems();
        Summary = "";
        IsLoading = false;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    [RelayCommand]
    private void RefreshList() => Refresh();

    [RelayCommand]
    private void OpenSnapshotFolder()
    {
        if (_main.Data is { } data)
            OpenFolder(data.Settings.EffectiveSnapshotFolder);
    }

    [RelayCommand]
    private void OpenRecordingFolder()
    {
        if (_main.Data is { } data)
            OpenFolder(data.Settings.EffectiveRecordingFolder);
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            ShellOpen(folder);
        }
        catch (Exception ex)
        {
            _ = _dialogs.AlertAsync("Open folder", $"Couldn't open {folder}: {ex.Message}");
        }
    }

    internal void Open(CaptureItemViewModel item)
    {
        try
        {
            ShellOpen(item.File.Path);
        }
        catch (Exception ex)
        {
            _ = _dialogs.AlertAsync("Open", $"Couldn't open {item.Name}: {ex.Message}");
        }
    }

    internal async Task DeleteAsync(CaptureItemViewModel item)
    {
        if (!await _dialogs.ConfirmAsync("Delete", $"Delete {item.Name}? It can't be recovered from here.", "Delete", danger: true))
            return;
        try
        {
            File.Delete(item.File.Path);
            Items.Remove(item);
            item.Thumbnail?.Dispose();
            OnPropertyChanged(nameof(ShowEmpty));
            _main.Notify($"Deleted {item.Name}");
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Delete", $"Couldn't delete {item.Name}: {ex.Message}");
        }
    }

    /// <summary>Opens a file or folder in the system's default application, locally.</summary>
    internal static void ShellOpen(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", [path])?.Dispose();
        else
            Process.Start("xdg-open", [path])?.Dispose();
    }
}

public sealed partial class CaptureItemViewModel(CaptureFile file, CapturesViewModel owner) : ObservableObject
{
    public CaptureFile File => file;
    public string Name => file.Name;
    public bool IsSnapshot => file.Kind == CaptureKind.Snapshot;
    public bool IsRecording => file.Kind == CaptureKind.Recording;
    public string Details => $"{file.Modified:ddd d MMM yyyy HH:mm} · {CapturesViewModel.FormatSize(file.Size)}";
    public string Placeholder => IsRecording ? "▶ Video" : "Image";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    [RelayCommand]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteAsync(this);

    [RelayCommand]
    private void ShowInFolder()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", ["/select,", file.Path])?.Dispose();
            else
                CapturesViewModel.ShellOpen(Path.GetDirectoryName(file.Path)!);
        }
        catch (Exception ex)
        {
            Log.Warn("captures", "Couldn't show the folder: " + ex.Message);
        }
    }
}
