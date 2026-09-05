using SqlHelper.Core.Guard;
using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Patching;

/// <summary>
/// One capture of an object from one database, feeding the cohort analysis.
/// <paramref name="Error"/> distinguishes "we couldn't read this database" from "the object
/// genuinely isn't there" — reporting a failed connection as a missing object would be actively
/// misleading.
/// </summary>
public sealed record ObjectCapture(DatabaseTarget Target, ProgrammableObject? Module, string? Error = null);

/// <summary>A group of databases whose copy of the object is identical (by the chosen key).</summary>
/// <param name="Error">Set only for a group that could not be read at all, so the caller can explain why.</param>
public sealed record Cohort(
    string Key,
    string Label,
    IReadOnlyList<DatabaseTarget> Members,
    string? SampleDefinition,
    bool Degraded,
    string? Error = null)
{
    public int Count => Members.Count;

    public bool IsFailure => Error is { Length: > 0 };
}

public sealed record CohortReport(IReadOnlyList<Cohort> Cohorts, string? ReferenceKey)
{
    public bool AllIdentical => Cohorts.Count <= 1;

    public Cohort? Largest => Cohorts.OrderByDescending(c => c.Count).FirstOrDefault();
}

/// <summary>
/// Groups client copies of an object into distinct versions before anything is deployed —
/// "24 clients on canonical, 4 on variant B, 2 on variant C" — so a patch is authored once per
/// variant, not once per client, and drift is visible before it causes a failed deploy.
/// </summary>
public static class CohortAnalyzer
{
    public static CohortReport Cluster(
        IEnumerable<ObjectCapture> captures,
        bool useSemanticKey = true,
        TSqlCompatibility level = TSqlParsing.Default)
    {
        ArgumentNullException.ThrowIfNull(captures);

        var groups = new Dictionary<string, List<DatabaseTarget>>();
        var samples = new Dictionary<string, (string? Text, bool Degraded)>();

        foreach (ObjectCapture capture in captures)
        {
            string key;
            string? sample;
            bool degraded = false;

            if (capture.Error is { Length: > 0 } failure)
            {
                key = "~failed:" + failure;
                sample = null;
            }
            else if (capture.Module is null)
            {
                key = "~missing";
                sample = null;
            }
            else if (!capture.Module.HasDefinition)
            {
                key = "~encrypted";
                sample = null;
            }
            else if (useSemanticKey)
            {
                NormalizedModule normalized = TSqlNormalizer.Normalize(capture.Module.Definition, level);
                key = normalized.Key;
                sample = capture.Module.Definition;
                degraded = normalized.Degraded;
            }
            else
            {
                key = TSqlNormalizer.ExactKey(capture.Module.Definition);
                sample = capture.Module.Definition;
            }

            if (!groups.TryGetValue(key, out List<DatabaseTarget>? members))
            {
                members = [];
                groups[key] = members;
                samples[key] = (sample, degraded);
            }

            members.Add(capture.Target);
        }

        List<KeyValuePair<string, List<DatabaseTarget>>> ordered = groups
            .OrderByDescending(g => g.Value.Count)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        string? referenceKey = ordered
            .FirstOrDefault(g => g.Key is not ("~missing" or "~encrypted") && !g.Key.StartsWith("~failed:", StringComparison.Ordinal))
            .Key;

        var cohorts = new List<Cohort>();
        int variantIndex = 0;
        foreach ((string key, List<DatabaseTarget> members) in ordered)
        {
            (string? text, bool degraded) = samples[key];
            bool isFailure = key.StartsWith("~failed:", StringComparison.Ordinal);
            string label = key switch
            {
                "~missing" => "Object does not exist",
                "~encrypted" => "Encrypted — cannot inspect",
                _ when isFailure => "Could not be read",
                _ when key == referenceKey => "Reference (most common)",
                _ => VariantLabel(variantIndex++),
            };

            cohorts.Add(new Cohort(key, label, members, text, degraded,
                isFailure ? key["~failed:".Length..] : null));
        }

        return new CohortReport(cohorts, referenceKey);
    }

    private static string VariantLabel(int index) =>
        index < 25 ? $"Variant {(char)('B' + index)}" : $"Variant #{index + 2}";
}
