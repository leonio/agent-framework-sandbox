using System.Text;
using Microsoft.Agents.AI;
using MrArchitectureReview.Domain;
using MrArchitectureReview.Persistence;

namespace MrArchitectureReview.Memory;

/// <summary>
/// Feeds past human accept/reject decisions back into a reviewer agent, so the AI learns the team's
/// standards over time without anyone editing the prompt.
/// </summary>
/// <remarks>
/// <para>
/// <b>What an <see cref="AIContextProvider"/> is.</b> The Agent Framework calls every provider registered
/// on a <c>ChatClientAgent</c> just before each model call (<see cref="ProvideAIContextAsync"/>), and
/// merges what it returns (extra instructions, messages and/or tools) into that call. It is the
/// framework's hook for memory and retrieval: the agent itself stays generic, and context is
/// composed in from the outside. <c>TextSearchProvider</c> (used for RAG in <c>Rag/GuidelinesSearch.cs</c>)
/// and <c>ChatHistoryMemoryProvider</c> are built-in examples of the same hook.
/// </para>
/// <para>
/// <b>WHY instructions rather than few-shot messages?</b> We add the history as transient
/// <see cref="AIContext.Instructions"/>. Instructions apply only to this call and are not persisted
/// in chat history, so calibration notes never leak into the transcript we store.
/// </para>
/// <para>
/// <b>This is "memory", not RAG, and that is deliberate.</b> The selection here is a plain filter:
/// same repository, same aspect, newest rejections first. It is predictable and needs no
/// infrastructure. Once the decision log grows into the thousands, swap the body of
/// <see cref="ProvideAIContextAsync"/> for a semantic lookup: embed each past finding + reason
/// (Microsoft.Extensions.AI <c>IEmbeddingGenerator</c>), store it in a vector store
/// (Microsoft.Extensions.VectorData: in-memory, SQLite, Azure AI Search, Qdrant, ...) and retrieve the
/// past decisions most similar to <i>this</i> diff. The interface to the agent does not change.
/// </para>
/// </remarks>
public sealed class ReviewHistoryContextProvider(
    ReviewStore store, string repositoryKey, ReviewAspect aspect, int maxExamples) : AIContextProvider
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var history = await store.GetHistoryAsync(repositoryKey, aspect, maxExamples, cancellationToken);
        if (history.Count == 0)
            return new AIContext();

        var sb = new StringBuilder()
            .AppendLine("## Team feedback on your previous reviews of this repository")
            .AppendLine("Humans triaged earlier findings from you. Calibrate to their standards:");

        foreach (var e in history)
        {
            var reason = string.IsNullOrWhiteSpace(e.Decision.Reason) ? "no reason given" : e.Decision.Reason;
            sb.AppendLine($"- {e.Decision.Verdict.ToString().ToUpperInvariant()}: \"{e.Finding.Title}\" ({e.Finding.Severity}). Reason: {reason}");
        }

        return new AIContext { Instructions = sb.ToString() };
    }
}
