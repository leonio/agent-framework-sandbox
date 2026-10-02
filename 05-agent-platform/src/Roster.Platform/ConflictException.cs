namespace Roster.Platform;

/// <summary>
/// The request is valid but cannot be done in the current state: deciding a finding before the review has finished,
/// writing to a retro while the facilitator is still answering. The API turns it into 409 Conflict.
/// </summary>
/// <remarks>
/// A type of its own rather than a plain <see cref="InvalidOperationException"/>, because the framework throws that
/// one for programming errors too (EF Core's <c>SingleAsync</c> on an empty result, for example), and those must stay
/// 500s, not be passed off as conflicts.
/// </remarks>
public sealed class ConflictException(string message) : InvalidOperationException(message);
