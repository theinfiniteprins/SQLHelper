namespace SqlHelper.Core.Model;

/// <summary>How the tool authenticates to a target server.</summary>
public enum AuthMode
{
    /// <summary>Integrated Windows / Active Directory. No password is stored.</summary>
    WindowsIntegrated = 0,

    /// <summary>SQL Server login. The password is stored encrypted with <see cref="Credentials.ISecretProtector"/>.</summary>
    SqlLogin = 1,
}

/// <summary>Operational classification of a target. Gates confirmations and colour-codes the UI.</summary>
public enum ServerEnvironment
{
    Development = 0,
    Test = 1,
    Staging = 2,
    Production = 3,
}

/// <summary>Kind of schema-bound programmable object the deployment engine understands.</summary>
public enum ProgrammableObjectKind
{
    StoredProcedure = 0,
    ScalarFunction = 1,
    TableValuedFunction = 2,
    View = 3,
    Trigger = 4,
}

/// <summary>Outcome of running one operation against one target.</summary>
public enum TargetStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    Cancelled = 5,
}
