using System.Text;
using System.Text.RegularExpressions;
using CliDesignPipeline.Content;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CliDesignPipeline.Rag;

/// <summary>
/// A deliberately tiny Retrieval-Augmented Generation (RAG) hook: before every agent call, find
/// the instruction sections most relevant to what the agent is being asked and add them to the
/// system instructions for that call only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it plugs in.</b> <see cref="AIContextProvider"/> is the Agent Framework extension
/// point that runs before (and after) each agent invocation. Whatever it returns in
/// <see cref="AIContext"/> (extra instructions, messages or tools) is merged into that one
/// request. It is registered per agent via <c>ChatClientAgentOptions.AIContextProviders</c>,
/// so each agent can have its own knowledge sources.
/// </para>
/// <para>
/// <b>Why not just pin every document?</b> Pinning (see <c>instructions:</c> in the prompt front
/// matter) is right for the two or three rules an agent must <i>always</i> follow. Once the
/// corpus grows (all your ADRs, every Confluence page about the domain) pinning blows the
/// context window and dilutes attention; retrieval sends only what matters for this turn.
/// </para>
/// <para>
/// <b>Why keyword scoring and not embeddings?</b> So the sample runs offline with zero
/// infrastructure. The production shape swaps <see cref="Search"/> for a vector search and keeps
/// everything else:
/// </para>
/// <code>
/// // using Microsoft.Extensions.VectorData;  (+ a connector, e.g. Microsoft.SemanticKernel.Connectors.InMemory,
/// //                                            AzureAISearch, Qdrant, PgVector, SqlServer...)
/// // IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt; embedder = openAI.GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator();
/// // var collection = vectorStore.GetCollection&lt;string, InstructionChunk&gt;("instructions");
/// // await foreach (var hit in collection.SearchAsync(queryText, top: 3)) { ... }   // embeds the query for you
/// </code>
/// <para>
/// Other places in this pipeline where RAG <i>would</i> earn its keep:
/// </para>
/// <list type="bullet">
///   <item><b>Interviewer</b>: retrieve similar past specs so it asks the questions that bit
///         the team last time ("you forgot about time zones again").</item>
///   <item><b>Developer</b>: retrieve snippets from your internal libraries / templates so the
///         generated code uses your logging and config conventions, not generic ones.</item>
///   <item><b>Tester</b>: retrieve past review findings for similar code (a "lessons learned"
///         store fed by <see cref="AIContextProvider.StoreAIContextAsync"/> after each review).</item>
/// </list>
/// <para>
/// <b>Security note:</b> anything retrieved is injected into the model's instructions. Only
/// index sources you trust as much as your own prompts; treat wiki/ticket content as data
/// (put it in a user message, clearly delimited) rather than as instructions.
/// </para>
/// </remarks>
public sealed partial class InstructionsContextProvider(PromptLibrary library, IReadOnlyCollection<string> pinnedDocuments, int top = 2)
    : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Query = the text of what the agent is being asked right now.
        var query = string.Join(' ', context.AIContext.Messages?.Where(m => m.Role == ChatRole.User).Select(m => m.Text) ?? []);
        var hits = Search(query).ToList();

        if (hits is [])
        {
            return ValueTask.FromResult(new AIContext());
        }

        var sb = new StringBuilder("# Retrieved guidance (most relevant sections of the team's standing instructions)")
            .AppendLine().AppendLine();
        foreach (var hit in hits)
        {
            // Cite the source so the model (and a human reading logs) can tell where it came from.
            sb.AppendLine($"## {hit.Heading} _(from instructions/{hit.Document}.md)_").AppendLine(hit.Text).AppendLine();
        }

        return ValueTask.FromResult(new AIContext { Instructions = sb.ToString() });
    }

    /// <summary>Scores each section by overlapping terms. Swap for vector search in production.</summary>
    public IEnumerable<InstructionSection> Search(string query)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return [];
        }

        return library.Instructions
            .Where(doc => !pinnedDocuments.Contains(doc.Name)) // pinned docs are already in context
            .SelectMany(doc => doc.Sections)
            .Select(section => (section, score: Tokenize($"{section.Heading} {section.Heading} {section.Text}").Count(terms.Contains)))
            .Where(x => x.score >= 2)
            .OrderByDescending(x => x.score)
            .Take(top)
            .Select(x => x.section);
    }

    private static HashSet<string> Tokenize(string text) =>
        [.. WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length > 3 && !StopWords.Contains(w))];

    private static readonly HashSet<string> StopWords = ["this", "that", "with", "from", "have", "what", "when", "will", "your", "must", "should", "there", "their", "they", "about", "into", "only", "each", "every", "than", "then", "were"];

    // Source-generated regex: compiled at build time, no runtime Regex construction cost.
    [GeneratedRegex(@"[a-z][a-z0-9\-]+")]
    private static partial Regex WordRegex();
}
