using CommunityToolkit.Mvvm.ComponentModel;
using SqlHelper.Core.Model;

namespace SqlHelper.App.ViewModels;

/// <summary>
/// One database in the registry editor. Every field is bound with
/// <c>UpdateSourceTrigger=PropertyChanged</c> from a plain form control, so what you see on
/// screen is always what gets saved — there is no pending "cell edit" that can be lost.
/// </summary>
public partial class RegistryRowViewModel : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string clientName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySubtitle))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string serverName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySubtitle))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string databaseName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlLogin))]
    [NotifyPropertyChangedFor(nameof(PasswordStatus))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private AuthMode authMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string? userId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProduction))]
    [NotifyPropertyChangedFor(nameof(EnvironmentLabel))]
    private ServerEnvironment environment;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySubtitle))]
    private bool enabled;

    [ObservableProperty]
    private bool trustServerCertificate;

    [ObservableProperty]
    private string? notes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordStatus))]
    private bool hasStoredPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordStatus))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string? pendingPassword;

    /// <summary>True until this row has been written to the registry at least once.</summary>
    public bool IsNew { get; private set; }

    public static IReadOnlyList<AuthMode> AuthModes { get; } = Enum.GetValues<AuthMode>();

    public static IReadOnlyList<ServerEnvironment> Environments { get; } = Enum.GetValues<ServerEnvironment>();

    public RegistryRowViewModel()
    {
        Id = Guid.NewGuid();
        clientName = string.Empty;
        serverName = string.Empty;
        databaseName = string.Empty;
        environment = ServerEnvironment.Production;
        enabled = true;
        IsNew = true;
    }

    public RegistryRowViewModel(DatabaseTarget target, bool hasStoredPassword)
    {
        Id = target.Id;
        clientName = target.ClientName;
        serverName = target.ServerName;
        databaseName = target.DatabaseName;
        authMode = target.AuthMode;
        userId = target.UserId;
        environment = target.Environment;
        enabled = target.Enabled;
        trustServerCertificate = target.TrustServerCertificate;
        notes = target.Notes;
        this.hasStoredPassword = hasStoredPassword;
        IsNew = false;
    }

    // --- display helpers for the list on the left ---

    public string DisplayTitle => string.IsNullOrWhiteSpace(ClientName) ? "(unnamed database)" : ClientName;

    public string DisplaySubtitle
    {
        get
        {
            string where = string.IsNullOrWhiteSpace(ServerName) && string.IsNullOrWhiteSpace(DatabaseName)
                ? "not configured yet"
                : $"{ServerName}/{DatabaseName}";
            return Enabled ? where : where + "  ·  disabled";
        }
    }

    public string EnvironmentLabel => Environment.ToString();

    public bool IsProduction => Environment == ServerEnvironment.Production;

    public bool IsSqlLogin => AuthMode == AuthMode.SqlLogin;

    public string PasswordStatus => !IsSqlLogin
        ? "Not needed — Windows authentication uses your signed-in account."
        : PendingPassword is { Length: > 0 }
            ? "New password entered. It is stored encrypted when you save."
            : HasStoredPassword
                ? "A password is stored. Leave blank to keep it."
                : "No password stored yet.";

    // --- validation ---

    public bool IsValid => ValidationMessage is null;

    public string? ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ClientName))
            {
                return "Client name is required.";
            }

            if (string.IsNullOrWhiteSpace(ServerName))
            {
                return "Server is required.";
            }

            if (string.IsNullOrWhiteSpace(DatabaseName))
            {
                return "Database is required.";
            }

            if (AuthMode == AuthMode.SqlLogin && string.IsNullOrWhiteSpace(UserId))
            {
                return "A SQL Server login needs a user name.";
            }

            if (AuthMode == AuthMode.SqlLogin && !HasStoredPassword && string.IsNullOrWhiteSpace(PendingPassword))
            {
                return "A SQL Server login needs a password.";
            }

            return null;
        }
    }

    public void MarkSaved()
    {
        IsNew = false;
        PendingPassword = null;
    }

    public DatabaseTarget ToTarget() => new()
    {
        Id = Id,
        ClientName = ClientName.Trim(),
        ServerName = ServerName.Trim(),
        DatabaseName = DatabaseName.Trim(),
        AuthMode = AuthMode,
        UserId = string.IsNullOrWhiteSpace(UserId) ? null : UserId.Trim(),
        Environment = Environment,
        Enabled = Enabled,
        TrustServerCertificate = TrustServerCertificate,
        Notes = Notes,
    };

    public RegistryRowViewModel CloneForNew() => new()
    {
        ClientName = ClientName + " (copy)",
        ServerName = ServerName,
        DatabaseName = DatabaseName,
        AuthMode = AuthMode,
        UserId = UserId,
        Environment = Environment,
        Enabled = Enabled,
        TrustServerCertificate = TrustServerCertificate,
        Notes = Notes,
    };
}
