using DiffPlex;
using DiffPlex.Model;

namespace SqlHelper.Core.Patching;

/// <param name="Ok">False when the two sides changed the same lines and there is no safe answer.</param>
/// <param name="Merged">The merged block; null on conflict.</param>
/// <param name="Conflict">What clashed, in words, when <paramref name="Ok"/> is false.</param>
/// <param name="KeptClientLines">How many lines existed only on the client's side and were carried through.</param>
internal sealed record MergeResult(bool Ok, IReadOnlyList<RawLine>? Merged, string? Conflict, int KeptClientLines);

/// <summary>
/// Merges a change into a block of a client's code the way a version control system would, with
/// the authored "before" text as the common ancestor:
///
///   base   = the anchor, i.e. the reference procedure as it was
///   ours   = the replacement, i.e. the reference procedure with the fix
///   theirs = the block found in this client's procedure
///
/// Lines the client added inside that block are carried through untouched; only the lines the
/// change actually touches are rewritten. Where both sides edited the same lines there is no
/// honest answer, so it reports a conflict and the caller sends that client to a manual edit
/// rather than guessing. Replacing the block wholesale would silently delete the client's own
/// customisations, which on a production database is not an acceptable outcome.
/// </summary>
internal static class ThreeWayLineMerge
{
    public static MergeResult Merge(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> replacementLines,
        IReadOnlyList<RawLine> clientLines,
        string lineEnding)
    {
        var differ = new Differ();
        string[] clientTrimmed = [.. clientLines.Select(l => l.Trimmed)];

        DiffResult toReplacement = differ.CreateLineDiffs(Join(baseLines), Join(replacementLines), ignoreWhitespace: true);
        DiffResult toClient = differ.CreateLineDiffs(Join(baseLines), Join(clientTrimmed), ignoreWhitespace: true);

        List<DiffBlock> ours = [.. toReplacement.DiffBlocks.OrderBy(b => b.DeleteStartA)];
        List<DiffBlock> theirs = [.. toClient.DiffBlocks.OrderBy(b => b.DeleteStartA)];

        var output = new List<RawLine>();
        int basePos = 0;
        int oursIndex = 0;
        int theirsIndex = 0;
        int clientOffset = 0;
        int keptClientLines = 0;

        while (basePos < baseLines.Count || oursIndex < ours.Count || theirsIndex < theirs.Count)
        {
            DiffBlock? ourBlock = oursIndex < ours.Count ? ours[oursIndex] : null;
            DiffBlock? theirBlock = theirsIndex < theirs.Count ? theirs[theirsIndex] : null;

            bool ourHere = ourBlock is not null && ourBlock.DeleteStartA <= basePos;
            bool theirHere = theirBlock is not null && theirBlock.DeleteStartA <= basePos;

            if (ourHere && theirHere)
            {
                bool ourTouchesLines = ourBlock!.DeleteCountA > 0;
                bool theirTouchesLines = theirBlock!.DeleteCountA > 0;

                if (ourTouchesLines && theirTouchesLines && Overlap(ourBlock, theirBlock))
                {
                    string theirText = Describe(clientTrimmed, theirBlock.InsertStartB, theirBlock.InsertCountB);
                    string baseText = Describe(baseLines, ourBlock.DeleteStartA, ourBlock.DeleteCountA);
                    return new MergeResult(false, null,
                        $"this client has its own version of the line(s) the change rewrites " +
                        $"(reference has \"{baseText}\", this client has \"{theirText}\")",
                        keptClientLines);
                }

                // A pure insertion on the client's side goes in first, then our change proceeds.
                if (!theirTouchesLines)
                {
                    AppendClientRange(output, clientLines, theirBlock.InsertStartB, theirBlock.InsertCountB);
                    keptClientLines += theirBlock.InsertCountB;
                    clientOffset += theirBlock.InsertCountB;
                    theirsIndex++;
                    continue;
                }

                if (!ourTouchesLines)
                {
                    AppendNewLines(output, replacementLines, ourBlock.InsertStartB, ourBlock.InsertCountB, lineEnding);
                    oursIndex++;
                    continue;
                }
            }

            if (ourHere)
            {
                AppendNewLines(output, replacementLines, ourBlock!.InsertStartB, ourBlock.InsertCountB, lineEnding);
                basePos += ourBlock.DeleteCountA;
                clientOffset += 0;
                oursIndex++;

                // The client's copy of those same base lines is superseded by the change.
                SkipClientLines(ref clientOffset, ourBlock.DeleteCountA, theirs, ref theirsIndex, basePos);
                continue;
            }

            if (theirHere)
            {
                AppendClientRange(output, clientLines, theirBlock!.InsertStartB, theirBlock.InsertCountB);
                keptClientLines += theirBlock.DeleteCountA == 0 ? theirBlock.InsertCountB : 0;
                clientOffset += theirBlock.InsertCountB - theirBlock.DeleteCountA;
                basePos += theirBlock.DeleteCountA;
                theirsIndex++;
                continue;
            }

            if (basePos >= baseLines.Count)
            {
                break;
            }

            // Untouched by either side: keep the client's own line, formatting and all.
            int clientIndex = basePos + clientOffset;
            output.Add(clientIndex >= 0 && clientIndex < clientLines.Count
                ? clientLines[clientIndex]
                : new RawLine(baseLines[basePos], lineEnding));
            basePos++;
        }

        return new MergeResult(true, output, null, keptClientLines);
    }

    private static bool Overlap(DiffBlock a, DiffBlock b) =>
        a.DeleteStartA < b.DeleteStartA + b.DeleteCountA && b.DeleteStartA < a.DeleteStartA + a.DeleteCountA;

    private static void SkipClientLines(ref int clientOffset, int baseCount, List<DiffBlock> theirs, ref int theirsIndex, int newBasePos)
    {
        // Drop any client block that only covered base lines the change has now rewritten.
        while (theirsIndex < theirs.Count
               && theirs[theirsIndex].DeleteCountA > 0
               && theirs[theirsIndex].DeleteStartA + theirs[theirsIndex].DeleteCountA <= newBasePos)
        {
            clientOffset += theirs[theirsIndex].InsertCountB - theirs[theirsIndex].DeleteCountA;
            theirsIndex++;
        }

        _ = baseCount;
    }

    private static void AppendClientRange(List<RawLine> output, IReadOnlyList<RawLine> clientLines, int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            if (index >= 0 && index < clientLines.Count)
            {
                output.Add(clientLines[index]);
            }
        }
    }

    private static void AppendNewLines(List<RawLine> output, IReadOnlyList<string> lines, int start, int count, string lineEnding)
    {
        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            if (index >= 0 && index < lines.Count)
            {
                output.Add(new RawLine(lines[index], lineEnding));
            }
        }
    }

    private static string Join(IReadOnlyList<string> lines) => string.Join('\n', lines);

    /// <summary>A short, readable quote of a range of lines, for a conflict message.</summary>
    private static string Describe(IReadOnlyList<string> lines, int start, int count)
    {
        var picked = new List<string>();
        for (int i = 0; i < count && start + i < lines.Count; i++)
        {
            picked.Add(lines[start + i].Trim());
        }

        string text = string.Join(" / ", picked.Where(p => p.Length > 0));
        return text.Length <= 90 ? text : text[..90] + "…";
    }
}
