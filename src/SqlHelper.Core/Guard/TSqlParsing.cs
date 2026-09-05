using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHelper.Core.Guard;

/// <summary>Target T-SQL dialect for parsing. Matches SQL Server compatibility levels.</summary>
public enum TSqlCompatibility
{
    Sql2016 = 130,
    Sql2017 = 140,
    Sql2019 = 150,
    Sql2022 = 160,
    Sql2025 = 170,
}

/// <summary>Central place to create a ScriptDom parser and run a parse.</summary>
public static class TSqlParsing
{
    public const TSqlCompatibility Default = TSqlCompatibility.Sql2022;

    public static TSqlParser CreateParser(TSqlCompatibility level = Default, bool quotedIdentifiers = true) => level switch
    {
        TSqlCompatibility.Sql2016 => new TSql130Parser(quotedIdentifiers),
        TSqlCompatibility.Sql2017 => new TSql140Parser(quotedIdentifiers),
        TSqlCompatibility.Sql2019 => new TSql150Parser(quotedIdentifiers),
        TSqlCompatibility.Sql2022 => new TSql160Parser(quotedIdentifiers),
        TSqlCompatibility.Sql2025 => new TSql170Parser(quotedIdentifiers),
        _ => new TSql160Parser(quotedIdentifiers),
    };

    public static TSqlFragment Parse(string sql, out IList<ParseError> errors, TSqlCompatibility level = Default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        using var reader = new StringReader(sql);
        return CreateParser(level).Parse(reader, out errors);
    }

    public static string Describe(ParseError error) => $"L{error.Line}:{error.Column}  {error.Message}";
}
