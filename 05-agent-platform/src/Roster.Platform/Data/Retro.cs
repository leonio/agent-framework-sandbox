namespace Roster.Platform.Data;

public enum RetroState { Open, Closed }

/// <summary>
/// A retro on one assignment: a conversation between its owner and the <c>retro-facilitator</c> agent that ends in
/// cards the person confirms. One per assignment; reopening continues the same conversation.
/// </summary>
public sealed class RetroSession
{
    public Guid Id { get; set; }

    public Guid AssignmentId { get; set; }

    public required string OwnerId { get; set; }

    public RetroState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One message in a retro. The facilitator's reply is created empty with <see cref="InProgress"/> set and filled in as
/// the model streams (written at most every 250 ms), so the UI can show it being typed.
/// </summary>
public sealed class RetroMessage
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>Position in the conversation, from 0. The order the facilitator sees.</summary>
    public int Sequence { get; set; }

    /// <summary><c>user</c> (the person, or the platform's opening note) or <c>assistant</c> (the facilitator).</summary>
    public required string Role { get; set; }

    public string Content { get; set; } = "";

    /// <summary>True while the facilitator is still writing this message.</summary>
    public bool InProgress { get; set; }

    /// <summary>For facilitator messages, the ledger row of the turn that wrote it.</summary>
    public Guid? InvocationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum CardSentiment { Good, Bad, Ugly }

public enum CardState
{
    /// <summary>Proposed by the facilitator. Not feedback yet: only the person can make it so.</summary>
    Draft,

    Confirmed,

    /// <summary>The person threw the draft away. Kept so the facilitator's proposals can be judged too.</summary>
    Discarded,
}

/// <summary>
/// A Good, Bad or Ugly card. The facilitator drafts it; the person edits and confirms it, and from then on it is their
/// feedback (with an "assisted by" mark). Confirmed cards feed the agents' scorecards.
/// </summary>
public sealed class RetroCard
{
    public Guid Id { get; set; }

    public Guid SessionId { get; set; }

    public Guid AssignmentId { get; set; }

    public CardSentiment Sentiment { get; set; }

    public required string Text { get; set; }

    /// <summary>The agent the card is about, if it is about one.</summary>
    public string? AgentName { get; set; }

    /// <summary>That agent's hash in this assignment, filled in by the platform so the scorecard credits the right version.</summary>
    public string? AgentHash { get; set; }

    public CardState State { get; set; }

    /// <summary><c>retro-facilitator</c> when the card started as a draft from it; null when the person wrote it alone.</summary>
    public string? AssistedBy { get; set; }

    public string? AuthorId { get; set; }

    /// <summary>Name and title copied at confirmation (see <see cref="AppUser.Title"/>).</summary>
    public string? AuthorName { get; set; }

    public string? AuthorTitle { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
}
