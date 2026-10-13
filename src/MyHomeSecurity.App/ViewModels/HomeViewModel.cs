using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.App.Services;
using MyHomeSecurity.Core.Diagnostics;
using MyHomeSecurity.Core.HomeAssistant;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>
/// Home Assistant tiles: values to watch and switches to flip. Polls only while the tab is on
/// screen; nothing is fetched while the app is locked, minimised or showing another tab.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    private readonly DialogService _dialogs;
    private readonly HaSwitchGuard _guard = new();
    private readonly HaSwitchGuard _boostGuard = new(HaBoost.PendingFor);

    /// <summary>Each running-total sensor's last step, by entity, and the state change it was worked out for.</summary>
    private readonly ConcurrentDictionary<string, (DateTimeOffset Changed, double? Step)> _steps = new(StringComparer.Ordinal);
    private VaultData? _data;
    private HaClient? _client;
    private CancellationTokenSource? _polling;
    private bool _active;

    public HomeViewModel(MainWindowViewModel main, DialogService dialogs)
    {
        _main = main;
        _dialogs = dialogs;
        Tiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowNoTiles));
    }

    public ObservableCollection<HaTileViewModel> Tiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoTiles), nameof(ShowTiles))]
    private bool _isConfigured;

    [ObservableProperty]
    private bool _isInsecure;

    [ObservableProperty]
    private string? _connectionError;

    [ObservableProperty]
    private DateTime? _lastUpdated;

    [ObservableProperty]
    private bool _isArranging;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoTiles), nameof(ShowTiles))]
    private HaTileEditorViewModel? _editor;

    public bool ShowTiles => IsConfigured && Editor is null;
    public bool ShowNoTiles => IsConfigured && Editor is null && Tiles.Count == 0;

    public string InsecureText =>
        "The Home Assistant address uses http:// to somewhere outside your home network, so the access token " +
        "and everything shown here cross the internet unencrypted. Use an https:// address.";

    internal HaClient? Client => _client;
    internal HaSwitchGuard Guard => _guard;
    internal DialogService Dialogs => _dialogs;

    /// <summary>Holds a boost's or a cancel's optimistic state until Hive's cloud reports it.</summary>
    internal HaSwitchGuard BoostGuard => _boostGuard;

    /// <summary>The last boost asked for on <paramref name="entityId"/>, if any.</summary>
    internal HaBoostRecord? GetBoost(string entityId) =>
        _data?.HomeAssistant.Boosts.GetValueOrDefault(entityId);

    internal void SaveBoost(string entityId, HaBoostRecord record)
    {
        if (_data is null)
            return;
        _data.HomeAssistant.Boosts[entityId] = record;
        _main.SaveVault();
    }

    public void Load(VaultData data)
    {
        _data = data;
        Reload();
    }

    /// <summary>Rebuilds the client and tiles from the settings (after they change).</summary>
    public void Reload()
    {
        StopPolling();
        _client?.Dispose();
        _client = null;
        Tiles.Clear();
        ConnectionError = null;
        LastUpdated = null;
        if (_data is null)
            return;
        var ha = _data.HomeAssistant;
        IsConfigured = ha.IsConfigured;
        IsInsecure = HaClient.IsInsecure(ha.ServerUrl);
        if (!IsConfigured)
            return;
        try
        {
            _client = new HaClient(ha.ServerUrl, ha.Token);
        }
        catch (Exception ex)
        {
            ConnectionError = ex.Message;
            return;
        }
        foreach (var tile in ha.Tiles)
            Tiles.Add(new HaTileViewModel(tile, this));
        if (_active)
            StartPolling();
    }

    public void Unload()
    {
        StopPolling();
        Editor?.Close();
        Editor = null;
        _client?.Dispose();
        _client = null;
        Tiles.Clear();
        _data = null;
        IsConfigured = false;
        ConnectionError = null;
        IsArranging = false;
    }

    public void SetActive(bool active)
    {
        if (_active == active)
            return;
        _active = active;
        if (active)
            StartPolling();
        else
            StopPolling();
    }

    private void StartPolling()
    {
        if (_client is null || _polling is not null || _data is null)
            return;
        _polling = new CancellationTokenSource();
        _ = PollAsync(_client, TimeSpan.FromSeconds(Math.Max(HomeAssistantSettings.MinimumPollSeconds, _data.HomeAssistant.PollSeconds)), _polling.Token);
    }

    private void StopPolling()
    {
        _polling?.Cancel();
        _polling = null;
    }

    private async Task PollAsync(HaClient client, TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync(client, ct);
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// For a sensor Home Assistant wrongly adds up (see <see cref="HaRunningTotal"/>), the latest
    /// reading: the step between its last two states. Only asked for when the state has changed.
    /// </summary>
    private async Task<double?> GetStepAsync(HaClient client, HaEntityState state, CancellationToken ct)
    {
        if (state.LastChanged is not { } changed || !Tiles.Any(t => t.Config.EntityId == state.EntityId && HaRunningTotal.Applies(state, t.Config.Field)))
            return null;
        if (_steps.TryGetValue(state.EntityId, out var known) && known.Changed == changed)
            return known.Step;
        double? step;
        try
        {
            step = await client.GetLastStepAsync(state.EntityId, changed, ct);
        }
        catch (HaException)
        {
            step = null;
        }
        _steps[state.EntityId] = (changed, step);
        return step;
    }

    /// <summary>Fetches every tile's entity once, in parallel.</summary>
    private async Task RefreshAsync(HaClient client, CancellationToken ct)
    {
        var tiles = Tiles.ToList();
        if (tiles.Count == 0)
            return;
        var ids = tiles.Select(t => t.Config.EntityId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var results = await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                var state = await client.GetStateAsync(id, ct);
                var withStep = state with { Step = await GetStepAsync(client, state, ct) };
                return (id, State: (HaEntityState?)withStep, Error: (string?)null);
            }
            catch (OperationCanceledException)
            {
                return (id, State: (HaEntityState?)null, Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (id, State: (HaEntityState?)null, Error: ex.Message);
            }
        }));
        if (ct.IsCancellationRequested)
            return;
        var byId = results.ToDictionary(r => r.id, StringComparer.Ordinal);
        // Every request failing the same way is a connection problem, not a tile problem.
        var allFailed = results.Length > 0 && results.All(r => r.Error is not null);
        ConnectionError = allFailed ? results[0].Error : null;
        foreach (var tile in tiles)
        {
            if (byId.TryGetValue(tile.Config.EntityId, out var r))
            {
                if (r.State is not null)
                    tile.Update(r.State);
                else if (r.Error is not null)
                    tile.SetError(allFailed ? "No connection" : r.Error);
            }
            else
            {
                tile.SetError("No entity set");
            }
        }
        if (!allFailed)
            LastUpdated = DateTime.Now;
    }

    /// <summary>Refreshes soon after a change so the tile shows Home Assistant's view of it.</summary>
    internal void RefreshSoon()
    {
        if (_client is null)
            return;
        var client = _client;
        var ct = _polling?.Token ?? CancellationToken.None;
        _ = Task.Delay(800, ct).ContinueWith(_ => Dispatcher.UIThread.Post(() => _ = RefreshAsync(client, ct)),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    internal void ReportError(string message) => _main.Notify(message);

    [RelayCommand]
    private void OpenSettings() => _main.SelectedTabIndex = (int)MainTab.Settings;

    [RelayCommand]
    private void ToggleArranging() => IsArranging = !IsArranging;

    // ---- Editing tiles ----

    [RelayCommand]
    private void AddTile()
    {
        if (_client is null)
            return;
        OpenEditor(new HaTileConfig(), isNew: true);
    }

    internal void EditTile(HaTileViewModel tile) => OpenEditor(tile.Config.Clone(), isNew: false);

    private void OpenEditor(HaTileConfig config, bool isNew)
    {
        if (_client is null)
            return;
        Editor?.Close();
        Editor = new HaTileEditorViewModel(this, _client, config, isNew);
        _ = Editor.LoadEntitiesAsync();
    }

    internal void CloseEditor()
    {
        Editor?.Close();
        Editor = null;
    }

    internal bool SaveTile(HaTileConfig config, bool isNew)
    {
        if (_data is null)
            return false;
        var list = _data.HomeAssistant.Tiles;
        if (isNew)
        {
            list.Add(config);
            Tiles.Add(new HaTileViewModel(config, this));
        }
        else
        {
            var index = list.FindIndex(t => t.Id == config.Id);
            if (index < 0)
                return false;
            list[index] = config;
            var vmIndex = Tiles.ToList().FindIndex(t => t.Config.Id == config.Id);
            if (vmIndex >= 0)
                Tiles[vmIndex] = new HaTileViewModel(config, this);
        }
        if (!_main.SaveVault())
            return false;
        if (_client is not null)
            _ = RefreshAsync(_client, _polling?.Token ?? CancellationToken.None);
        return true;
    }

    internal async Task RemoveTileAsync(HaTileViewModel tile)
    {
        if (_data is null)
            return;
        if (!await _dialogs.ConfirmAsync("Remove tile", $"Remove the tile \"{tile.Name}\"? This doesn't change anything in Home Assistant.",
                "Remove", danger: true))
            return;
        _data.HomeAssistant.Tiles.RemoveAll(t => t.Id == tile.Config.Id);
        Tiles.Remove(tile);
        _main.SaveVault();
    }

    internal void MoveTile(HaTileViewModel tile, int step)
    {
        if (_data is null)
            return;
        var from = Tiles.IndexOf(tile);
        var to = from + step;
        if (from < 0 || to < 0 || to >= Tiles.Count)
            return;
        Tiles.Move(from, to);
        var list = _data.HomeAssistant.Tiles;
        var config = list.FirstOrDefault(t => t.Id == tile.Config.Id);
        if (config is not null)
        {
            list.Remove(config);
            list.Insert(Math.Min(to, list.Count), config);
        }
        _main.SaveVault();
    }

    partial void OnEditorChanged(HaTileEditorViewModel? value)
    {
        // The tiles pause while editing; the picker has its own fetch.
        if (value is null && _active)
            StartPolling();
        else if (value is not null)
            StopPolling();
    }
}

/// <summary>One Home Assistant tile: a value, or a switch (with brightness for dimmable lights).</summary>
public sealed partial class HaTileViewModel : ObservableObject
{
    private readonly HomeViewModel _owner;
    private CancellationTokenSource? _brightnessDebounce;
    private bool _updatingBrightness;

    public HaTileViewModel(HaTileConfig config, HomeViewModel owner)
    {
        Config = config;
        _owner = owner;
        _name = config.DisplayName;
    }

    public HaTileConfig Config { get; }
    public bool IsSwitch => Config.IsSwitch;
    public bool IsValue => !Config.IsSwitch;
    public bool IsLarge => Config.Size == HaTileSize.Large;
    public double TileWidth => IsLarge ? 336 : 160;
    public double ValueFontSize => IsLarge ? 30 : 22;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _value = "…";

    [ObservableProperty]
    private string _units = "";

    /// <summary>A line under the value, e.g. a thermostat's mode and set temperature.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detail = "";

    public bool HasDetail => Detail.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwitchText))]
    private bool _isOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    private bool _isUnavailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    [ObservableProperty]
    private bool _supportsBrightness;

    [ObservableProperty]
    private double _brightness;

    // ---- Boost (thermostats with a boost preset) ----

    private HaEntityState? _state;

    /// <summary>Shows the Boost button: a thermostat that can boost and isn't boosting.</summary>
    [ObservableProperty]
    private bool _canBoost;

    /// <summary>Shows the boost chip instead of the button.</summary>
    [ObservableProperty]
    private bool _isBoosting;

    /// <summary>"Starting…" until the thermostat confirms, then "Until 20:34 ✕" (or "Boosting ✕" if the end isn't known).</summary>
    [ObservableProperty]
    private string _boostText = "";

    [ObservableProperty]
    private bool _canCancelBoost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSwitch))]
    private bool _isBusy;

    public bool HasError => Error is not null;
    public bool CanSwitch => !IsUnavailable && !IsBusy;
    public string SwitchText => IsOn ? "On" : "Off";

    internal void Update(HaEntityState state)
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(Config.Name))
            Name = state.FriendlyName;
        IsUnavailable = state.IsUnavailable;
        if (IsSwitch)
        {
            IsOn = _owner.Guard.Resolve(Config.EntityId, state.IsOn);
            Value = state.IsUnavailable ? "Unavailable" : SwitchText;
        }
        else
        {
            var value = state.GetTileValue(Config.Field);
            Value = value.Length == 0 ? "—" : value;
        }
        Units = !string.IsNullOrWhiteSpace(Config.Units) ? Config.Units : state.GetTileUnits(Config.Field);
        Detail = IsSwitch ? "" : state.GetTileDetail(Config.Field);
        SupportsBrightness = IsSwitch && state.SupportsBrightness;
        if (SupportsBrightness && _brightnessDebounce is null && state.BrightnessPercent is { } pct)
        {
            _updatingBrightness = true;
            Brightness = IsOn ? pct : 0;
            _updatingBrightness = false;
        }
        _state = state;
        ShowBoost();
    }

    private void ShowBoost()
    {
        if (_state is not { } state || IsSwitch)
        {
            CanBoost = IsBoosting = CanCancelBoost = false;
            return;
        }
        var boosting = _owner.BoostGuard.Resolve(Config.EntityId, state.IsBoosting);
        CanBoost = !boosting && state.SupportsBoost;
        IsBoosting = boosting;
        if (!boosting)
        {
            CanCancelBoost = false;
            return;
        }
        var now = DateTimeOffset.Now;
        var record = _owner.GetBoost(Config.EntityId);
        // Still waiting for Home Assistant to report the boost asked for: not cancellable yet.
        var confirmed = state.IsBoosting && HaBoost.IsConfirmed(state, record, now);
        CanCancelBoost = confirmed;
        BoostText = !confirmed ? "Starting…"
            : record?.EndsAt is { } ends && ends > now ? $"Until {ends.ToLocalTime():HH:mm} ✕"
            : "Boosting ✕";
    }

    internal void SetError(string message)
    {
        Error = message;
        Value = "—";
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_owner.Client is not { } client || !CanSwitch)
            return;
        var on = !IsOn;
        IsOn = on;
        _owner.Guard.Set(Config.EntityId, on);
        IsBusy = true;
        try
        {
            await client.SetOnAsync(Config.EntityId, on);
            _owner.RefreshSoon();
        }
        catch (Exception ex)
        {
            _owner.Guard.Clear(Config.EntityId);
            IsOn = !on;
            Log.Warn("home", $"Switching {Config.EntityId} failed: {ex.Message}");
            _owner.ReportError($"Couldn't switch {Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            Value = SwitchText;
        }
    }

    partial void OnBrightnessChanged(double value)
    {
        if (_updatingBrightness || _owner.Client is not { } client)
            return;
        // Sliders fire continuously while dragged; send only once it settles.
        _brightnessDebounce?.Cancel();
        var cts = _brightnessDebounce = new CancellationTokenSource();
        var percent = (int)Math.Round(value);
        _ = Task.Delay(350, cts.Token).ContinueWith(async _ =>
        {
            try
            {
                if (percent <= 0)
                    await client.SetOnAsync(Config.EntityId, false);
                else
                    await client.SetBrightnessAsync(Config.EntityId, percent);
                _owner.Guard.Set(Config.EntityId, percent > 0);
                Dispatcher.UIThread.Post(() =>
                {
                    IsOn = percent > 0;
                    Value = SwitchText;
                    _owner.RefreshSoon();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _owner.ReportError($"Couldn't dim {Name}: {ex.Message}"));
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_brightnessDebounce, cts))
                        _brightnessDebounce = null;
                });
            }
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    [RelayCommand]
    private async Task BoostAsync()
    {
        if (_owner.Client is not { } client || _state is not { } state)
            return;
        // Start from the last boost asked for on this thermostat.
        var last = _owner.GetBoost(Config.EntityId);
        var status = string.Join(" · ", new[]
        {
            state.Number("current_temperature") is { } current ? $"Now {current:0.#}°" : "",
            state.Number("temperature") is { } target ? $"Target {target:0.#}°" : "",
        }.Where(p => p.Length > 0));
        var minutes = last is not null && HaBoost.MinuteChoices.Contains(last.Minutes) ? last.Minutes : HaBoost.DefaultMinutes;
        var chosen = await _owner.Dialogs.BoostAsync(Name, status, last?.Temperature ?? HaBoost.DefaultTemperature(state),
            HaBoost.Step, t => HaBoost.Clamp(state, t), [.. HaBoost.MinuteChoices.Select(m => (m, HaBoost.MinutesText(m)))], minutes);
        if (chosen is not { } boost || _owner.Client != client)
            return;

        _owner.BoostGuard.Set(Config.EntityId, true);
        ShowBoost();
        try
        {
            var started = DateTimeOffset.Now;
            var timed = await client.BoostAsync(Config.EntityId, boost.Temperature, boost.Minutes);
            _owner.SaveBoost(Config.EntityId, new HaBoostRecord(boost.Temperature, boost.Minutes,
                timed ? started.AddMinutes(boost.Minutes) : null));
            ShowBoost();
            _owner.RefreshSoon();
        }
        catch (Exception ex)
        {
            _owner.BoostGuard.Clear(Config.EntityId);
            ShowBoost();
            Log.Warn("home", $"Boosting {Config.EntityId} failed: {ex.Message}");
            _owner.ReportError($"Couldn't boost {Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CancelBoostAsync()
    {
        if (_owner.Client is not { } client)
            return;
        if (_owner.GetBoost(Config.EntityId) is { } last)
            _owner.SaveBoost(Config.EntityId, last with { EndsAt = null });
        _owner.BoostGuard.Set(Config.EntityId, false);
        ShowBoost();
        try
        {
            await client.CancelBoostAsync(Config.EntityId);
            _owner.RefreshSoon();
        }
        catch (Exception ex)
        {
            _owner.BoostGuard.Clear(Config.EntityId);
            ShowBoost();
            Log.Warn("home", $"Cancelling the boost on {Config.EntityId} failed: {ex.Message}");
            _owner.ReportError($"Couldn't cancel the boost on {Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Edit() => _owner.EditTile(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveTileAsync(this);

    [RelayCommand]
    private void MoveEarlier() => _owner.MoveTile(this, -1);

    [RelayCommand]
    private void MoveLater() => _owner.MoveTile(this, +1);
}
