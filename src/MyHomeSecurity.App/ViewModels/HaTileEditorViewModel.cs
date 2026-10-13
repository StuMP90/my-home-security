using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyHomeSecurity.Core.HomeAssistant;
using MyHomeSecurity.Core.Models;

namespace MyHomeSecurity.App.ViewModels;

/// <summary>Adds or edits one Home Assistant tile, with a searchable list of the server's entities.</summary>
public sealed partial class HaTileEditorViewModel : ObservableObject
{
    private readonly HomeViewModel _owner;
    private readonly HaClient _client;
    private readonly HaTileConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private List<HaEntityState> _all = new();

    public HaTileEditorViewModel(HomeViewModel owner, HaClient client, HaTileConfig config, bool isNew)
    {
        _owner = owner;
        _client = client;
        _config = config;
        IsNew = isNew;
        _entityId = config.EntityId;
        _field = string.IsNullOrWhiteSpace(config.Field) ? "state" : config.Field;
        _name = config.Name;
        _units = config.Units;
        _kindIndex = (int)config.Kind;
        _isLarge = config.Size == HaTileSize.Large;
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Add a tile" : "Edit tile";
    public string[] Kinds { get; } = ["Automatic", "Show the value", "On/off switch"];

    public ObservableCollection<HaEntityChoice> Entities { get; } = new();

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(Attributes))]
    private string _entityId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private string _field;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _units;

    [ObservableProperty]
    private int _kindIndex;

    [ObservableProperty]
    private bool _isLarge;

    [ObservableProperty]
    private HaEntityChoice? _selectedEntity;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadError;

    [ObservableProperty]
    private string? _error;

    /// <summary>The selected entity's attribute names, to help fill in the field.</summary>
    public string Attributes =>
        Find(EntityId) is { } s && s.Attributes.Count > 0
            ? "Attributes: " + string.Join(", ", s.Attributes.Select(a => a.Key))
            : "";

    /// <summary>What the tile would show now.</summary>
    public string Preview =>
        Find(EntityId) is { } s
            ? "Shows now: " + (s.GetTileValue(Field) is { Length: > 0 } v ? v : "(nothing)") + (s.GetTileUnits(Field) is { Length: > 0 } u ? " " + u : "")
              + (s.GetTileDetail(Field) is { Length: > 0 } d ? " (" + d + ")" : "")
            : "";

    private HaEntityState? Find(string id) => _all.FirstOrDefault(e => e.EntityId == id);

    public async Task LoadEntitiesAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            _all = (await _client.GetStatesAsync(_cts.Token)).OrderBy(e => e.EntityId, StringComparer.Ordinal).ToList();
            ApplyFilter();
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(Attributes));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoadError = "Couldn't fetch the entity list: " + ex.Message + " You can still type an entity ID.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Entities.Clear();
        foreach (var e in _all)
        {
            var text = e.EntityId + " " + e.FriendlyName;
            if (words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
                Entities.Add(new HaEntityChoice(e));
            if (Entities.Count >= 300)
                break;
        }
    }

    partial void OnSelectedEntityChanged(HaEntityChoice? value)
    {
        if (value is null)
            return;
        EntityId = value.EntityId;
        if (string.IsNullOrWhiteSpace(Name))
            Name = value.FriendlyName;
    }

    [RelayCommand]
    private void Save()
    {
        Error = null;
        var id = EntityId.Trim();
        if (id.Length == 0 || !id.Contains('.'))
        {
            Error = "Choose an entity, or type its ID (such as sensor.living_room_temperature).";
            return;
        }
        var config = _config.Clone();
        config.EntityId = id;
        config.Field = string.IsNullOrWhiteSpace(Field) ? "state" : Field.Trim();
        config.Name = Name.Trim();
        config.Units = Units.Trim();
        config.Kind = (HaTileKind)Math.Clamp(KindIndex, 0, 2);
        config.Size = IsLarge ? HaTileSize.Large : HaTileSize.Small;
        if (_owner.SaveTile(config, IsNew))
            _owner.CloseEditor();
        else
            Error = "The tile couldn't be saved.";
    }

    [RelayCommand]
    private void Cancel() => _owner.CloseEditor();

    internal void Close() => _cts.Cancel();
}

public sealed class HaEntityChoice(HaEntityState state)
{
    public string EntityId => state.EntityId;
    public string FriendlyName => state.FriendlyName;
    public string Summary => state.State + (state.UnitOfMeasurement is { } u ? " " + u : "");
}
