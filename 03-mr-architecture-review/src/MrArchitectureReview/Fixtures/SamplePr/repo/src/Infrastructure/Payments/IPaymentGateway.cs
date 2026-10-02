namespace Contoso.Shop.Infrastructure.Payments;

/// <summary>
/// One implementation per payment provider, registered as a keyed service:
/// services.AddKeyedScoped&lt;IPaymentGateway, StripeGateway&gt;("stripe");
/// </summary>
public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(string orderId, decimal amount, string token, CancellationToken ct);
}

public sealed record PaymentResult(bool Succeeded, string? ProviderReference, string? Error);
