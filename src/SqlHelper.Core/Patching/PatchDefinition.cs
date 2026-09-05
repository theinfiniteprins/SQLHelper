using SqlHelper.Core.Scripting;

namespace SqlHelper.Core.Patching;

/// <summary>
/// One contiguous change: the old lines (with enough surrounding context to be locatable) and
/// their replacement. Never a whole-file replace — this is spliced into whatever the target
/// database already has.
/// </summary>
public sealed record PatchHunk(string AnchorText, string ReplacementText, int ReferenceStartLine);

/// <summary>A named change to one programmable object, made up of one or more hunks, applied in order.</summary>
public sealed record PatchDefinition(string ChangeTitle, ObjectName TargetObject, IReadOnlyList<PatchHunk> Hunks)
{
    public static PatchDefinition SingleHunk(string changeTitle, ObjectName target, string anchor, string replacement) =>
        new(changeTitle, target, [new PatchHunk(anchor, replacement, ReferenceStartLine: 0)]);
}
