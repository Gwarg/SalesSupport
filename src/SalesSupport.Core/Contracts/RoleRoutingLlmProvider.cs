namespace SalesSupport.Core.Contracts;

/// <summary>
/// Routes each role to its own ILlmProvider — the D31 role-mix mechanism: a fast gate
/// model, a cheaper advisor, a thorough summarizer, freely across vendors. Providers
/// keep their own role→model mapping internally; this only picks which provider
/// handles which role.
/// </summary>
public sealed class RoleRoutingLlmProvider(IReadOnlyDictionary<LlmRole, ILlmProvider> routes) : ILlmProvider
{
    public Task<T> CompleteJsonAsync<T>(LlmRole role, LlmConversation conversation, CancellationToken ct = default)
        where T : class =>
        routes.TryGetValue(role, out var provider)
            ? provider.CompleteJsonAsync<T>(role, conversation, ct)
            : throw new InvalidOperationException($"No provider routed for role {role}.");
}
