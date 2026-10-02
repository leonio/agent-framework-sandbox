namespace Checkout.Payments;

public sealed class PaymentClient(HttpClient http)
{
    public async Task<PaymentResult> AuthoriseAsync(Guid idempotencyKey, decimal amount, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/authorisations")
        {
            Content = JsonContent.Create(new { amount, currency = "EUR" }),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());

        // Timeout raised to 10s in 1c88e41; retries handled by the Polly pipeline in Program.cs (3 attempts, exponential back-off).
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? new PaymentResult(true, null)
            : new PaymentResult(false, $"Provider returned {(int)response.StatusCode}");
    }
}

public sealed record PaymentResult(bool Authorised, string? Error);
