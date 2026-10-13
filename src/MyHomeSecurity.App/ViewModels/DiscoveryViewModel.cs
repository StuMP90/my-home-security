using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.Discovery;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>Looks for cameras on the local network so they can be added in a click or two.</summary>
public sealed partial class DiscoveryViewModel : ObservableObject
{
    private readonly CamerasViewModel _owner;
    private readonly HashSet<string> _existingHosts;
    private CancellationTokenSource? _search;

    public DiscoveryViewModel(CamerasViewModel owner, IEnumerable<string> existingHosts)
    {
        _owner = owner;
        _existingHosts = new HashSet<string>(existingHosts, StringComparer.OrdinalIgnoreCase);
    }

    public ObservableCollection<DiscoveredCameraViewModel> Results { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(ShowNothingFound))]
    private bool _isSearching;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNothingFound))]
    private bool _hasSearched;

    [ObservableProperty]
    private string? _error;

    public bool IsIdle => !IsSearching;
    public bool ShowNothingFound => HasSearched && !IsSearching && Results.Count == 0 && Error is null;

    public async Task SearchAsync()
    {
        _search?.Cancel();
        var cts = new CancellationTokenSource();
        _search = cts;
        Results.Clear();
        Error = null;
        Progress = 0;
        IsSearching = true;
        try
        {
            var progress = new Progress<double>(p => Progress = Math.Clamp(p * 100, 0, 100));
            var found = await CameraDiscovery.FindAsync(progress, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            foreach (var camera in found.OrderBy(c => SortKey(c.Host)))
                Results.Add(new DiscoveredCameraViewModel(camera, _existingHosts.Contains(camera.Host), this));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("discovery", "Search failed: " + ex.Message);
            Error = "The search failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_search, cts))
            {
                IsSearching = false;
                HasSearched = true;
                Progress = 100;
                OnPropertyChanged(nameof(ShowNothingFound));
            }
        }
    }

    /// <summary>Sorts IPv4 addresses numerically, names after them.</summary>
    private static string SortKey(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.GetAddressBytes() is { Length: 4 } b
            ? $"0{b[0]:D3}{b[1]:D3}{b[2]:D3}{b[3]:D3}"
            : "1" + host;

    [RelayCommand]
    private Task SearchAgainAsync() => SearchAsync();

    [RelayCommand]
    public void Cancel()
    {
        _search?.Cancel();
        IsSearching = false;
    }

    [RelayCommand]
    private void Close() => _owner.CloseDiscovery();

    [RelayCommand]
    private void AddManually() => _owner.OpenEditor(new Core.Models.CameraProfile(), isNew: true, onvifAddress: null);

    internal void Add(DiscoveredCamera camera) =>
        _owner.OpenEditor(CameraDiscovery.ToProfile(camera), isNew: true, onvifAddress: camera.OnvifAddress);
}

public sealed partial class DiscoveredCameraViewModel(DiscoveredCamera camera, bool alreadyAdded, DiscoveryViewModel owner)
{
    public string Host => camera.Host;
    public string Description => camera.Description;
    public string Source => "Found by " + camera.Source;
    public bool AlreadyAdded => alreadyAdded;
    public string AddText => alreadyAdded ? "Add again" : "Add…";

    [RelayCommand]
    private void Add() => owner.Add(camera);
}
