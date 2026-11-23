using RuleOpen.Core.Models;

namespace RuleOpen.Core.Rules;

/// <summary>
/// Deterministic total-order comparer for matching rule candidates.
/// Ensures evaluation order is strictly independent of JSON array ordering.
/// </summary>
public sealed class RulePrecedenceComparer(ConflictResolutionMode mode) : IComparer<RuleMatchCandidate>
{
    public int Compare(RuleMatchCandidate? x, RuleMatchCandidate? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        int result = mode == ConflictResolutionMode.PriorityFirst
            ? ComparePriorityFirst(x, y)
            : CompareSpecificityFirst(x, y);

        if (result != 0)
        {
            return result;
        }

        // Narrower extension list (e.g., [.pdf] vs [.pdf, .txt, .md]) wins ties.
        result = x.Rule.Extensions.Count.CompareTo(y.Rule.Extensions.Count);
        if (result != 0)
        {
            return result;
        }

        // Final total deterministic tie-breaker independent of input collection order.
        return string.Compare(x.Rule.Id, y.Rule.Id, StringComparison.Ordinal);
    }

    private static int CompareSpecificityFirst(RuleMatchCandidate x, RuleMatchCandidate y)
    {
        // 1. Deeper folder depth wins.
        int result = y.FolderDepth.CompareTo(x.FolderDepth);
        if (result != 0)
        {
            return result;
        }

        // 2. Direct/exact folder rule wins over recursive subfolder match at equal depth.
        result = y.IsExactFolderMatch.CompareTo(x.IsExactFolderMatch);
        if (result != 0)
        {
            return result;
        }

        // 3. Explicit priority descending.
        return y.Rule.Priority.CompareTo(x.Rule.Priority);
    }

    private static int ComparePriorityFirst(RuleMatchCandidate x, RuleMatchCandidate y)
    {
        // 1. Explicit priority descending.
        int result = y.Rule.Priority.CompareTo(x.Rule.Priority);
        if (result != 0)
        {
            return result;
        }

        // 2. Deeper folder depth wins.
        result = y.FolderDepth.CompareTo(x.FolderDepth);
        if (result != 0)
        {
            return result;
        }

        // 3. Direct/exact folder rule wins over recursive match.
        return y.IsExactFolderMatch.CompareTo(x.IsExactFolderMatch);
    }
}
