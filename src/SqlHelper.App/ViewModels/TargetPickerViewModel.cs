using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlHelper.App.Services;
using SqlHelper.Core.Model;

namespace SqlHelper.App.ViewModels;

public partial class TargetCheckItem(DatabaseTarget target) : ObservableObject
{
    public DatabaseTarget Target { get; } = target;

    [ObservableProperty]
    private bool isSelected;

    public string Display => Target.QualifiedName;

    public string EnvironmentLabel => Target.Environment.ToString();

    public bool IsProduction => Target.Environment == ServerEnvironment.Production;
}

/// <summary>Reusable "which databases" picker: search, named-group presets, select all/none, shared by every fan-out screen.</summary>
public partial class TargetPickerViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly bool _selectAllByDefault;
    private bool _operatorHasChosen;

    public ObservableCollection<TargetCheckItem> Items { get; } = [];

    public ObservableCollection<string> GroupNames { get; } = [];

    public ICollectionView View { get; }

    [ObservableProperty]
    private string filterText = string.Empty;

    [ObservableProperty]
    private string? selectedGroup;

    public IReadOnlyList<DatabaseTarget> SelectedTargets => [.. Items.Where(i => i.IsSelected).Select(i => i.Target)];

    public int SelectedCount => Items.Count(i => i.IsSelected);

    /// <param name="selectAllByDefault">
    /// True for read-only screens, where "run this on everything" is the obvious intent. Write
    /// screens leave the selection empty on purpose, so nothing destructive is ever pre-armed.
    /// </param>
    public TargetPickerViewModel(AppSession session, bool selectAllByDefault = false)
    {
        _session = session;
        _selectAllByDefault = selectAllByDefault;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = FilterItem;
        Refresh();
    }

    public void Refresh()
    {
        var selectedIds = Items.Where(i => i.IsSelected).Select(i => i.Target.Id).ToHashSet();

        Items.Clear();
        foreach (DatabaseTarget target in _session.Registry.Targets.Where(t => t.Enabled).OrderBy(t => t.ClientName, StringComparer.OrdinalIgnoreCase))
        {
            bool selected = _selectAllByDefault && !_operatorHasChosen
                ? true
                : selectedIds.Contains(target.Id);
            var item = new TargetCheckItem(target) { IsSelected = selected };
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        GroupNames.Clear();
        foreach (string name in _session.Registry.Groups.Select(g => g.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            GroupNames.Add(name);
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    partial void OnFilterTextChanged(string value) => View.Refresh();

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetCheckItem.IsSelected))
        {
            _operatorHasChosen = true;
            OnPropertyChanged(nameof(SelectedCount));
        }
    }

    private bool FilterItem(object obj)
    {
        if (obj is not TargetCheckItem item)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        return item.Target.ClientName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || item.Target.ServerName.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || item.Target.DatabaseName.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (object obj in View)
        {
            if (obj is TargetCheckItem item)
            {
                item.IsSelected = true;
            }
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (TargetCheckItem item in Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void ApplyGroup()
    {
        if (SelectedGroup is null)
        {
            return;
        }

        HashSet<Guid> ids = [.. _session.Registry.ResolveGroup(SelectedGroup).Select(t => t.Id)];
        foreach (TargetCheckItem item in Items)
        {
            item.IsSelected = ids.Contains(item.Target.Id);
        }
    }
}
