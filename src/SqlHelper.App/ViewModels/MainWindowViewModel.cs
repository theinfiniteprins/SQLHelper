using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlHelper.App.Services;
using SqlHelper.Core;

namespace SqlHelper.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppSession _session;

    public RegistryViewModel RegistryVm { get; }

    public SelectQueryViewModel SelectVm { get; }

    public NonQueryViewModel NonQueryVm { get; }

    public DeployObjectViewModel DeployVm { get; }

    public PatchViewModel PatchVm { get; }

    [ObservableProperty]
    private object currentView;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegistryActive))]
    [NotifyPropertyChangedFor(nameof(IsSelectActive))]
    [NotifyPropertyChangedFor(nameof(IsNonQueryActive))]
    [NotifyPropertyChangedFor(nameof(IsDeployActive))]
    [NotifyPropertyChangedFor(nameof(IsPatchActive))]
    private string activeTab = "Registry";

    public bool IsRegistryActive => ActiveTab == "Registry";

    public bool IsSelectActive => ActiveTab == "Select";

    public bool IsNonQueryActive => ActiveTab == "NonQuery";

    public bool IsDeployActive => ActiveTab == "Deploy";

    public bool IsPatchActive => ActiveTab == "Patch";

    [ObservableProperty]
    private string registrySummary = string.Empty;

    public string OperatorLine => $"{AppInfo.Operator} on {AppInfo.Machine}";

    public string VersionLine => $"SqlHelper {AppInfo.Version}";

    public MainWindowViewModel(AppSession session)
    {
        _session = session;
        RegistryVm = new RegistryViewModel(session, OnRegistryChanged);
        SelectVm = new SelectQueryViewModel(session);
        NonQueryVm = new NonQueryViewModel(session);
        DeployVm = new DeployObjectViewModel(session);
        PatchVm = new PatchViewModel(session);
        currentView = RegistryVm;
        UpdateRegistrySummary();
    }

    private void OnRegistryChanged()
    {
        SelectVm.Picker.Refresh();
        NonQueryVm.Picker.Refresh();
        DeployVm.Picker.Refresh();
        PatchVm.Picker.Refresh();
        UpdateRegistrySummary();
    }

    private void UpdateRegistrySummary()
    {
        int total = _session.Registry.Targets.Count;
        int enabled = _session.Registry.Targets.Count(t => t.Enabled);
        RegistrySummary = total switch
        {
            0 => "No databases registered yet",
            1 => "1 database registered",
            _ when enabled == total => $"{total} databases registered",
            _ => $"{total} databases registered ({enabled} enabled)",
        };
    }

    [RelayCommand]
    private void ShowRegistry()
    {
        CurrentView = RegistryVm;
        ActiveTab = "Registry";
    }

    [RelayCommand]
    private void ShowSelect()
    {
        SelectVm.Picker.Refresh();
        CurrentView = SelectVm;
        ActiveTab = "Select";
    }

    [RelayCommand]
    private void ShowNonQuery()
    {
        NonQueryVm.Picker.Refresh();
        CurrentView = NonQueryVm;
        ActiveTab = "NonQuery";
    }

    [RelayCommand]
    private void ShowDeploy()
    {
        DeployVm.Picker.Refresh();
        CurrentView = DeployVm;
        ActiveTab = "Deploy";
    }

    [RelayCommand]
    private void ShowPatch()
    {
        PatchVm.Picker.Refresh();
        CurrentView = PatchVm;
        ActiveTab = "Patch";
    }

    public void Dispose()
    {
        SelectVm.Dispose();
        NonQueryVm.Dispose();
        _session.Audit.Dispose();
        GC.SuppressFinalize(this);
    }
}
