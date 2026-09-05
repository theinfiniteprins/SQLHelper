using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHelper.Core.Guard;

/// <summary>A construct in a script that makes it more than a read-only query.</summary>
public sealed record ReadOnlyViolation(string Construct, int Line, int Column, string Reason);

/// <summary>Result of inspecting a script for the SELECT-only screen.</summary>
public sealed record ReadOnlyVerdict(
    bool IsReadOnly,
    IReadOnlyList<ReadOnlyViolation> Violations,
    IReadOnlyList<string> ParseErrors)
{
    public bool ParsedCleanly => ParseErrors.Count == 0;

    public string Summary => IsReadOnly
        ? "Read-only — safe to run on the query screen."
        : ParseErrors.Count > 0
            ? $"Could not verify: the script did not parse ({ParseErrors.Count} error(s))."
            : $"Blocked: {string.Join("; ", Violations.Select(v => $"{v.Construct} (line {v.Line})"))}.";
}

/// <summary>
/// Decides whether a script is safe to run on the read-only query screen by parsing it with
/// ScriptDom and walking the syntax tree. This is an <em>allowlist</em>: anything that is not a
/// plain SELECT (or a harmless helper such as <c>DECLARE</c> / <c>SET NOCOUNT</c> / control flow)
/// is rejected. A script that does not parse is rejected too — an unverifiable script is not
/// trusted. This is one of three layers; the executor also wraps every run in a rolled-back
/// transaction, and the connection should use a read-only login.
/// </summary>
public static class ReadOnlyGuard
{
    public static ReadOnlyVerdict Inspect(string sql, TSqlCompatibility level = TSqlParsing.Default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new ReadOnlyVerdict(false, [new ReadOnlyViolation("(empty)", 0, 0, "There is nothing to run.")], []);
        }

        TSqlFragment fragment = TSqlParsing.Parse(sql, out IList<ParseError> errors, level);
        if (errors.Count > 0)
        {
            return new ReadOnlyVerdict(false, [], errors.Select(TSqlParsing.Describe).ToList());
        }

        var visitor = new Visitor();
        fragment.Accept(visitor);
        visitor.Violations.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));

        return new ReadOnlyVerdict(visitor.Violations.Count == 0, visitor.Violations, []);
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        // Statement types that cannot change persistent data or schema.
        private static readonly HashSet<Type> Allowed =
        [
            typeof(SelectStatement),
            typeof(DeclareVariableStatement),
            typeof(DeclareTableVariableStatement),
            typeof(SetVariableStatement),
            typeof(PredicateSetStatement),                 // SET NOCOUNT / ANSI_NULLS / etc.
            typeof(SetStatisticsStatement),
            typeof(SetRowCountStatement),
            typeof(SetOffsetsStatement),
            typeof(SetCommandStatement),
            typeof(SetTransactionIsolationLevelStatement),
            typeof(IfStatement),
            typeof(WhileStatement),
            typeof(BeginEndBlockStatement),
            typeof(TryCatchStatement),
            typeof(BreakStatement),
            typeof(ContinueStatement),
            typeof(GoToStatement),
            typeof(LabelStatement),
            typeof(ReturnStatement),
            typeof(WaitForStatement),
            typeof(PrintStatement),
            typeof(UseStatement),
            typeof(BeginTransactionStatement),
            typeof(CommitTransactionStatement),
            typeof(RollbackTransactionStatement),
            typeof(SaveTransactionStatement),
        ];

        private static readonly Dictionary<Type, string> PrettyNames = new()
        {
            [typeof(UpdateStatement)] = "UPDATE",
            [typeof(DeleteStatement)] = "DELETE",
            [typeof(InsertStatement)] = "INSERT",
            [typeof(MergeStatement)] = "MERGE",
            [typeof(ExecuteStatement)] = "EXEC / EXECUTE",
            [typeof(TruncateTableStatement)] = "TRUNCATE TABLE",
            [typeof(BulkInsertStatement)] = "BULK INSERT",
        };

        public List<ReadOnlyViolation> Violations { get; } = [];

        public override void Visit(TSqlFragment node)
        {
            switch (node)
            {
                case SelectStatement { Into: { } into }:
                    Violations.Add(new ReadOnlyViolation(
                        "SELECT … INTO",
                        node.StartLine,
                        node.StartColumn,
                        $"SELECT … INTO writes a new table ({Format(into)})."));
                    break;

                case TSqlStatement statement when !Allowed.Contains(statement.GetType()):
                    Violations.Add(new ReadOnlyViolation(
                        Pretty(statement.GetType()),
                        node.StartLine,
                        node.StartColumn,
                        "Only read-only SELECT statements are allowed on this screen."));
                    break;
            }
        }

        private static string Pretty(Type type) =>
            PrettyNames.TryGetValue(type, out string? name)
                ? name
                : type.Name.EndsWith("Statement", StringComparison.Ordinal)
                    ? type.Name[..^"Statement".Length]
                    : type.Name;

        private static string Format(SchemaObjectName name) =>
            string.Join('.', name.Identifiers.Select(i => i.Value));
    }
}
