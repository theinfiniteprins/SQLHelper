using SqlHelper.Core.Model;

namespace SqlHelper.Core.Scripting;

/// <summary>A programmable object captured from a database, exactly as stored.</summary>
public sealed record ProgrammableObject(
    ObjectName Name,
    ProgrammableObjectKind Kind,
    string Definition,
    bool UsesAnsiNulls,
    bool UsesQuotedIdentifier,
    int ObjectId,
    DateTime CreatedServerTime,
    DateTime ModifiedServerTime,
    bool IsEncrypted)
{
    /// <summary>False when the module is encrypted (WITH ENCRYPTION) — nothing to back up or patch.</summary>
    public bool HasDefinition => !IsEncrypted && !string.IsNullOrWhiteSpace(Definition);

    /// <summary>Safe file stem, e.g. <c>dbo.usp_GetOrders</c>.</summary>
    public string FileStem => $"{Sanitize(Name.Schema)}.{Sanitize(Name.Name)}";

    private static string Sanitize(string part)
    {
        Span<char> buffer = stackalloc char[part.Length];
        for (int i = 0; i < part.Length; i++)
        {
            buffer[i] = Array.IndexOf(Path.GetInvalidFileNameChars(), part[i]) >= 0 ? '_' : part[i];
        }

        return new string(buffer);
    }
}

/// <summary>Lightweight listing entry for the object picker.</summary>
public sealed record ObjectSummary(ObjectName Name, ProgrammableObjectKind Kind, DateTime ModifiedServerTime);

public static class ProgrammableObjectKinds
{
    /// <summary>Maps a <c>sys.objects.type</c> code to a kind. Null for unsupported (CLR, etc.).</summary>
    public static ProgrammableObjectKind? FromSysType(string sysType) => sysType.Trim() switch
    {
        "P" => ProgrammableObjectKind.StoredProcedure,
        "FN" => ProgrammableObjectKind.ScalarFunction,
        "TF" => ProgrammableObjectKind.TableValuedFunction,
        "IF" => ProgrammableObjectKind.TableValuedFunction,
        "V" => ProgrammableObjectKind.View,
        "TR" => ProgrammableObjectKind.Trigger,
        _ => null,
    };

    public static string Noun(this ProgrammableObjectKind kind) => kind switch
    {
        ProgrammableObjectKind.StoredProcedure => "stored procedure",
        ProgrammableObjectKind.ScalarFunction => "scalar function",
        ProgrammableObjectKind.TableValuedFunction => "table-valued function",
        ProgrammableObjectKind.View => "view",
        ProgrammableObjectKind.Trigger => "trigger",
        _ => "object",
    };
}
