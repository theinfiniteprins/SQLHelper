using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlHelper.Core.Guard;
using SqlHelper.Core.Internal;
using SqlHelper.Core.Model;

namespace SqlHelper.Core.Scripting;

/// <summary>The single module statement pulled out of a deployment script.</summary>
public sealed record ModuleExtraction(
    bool Ok,
    ProgrammableObjectKind? Kind,
    ObjectName? Name,
    string ModuleText,
    IReadOnlyList<string> RemovedLeadingDirectives,
    string? Error);

/// <summary>A normalized view of a definition plus the key used to group identical ones.</summary>
public sealed record NormalizedModule(string CanonicalText, string Key, bool Degraded);

/// <summary>
/// Normalizes T-SQL module definitions so that cosmetic differences — whitespace, casing of
/// keywords, <c>USE</c>/<c>GO</c>/<c>SET</c> preamble, and CREATE vs ALTER vs CREATE OR ALTER —
/// do not register as different versions. Used to cluster client copies into cohorts and to
/// locate a patch anchor against the clean module body.
/// </summary>
public static partial class TSqlNormalizer
{
    [GeneratedRegex(@"^\s*(CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+(PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingVerb();

    [GeneratedRegex(@"^\s*(USE\s+.+|GO(\s+\d+)?|SET\s+(ANSI_NULLS|QUOTED_IDENTIFIER|ANSI_PADDING|ANSI_WARNINGS|ARITHABORT|CONCAT_NULL_YIELDS_NULL|NUMERIC_ROUNDABORT|NOCOUNT|XACT_ABORT)\s+(ON|OFF))\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDirectiveLine();

    /// <summary>Deterministic re-serialisation of a parsed fragment.</summary>
    private static SqlScriptGeneratorOptions GeneratorOptions => new()
    {
        KeywordCasing = KeywordCasing.Uppercase,
        IncludeSemicolons = true,
        AlignClauseBodies = false,
        AlignColumnDefinitionFields = false,
        AsKeywordOnOwnLine = false,
        IndentationSize = 4,
        NewLineBeforeFromClause = false,
        NewLineBeforeWhereClause = false,
        NewLineBeforeOrderByClause = false,
        NewLineBeforeGroupByClause = false,
        NewLineBeforeHavingClause = false,
        NewLineBeforeJoinClause = false,
    };

    /// <summary>SHA-256 of the definition after only line-ending and trailing-whitespace normalisation.</summary>
    public static string ExactKey(string definition) => Hashing.Sha256Hex(NormalizeLines(definition));

    /// <summary>
    /// SHA-256 of a canonical re-serialisation with the CREATE/ALTER verb folded together. Two
    /// definitions with the same key are the same logic regardless of formatting. Falls back to a
    /// whitespace-normalised text hash when the definition does not parse (marked degraded).
    /// </summary>
    public static NormalizedModule Normalize(string definition, TSqlCompatibility level = TSqlParsing.Default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        TSqlFragment fragment = TSqlParsing.Parse(definition, out IList<ParseError> errors, level);
        if (errors.Count > 0)
        {
            string fallback = CollapseWhitespace(NormalizeLines(definition));
            return new NormalizedModule(fallback, "degraded:" + Hashing.Sha256Hex(fallback), Degraded: true);
        }

        TSqlFragment target = FindModuleFragment(fragment) ?? fragment;
        var generator = new Sql160ScriptGenerator(GeneratorOptions);
        generator.GenerateScript(target, out string canonical);

        canonical = LeadingVerb().Replace(canonical.Trim(), "CREATE OR ALTER $2", 1);
        canonical = CollapseWhitespace(canonical);

        return new NormalizedModule(canonical, Hashing.Sha256Hex(canonical), Degraded: false);
    }

    public static string SemanticKey(string definition, TSqlCompatibility level = TSqlParsing.Default)
        => Normalize(definition, level).Key;

    /// <summary>
    /// Removes a leading <c>USE</c> / <c>GO</c> / <c>SET</c> preamble, returning the script from the
    /// first real statement onward. Handles the SSMS "Script as ALTER" header the operator often
    /// pastes in.
    /// </summary>
    public static string StripLeadingDirectives(
        string script,
        out IReadOnlyList<string> removed,
        TSqlCompatibility level = TSqlParsing.Default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var removedList = new List<string>();
        removed = removedList;

        TSqlFragment fragment = TSqlParsing.Parse(script, out IList<ParseError> errors, level);
        if (errors.Count == 0 && fragment is TSqlScript parsed)
        {
            foreach (TSqlStatement statement in parsed.Batches.SelectMany(b => b.Statements))
            {
                if (statement is UseStatement or PredicateSetStatement)
                {
                    removedList.Add(Slice(script, statement).Trim());
                    continue;
                }

                return script[statement.StartOffset..].TrimStart('\r', '\n');
            }

            return string.Empty; // preamble only
        }

        // Fallback: strip matching leading lines textually.
        var lines = new List<string>(script.Split('\n'));
        int keepFrom = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0 || LeadingDirectiveLine().IsMatch(line))
            {
                if (line.Trim().Length != 0)
                {
                    removedList.Add(line.Trim());
                }

                keepFrom = i + 1;
                continue;
            }

            break;
        }

        return string.Join('\n', lines.Skip(keepFrom));
    }

    /// <summary>Pulls the single Create/Alter module statement out of a script.</summary>
    public static ModuleExtraction ExtractModule(string script, TSqlCompatibility level = TSqlParsing.Default)
    {
        ArgumentNullException.ThrowIfNull(script);

        TSqlFragment fragment = TSqlParsing.Parse(script, out IList<ParseError> errors, level);
        if (errors.Count > 0)
        {
            return new ModuleExtraction(false, null, null, string.Empty, [], $"Script did not parse: {TSqlParsing.Describe(errors[0])}");
        }

        if (fragment is not TSqlScript parsed)
        {
            return new ModuleExtraction(false, null, null, string.Empty, [], "Script is not a batch.");
        }

        List<TSqlStatement> allStatements = parsed.Batches.SelectMany(b => b.Statements).ToList();
        List<TSqlStatement> modules = allStatements.Where(IsModuleStatement).ToList();

        if (modules.Count == 0)
        {
            return new ModuleExtraction(false, null, null, string.Empty, [],
                "No CREATE / ALTER of a procedure, function, view or trigger was found.");
        }

        if (modules.Count > 1)
        {
            return new ModuleExtraction(false, null, null, string.Empty, [],
                $"Script changes {modules.Count} objects; deploy one object at a time.");
        }

        TSqlStatement module = modules[0];
        var removed = allStatements
            .TakeWhile(s => !ReferenceEquals(s, module))
            .Where(s => s is UseStatement or PredicateSetStatement)
            .Select(s => Slice(script, s).Trim())
            .ToList();

        return new ModuleExtraction(
            true,
            KindOf(module),
            NameOf(module),
            Slice(script, module),
            removed,
            null);
    }

    internal static bool IsModuleStatement(TSqlStatement statement) =>
        statement is ProcedureStatementBodyBase or ViewStatementBody or TriggerStatementBody;

    private static TSqlStatement? FindModuleFragment(TSqlFragment root)
    {
        if (root is not TSqlScript script)
        {
            return null;
        }

        return script.Batches
            .SelectMany(b => b.Statements)
            .FirstOrDefault(IsModuleStatement);
    }

    private static ProgrammableObjectKind? KindOf(TSqlStatement statement) => statement switch
    {
        FunctionStatementBody f => IsTableValued(f) ? ProgrammableObjectKind.TableValuedFunction : ProgrammableObjectKind.ScalarFunction,
        ProcedureStatementBody => ProgrammableObjectKind.StoredProcedure,
        ViewStatementBody => ProgrammableObjectKind.View,
        TriggerStatementBody => ProgrammableObjectKind.Trigger,
        _ => null,
    };

    private static bool IsTableValued(FunctionStatementBody function) =>
        function.ReturnType is TableValuedFunctionReturnType or SelectFunctionReturnType;

    private static ObjectName? NameOf(TSqlStatement statement)
    {
        SchemaObjectName? son = statement switch
        {
            ProcedureStatementBody p => p.ProcedureReference?.Name,
            FunctionStatementBody f => f.Name,
            ViewStatementBody v => v.SchemaObjectName,
            TriggerStatementBody t => t.Name,
            _ => null,
        };

        if (son is null || son.Identifiers.Count == 0)
        {
            return null;
        }

        string name = son.Identifiers[^1].Value;
        string schema = son.Identifiers.Count >= 2 ? son.Identifiers[^2].Value : ObjectName.DefaultSchema;
        return new ObjectName(schema, name);
    }

    private static string Slice(string script, TSqlFragment fragment)
        => script.Substring(fragment.StartOffset, fragment.FragmentLength);

    private static string NormalizeLines(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            builder.Append(lines[i].TrimEnd());
            if (i < lines.Length - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString().Trim('\n');
    }

    private static string CollapseWhitespace(string text)
        => WhitespaceRun().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
