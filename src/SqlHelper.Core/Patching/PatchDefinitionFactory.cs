using DiffPlex;
using DiffPlex.Model;
using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Patching;

/// <summary>Outcome of building a <see cref="PatchDefinition"/> from a before/after reference pair.</summary>
public sealed record PatchDefinitionResult(bool Ok, PatchDefinition? Patch, string? Error)
{
    public static PatchDefinitionResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// Builds a patch by diffing two full versions of the reference object — the natural way a
/// DBA already has the change in hand ("here's the old canonical procedure, here's the fixed
/// one"). Any <c>USE</c> / <c>GO</c> / <c>SET</c> preamble the operator pasted in (SSMS's
/// "Script as ALTER" header, most commonly) is stripped from both sides before diffing, so a
/// difference there is never mistaken for part of the change.
/// </summary>
public static class PatchDefinitionFactory
{
    private const int DefaultContextLines = 2;

    public static PatchDefinitionResult FromReferenceDiff(
        string changeTitle,
        string beforeScript,
        string afterScript,
        int contextLines = DefaultContextLines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeScript);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterScript);

        ModuleExtraction before = TSqlNormalizer.ExtractModule(beforeScript);
        if (!before.Ok)
        {
            return PatchDefinitionResult.Failure($"'Before' script: {before.Error}");
        }

        ModuleExtraction after = TSqlNormalizer.ExtractModule(afterScript);
        if (!after.Ok)
        {
            return PatchDefinitionResult.Failure($"'After' script: {after.Error}");
        }

        if (!Equals(before.Name, after.Name))
        {
            return PatchDefinitionResult.Failure(
                $"'Before' script is for {before.Name} but 'after' is for {after.Name} — they must be the same object.");
        }

        var differ = new Differ();
        DiffResult diff = differ.CreateLineDiffs(before.ModuleText, after.ModuleText, ignoreWhitespace: false);

        if (diff.DiffBlocks.Count == 0)
        {
            return PatchDefinitionResult.Failure("The two versions are identical — there is nothing to patch.");
        }

        var hunks = new List<PatchHunk>();
        foreach (DiffBlock block in diff.DiffBlocks)
        {
            int contextStart = Math.Max(0, block.DeleteStartA - contextLines);
            int contextEndExclusive = Math.Min(diff.PiecesOld.Count, block.DeleteStartA + block.DeleteCountA + contextLines);

            IEnumerable<string> before2 = diff.PiecesOld.Skip(contextStart).Take(block.DeleteStartA - contextStart);
            IEnumerable<string> oldMiddle = diff.PiecesOld.Skip(block.DeleteStartA).Take(block.DeleteCountA);
            IEnumerable<string> newMiddle = diff.PiecesNew.Skip(block.InsertStartB).Take(block.InsertCountB);
            IEnumerable<string> after2 = diff.PiecesOld.Skip(block.DeleteStartA + block.DeleteCountA).Take(contextEndExclusive - (block.DeleteStartA + block.DeleteCountA));

            string anchor = string.Join('\n', before2.Concat(oldMiddle).Concat(after2));
            string replacement = string.Join('\n', before2.Concat(newMiddle).Concat(after2));

            hunks.Add(new PatchHunk(anchor, replacement, block.DeleteStartA + 1));
        }

        var patch = new PatchDefinition(changeTitle, before.Name!, hunks);
        return new PatchDefinitionResult(true, patch, null);
    }
}
