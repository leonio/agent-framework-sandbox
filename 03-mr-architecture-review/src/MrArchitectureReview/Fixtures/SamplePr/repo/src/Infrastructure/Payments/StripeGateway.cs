namespace Contoso.Shop.Infrastructure.Payments;

public sealed class StripeGateway(HttpClient http) : IPaymentGateway
{
    public async Task<PaymentResult> ChargeAsync(string orderId, decimal amount, string token, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v1/charges",
            new { amount = (int)(amount * 100), currency = "gbp", source = token, metadata = new { orderId } }, ct);
        return response.IsSuccessStatusCode
            ? new PaymentResult(true, response.Headers.Location?.ToString(), null)
            : new PaymentResult(false, null, $"Stripe returned {(int)response.StatusCode}");
    }
}
