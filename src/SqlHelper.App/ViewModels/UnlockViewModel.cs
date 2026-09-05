using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlHelper.App.Services;
using SqlHelper.Core;
using SqlHelper.Core.Auditing;
using SqlHelper.Core.Credentials;
using SqlHelper.Core.Execution;
using SqlHelper.Core.Orchestration;
using SqlHelper.Core.Registry;

namespace SqlHelper.App.ViewModels;

public partial class UnlockViewModel : ObservableObject
{
    private readonly AppPaths _paths;

    public bool IsFirstRun { get; }

    [ObservableProperty]
    private string errorMessage = string.Empty;

    public AppSession? Result { get; private set; }

    public UnlockViewModel(AppPaths paths)
    {
        _paths = paths;
        IsFirstRun = !File.Exists(paths.RegistryFile);
    }

    public bool TryUnlock(string passphrase)
    {
        try
        {
            byte[] entropy = MasterKey.DeriveEntropy(passphrase);
            var protector = new DpapiSecretProtector(entropy);
            var store = new RegistryStore(_paths.RegistryFile, protector);
            ConnectionRegistry registry = store.Load();

            if (IsFirstRun)
            {
                // Lock in the passphrase choice immediately so a blank vs non-blank choice isn't lost.
                store.Save(registry);
            }

            var audit = new AuditLog(_paths.AuditDirectory);
            var connections = new RegistryConnectionFactory(registry);
            var engine = new DeploymentEngine(connections, audit);

            Result = new AppSession
            {
                Paths = _paths,
                Protector = protector,
                Store = store,
                Registry = registry,
                Audit = audit,
                Connections = connections,
                Engine = engine,
            };
            ErrorMessage = string.Empty;
            return true;
        }
        catch (InvalidDataException)
        {
            ErrorMessage = "That passphrase didn't open the registry — or it belongs to a different Windows account on this machine.";
            return false;
        }
    }
}
