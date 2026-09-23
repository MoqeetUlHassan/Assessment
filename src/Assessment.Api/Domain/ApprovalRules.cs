namespace Assessment.Api.Domain;

/// <summary>
/// When does a change need a human approver? (PLAN.md §6)
/// The threshold is always the request's own snapshot, never the org's current value.
/// </summary>
public static class ApprovalRules
{
    /// <summary>
    /// Initial or edited content. Needs approval when the amount is at/above the threshold AND something
    /// the approver has not already signed off on is being asked for: a higher amount, or different wording.
    /// With nothing approved yet, any amount at/above the threshold needs approval.
    /// Cost decreases with unchanged wording never need approval.
    /// </summary>
    public static bool ContentNeedsApproval(
        decimal amount, string description, decimal threshold, RequestRevision? lastApprovedContent) =>
        amount >= threshold
        && (lastApprovedContent is null
            || amount > lastApprovedContent.Amount
            || !string.Equals(description, lastApprovedContent.Description, StringComparison.Ordinal));

    /// <summary>The actual cost needs approval whenever it is at/above the threshold.</summary>
    public static bool ActualCostNeedsApproval(decimal actualCost, decimal threshold) => actualCost >= threshold;
}
