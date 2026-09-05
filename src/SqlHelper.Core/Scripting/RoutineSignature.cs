using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlHelper.Core.Guard;
using SqlHelper.Core.Internal;
using SqlHelper.Core.Model;

namespace SqlHelper.Core.Scripting;

/// <summary>One parameter of a procedure or function, read from the parsed module definition.</summary>
public sealed record RoutineParameter(
    int Ordinal,
    string Name,
    string DataType,
    bool IsOutput,
    bool IsReadOnly,
    bool HasDefault)
{
    /// <summary>Stable one-line form used for hashing and diffing.</summary>
    public string Canonical =>
        $"{Name.ToLowerInvariant()}:{DataType.ToLowerInvariant()}" +
        $"{(IsOutput ? " OUT" : "")}{(IsReadOnly ? " READONLY" : "")}{(HasDefault ? " =default" : "")}";

    public string ForHumans
    {
        get
        {
            string suffix = IsOutput ? " OUTPUT" : IsReadOnly ? " READONLY" : "";
            string dflt = HasDefault ? " = <default>" : "";
            return $"{Name} {DataType}{dflt}{suffix}";
        }
    }
}

/// <summary>The callable interface of a routine: its parameters, and its return type for scalar functions.</summary>
public sealed record RoutineSignature(
    ProgrammableObjectKind Kind,
    IReadOnlyList<RoutineParameter> Parameters,
    string? ReturnType)
{
    public string Fingerprint => string.Join(
        " | ",
        Enumerable.Repeat($"kind:{Kind};returns:{ReturnType?.ToLowerInvariant() ?? "-"}", 1)
            .Concat(Parameters.OrderBy(p => p.Ordinal).Select(p => p.Canonical)));

    public string Hash => Hashing.Sha256Hex(Fingerprint);

    /// <summary>Reads the signature straight from a module definition. Returns null when the text does not parse as a routine.</summary>
    public static RoutineSignature? FromDefinition(
        string definition,
        ProgrammableObjectKind kind,
        TSqlCompatibility level = TSqlParsing.Default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (kind is ProgrammableObjectKind.View or ProgrammableObjectKind.Trigger)
        {
            return new RoutineSignature(kind, [], null);
        }

        TSqlFragment fragment = TSqlParsing.Parse(definition, out IList<ParseError> errors, level);
        if (errors.Count > 0)
        {
            return null;
        }

        var visitor = new SignatureVisitor(kind);
        fragment.Accept(visitor);
        return visitor.Result;
    }

    public SignatureComparison CompareWith(RoutineSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var differences = new List<string>();

        if (Kind != other.Kind)
        {
            differences.Add($"kind: {Kind} vs {other.Kind}");
        }

        if (!string.Equals(ReturnType, other.ReturnType, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"return type: {ReturnType ?? "-"} vs {other.ReturnType ?? "-"}");
        }

        Dictionary<string, RoutineParameter> mine = Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RoutineParameter> theirs = other.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (RoutineParameter p in Parameters)
        {
            if (!theirs.TryGetValue(p.Name, out RoutineParameter? match))
            {
                differences.Add($"parameter {p.Name} is only on the reference side");
                continue;
            }

            if (!string.Equals(p.Canonical, match.Canonical, StringComparison.Ordinal))
            {
                differences.Add($"parameter {p.Name}: '{p.ForHumans}' vs '{match.ForHumans}'");
            }

            if (p.Ordinal != match.Ordinal)
            {
                differences.Add($"parameter {p.Name}: position {p.Ordinal} vs {match.Ordinal}");
            }
        }

        foreach (RoutineParameter p in other.Parameters.Where(p => !mine.ContainsKey(p.Name)))
        {
            differences.Add($"parameter {p.Name} is only on the other side");
        }

        return new SignatureComparison(differences.Count == 0, differences);
    }

    private sealed class SignatureVisitor(ProgrammableObjectKind kind) : TSqlFragmentVisitor
    {
        public RoutineSignature? Result { get; private set; }

        public override void Visit(TSqlFragment node)
        {
            switch (node)
            {
                case ProcedureStatementBody proc:
                    Result = new RoutineSignature(kind, Read(proc.Parameters), null);
                    break;

                case FunctionStatementBody func:
                    Result = new RoutineSignature(
                        kind,
                        Read(func.Parameters),
                        func.ReturnType is ScalarFunctionReturnType scalar ? DataTypeText(scalar.DataType) : null);
                    break;
            }
        }

        private static IReadOnlyList<RoutineParameter> Read(IList<ProcedureParameter> parameters)
        {
            var list = new List<RoutineParameter>(parameters.Count);
            for (int i = 0; i < parameters.Count; i++)
            {
                ProcedureParameter p = parameters[i];
                list.Add(new RoutineParameter(
                    Ordinal: i + 1,
                    Name: p.VariableName.Value,
                    DataType: DataTypeText(p.DataType),
                    IsOutput: p.Modifier == ParameterModifier.Output,
                    IsReadOnly: p.Modifier == ParameterModifier.ReadOnly,
                    HasDefault: p.Value is not null));
            }

            return list;
        }

        private static string DataTypeText(DataTypeReference? dataType)
        {
            switch (dataType)
            {
                case SqlDataTypeReference sql:
                {
                    var builder = new StringBuilder(string.Join('.', sql.Name.Identifiers.Select(id => id.Value)).ToLowerInvariant());
                    if (sql.Parameters.Count > 0)
                    {
                        builder.Append('(')
                            .Append(string.Join(',', sql.Parameters.Select(FormatTypeParameter)))
                            .Append(')');
                    }

                    return builder.ToString();
                }

                case UserDataTypeReference user:
                    return string.Join('.', user.Name.Identifiers.Select(id => id.Value)).ToLowerInvariant();

                case null:
                    return "?";

                default:
                    return dataType.GetType().Name;
            }
        }

        private static string FormatTypeParameter(Literal literal) => literal switch
        {
            MaxLiteral => "max",
            IntegerLiteral i => i.Value,
            _ => literal.Value ?? "?",
        };
    }
}

public sealed record SignatureComparison(bool Identical, IReadOnlyList<string> Differences);
