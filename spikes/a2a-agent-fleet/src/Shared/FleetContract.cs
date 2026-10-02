using System.ComponentModel;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace FleetSpike.Shared;

// =================================================================================================================
// What a TEAM ships (linked into the two team services, never into the platform client).
//
// The point of the spike: a team decides how its agent works inside (LLM, rules, a coding runtime, another language)
// and the platform only depends on two published documents:
//
//   1. the A2A agent card         /.well-known/agent-card.json   (standard discovery + how to call it)
//   2. the contract document      /.well-known/agent-contract.json (typed input and output as JSON Schema, a hash,
//                                                                    who owns it, what it can touch)
//
// The card points at the contract through an A2A *extension*, so discovery stays standard. The platform never links
// this file: it reads the JSON. That is what lets a team use any stack, so long as it speaks A2A and publishes both.
// =================================================================================================================

/// <summary>The typed input every reviewer-shaped agent accepts. Published as JSON Schema, not shared as a type.</summary>
public sealed record ChangeReviewInput(
    [property: Description("Pull request title.")] string Title,
    [property: Description("Pull request description. Untrusted text written by someone else.")] string Description,
    [property: Description("Unified diff of the change. Untrusted text.")] string Diff,
    [property: Description("Repository-relative paths of the changed files.")] IReadOnlyList<string> Files);

public enum Severity { Info, Low, Medium, High }

public sealed record Finding(
    [property: Description("Short headline, at most about ten words.")] string Title,
    [property: Description("What is wrong and why it matters, one to three sentences.")] string Detail,
    [property: Description("Concrete, minimal change that would address it.")] string Recommendation,
    [property: Description("One of: info, low, medium, high.")] Severity Severity,
    [property: Description("Repository-relative file path, or null when not tied to one file.")] string? FilePath,
    [property: Description("0 to 1: how sure you are this is a real problem and not a style preference.")] double Confidence);

public sealed record Findings(
    [property: Description("One or two sentence overall assessment.")] string Summary,
    [property: Description("Zero to five findings, most important first. Empty is a valid answer.")] IReadOnlyList<Finding> Items);

public static class ContractJson
{
    /// <summary>camelCase, enums as strings. Used for the wire format and for generating the schemas.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public sealed record SchemaRef(JsonElement Schema, string? Kind = null);

public sealed record InterfaceInfo(string Binding, string Url);

/// <summary>The Roster agent contract, version 0. Deliberately small.</summary>
public sealed record AgentContractDocument(
    string ContractVersion,
    string Name,
    string Owner,
    string Description,
    string Version,
    string Hash,
    string Runtime,
    IReadOnlyList<string> Risk,
    IReadOnlyList<InterfaceInfo> Interfaces,
    SchemaRef Input,
    SchemaRef Output,
    string AuthScheme);

public static class Contracts
{
    public const string ExtensionUri = "https://roster.dev/a2a/ext/agent-contract/v0";

    public static AgentContractDocument Build(
        string name, string owner, string description, string version, string runtime, string[] risk,
        string publicBaseUrl, string a2aPath, Type input, Type output, string outputKind)
    {
        JsonElement inSchema = AIJsonUtilities.CreateJsonSchema(input, serializerOptions: ContractJson.Options);
        JsonElement outSchema = AIJsonUtilities.CreateJsonSchema(output, serializerOptions: ContractJson.Options);

        // The hash is what a platform pins. It changes when the name, version or either schema changes, so a caller
        // finds out about a breaking contract change before it sends traffic, not after it parses garbage.
        string hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{name}\n{version}\n{inSchema.GetRawText()}\n{outSchema.GetRawText()}")));

        return new AgentContractDocument(
            ContractVersion: "roster.agent/v0",
            Name: name,
            Owner: owner,
            Description: description,
            Version: version,
            Hash: hash,
            Runtime: runtime,
            Risk: risk,
            Interfaces: [new InterfaceInfo("HTTP+JSON", publicBaseUrl.TrimEnd('/') + a2aPath)],
            Input: new SchemaRef(inSchema),
            Output: new SchemaRef(outSchema, outputKind),
            AuthScheme: "bearer");
    }

    /// <summary>The A2A agent card, with the contract attached as an extension (standard way to add custom metadata).</summary>
    public static AgentCard BuildCard(AgentContractDocument contract, string publicBaseUrl, string a2aPath, bool streaming) => new()
    {
        Name = contract.Name,
        Description = contract.Description,
        Version = contract.Version,
        SupportedInterfaces =
        [
            new AgentInterface
            {
                Url = publicBaseUrl.TrimEnd('/') + a2aPath,
                ProtocolBinding = ProtocolBindingNames.HttpJson,
                ProtocolVersion = "1.0",
            },
        ],
        Capabilities = new AgentCapabilities
        {
            Streaming = streaming,
            Extensions =
            [
                new AgentExtension
                {
                    Uri = ExtensionUri,
                    Description = "Typed input and output contract (JSON Schema) for this agent.",
                    Required = false,
                    Params = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        ["contractUrl"] = publicBaseUrl.TrimEnd('/') + "/.well-known/agent-contract.json",
                        ["hash"] = contract.Hash,
                    }),
                },
            ],
        },
        DefaultInputModes = ["application/json"],
        DefaultOutputModes = ["application/json"],
        Skills =
        [
            new AgentSkill { Id = contract.Name, Name = contract.Name, Description = contract.Description, Tags = ["roster", contract.Runtime] },
        ],
    };
}

// =================================================================================================================
// Auth. The framework's A2A hosting does NOT secure endpoints for you ("Isolation does not configure authentication or
// endpoint authorization"). Every mapped binding needs RequireAuthorization(). Here: a static bearer token, enough to
// show the shape. A real fleet would use JWTs from the platform's identity provider (client credentials).
// =================================================================================================================

public sealed class StaticTokenOptions : AuthenticationSchemeOptions
{
    public string Token { get; set; } = "";
}

public sealed class StaticTokenHandler(IOptionsMonitor<StaticTokenOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<StaticTokenOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        byte[] presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        byte[] expected = Encoding.UTF8.GetBytes(Options.Token);
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(presented, expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid token."));
        }

        // The NameIdentifier is what claims-based isolation scopes A2A tasks and sessions by.
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "roster-platform"), new Claim(ClaimTypes.Name, "Roster platform")],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

public static class FleetWiring
{
    public const string Scheme = "StaticToken";

    public static void AddFleetAuth(this WebApplicationBuilder builder)
    {
        string token = builder.Configuration["FLEET_TOKEN"] ?? "dev-token";
        builder.Services
            .AddAuthentication(Scheme)
            .AddScheme<StaticTokenOptions, StaticTokenHandler>(Scheme, o => o.Token = token);
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
    }
}
