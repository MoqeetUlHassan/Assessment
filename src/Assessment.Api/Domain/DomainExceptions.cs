namespace Assessment.Api.Domain;

/// <summary>Input or invariant violation. Maps to 400.</summary>
public class DomainException(string message) : Exception(message);

/// <summary>Action not legal from the current state. Maps to 409.</summary>
public class InvalidTransitionException(RequestStatus status, RevisionKind? pendingKind, RequestAction action)
    : DomainException($"'{action}' is not allowed when the request is {Describe(status, pendingKind)}.")
{
    private static string Describe(RequestStatus status, RevisionKind? pendingKind) =>
        pendingKind is null ? status.ToString() : $"{status} ({pendingKind})";
}

/// <summary>The approver acted on a revision that is no longer the pending one. Maps to 409.</summary>
public class StaleRevisionException(Guid suppliedRevisionId)
    : DomainException($"Revision {suppliedRevisionId} is no longer pending; reload the request and review the current revision.");
