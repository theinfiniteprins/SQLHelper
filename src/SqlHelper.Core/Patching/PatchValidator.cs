using DiffPlex;
using DiffPlex.Model;
using SqlHelper.Core.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Patching;

public sealed record PatchValidation(
    bool Ok,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    int ChangedLineCount,
    SignatureComparison? SignatureChange);

/// <summary>
/// Gates every patched result, from either tier, before it is offered for approval: the result
/// must still parse as exactly one module of the same kind and name, and the size of the change
/// is sanity-checked against what the hunk itself asked for.
/// </summary>
public static class PatchValidator
{
    public static PatchValidation Validate(
        string originalBody,
        string patchedBody,
        ObjectName expectedName,
        ProgrammableObjectKind expectedKind,
        int hunkLineCount)
    {
        ArgumentNullException.ThrowIfNull(originalBody);
        ArgumentNullException.ThrowIfNull(patchedBody);

        var errors = new List<string>();
        var warnings = new List<string>();

        ModuleExtraction extraction = TSqlNormalizer.ExtractModule(patchedBody);
        if (!extraction.Ok)
        {
            errors.Add($"The patched text is no longer valid T-SQL: {extraction.Error}");
            return new PatchValidation(false, errors, warnings, 0, null);
        }

        if (extraction.Kind != expectedKind)
        {
            errors.Add($"The patch turned this into a {extraction.Kind} instead of a {expectedKind}.");
        }

        if (!Equals(extraction.Name, expectedName))
        {
            errors.Add($"The patched object is named {extraction.Name} instead of {expectedName}.");
        }

        var differ = new Differ();
        DiffResult lineDiff = differ.CreateLineDiffs(originalBody, patchedBody, ignoreWhitespace: false);
        int changedLines = lineDiff.DiffBlocks.Sum(b => Math.Max(b.DeleteCountA, b.InsertCountB));

        int budget = Math.Max(hunkLineCount * 3, hunkLineCount + 10);
        if (changedLines > budget)
        {
            warnings.Add(
                $"The patch changed {changedLines} lines, well beyond the {hunkLineCount}-line hunk that was authored " +
                "— double-check the diff before approving this target.");
        }

        SignatureComparison? signatureChange = null;
        RoutineSignature? before = RoutineSignature.FromDefinition(originalBody, expectedKind);
        RoutineSignature? after = RoutineSignature.FromDefinition(patchedBody, expectedKind);
        if (before is not null && after is not null)
        {
            signatureChange = before.CompareWith(after);
            if (!signatureChange.Identical)
            {
                warnings.Add("The patch changes the parameter signature — callers on the old signature may break.");
            }
        }

        return new PatchValidation(errors.Count == 0, errors, warnings, changedLines, signatureChange);
    }
}
