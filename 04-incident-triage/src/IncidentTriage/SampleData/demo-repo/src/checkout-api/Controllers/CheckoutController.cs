using Checkout.Data;
using Checkout.Payments;

namespace Checkout.Controllers;

public static class CheckoutEndpoints
{
    public static void MapCheckout(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/checkout", async (CheckoutRequest request, OrderRepository orders, PaymentClient payments, CancellationToken ct) =>
        {
            // Order is saved before payment so the payment provider gets a stable order id.
            var orderId = await orders.SaveOrderAsync(request.ToOrder(), ct);
            var payment = await payments.AuthoriseAsync(request.IdempotencyKey, request.Total, ct);
            return payment.Authorised
                ? Results.Ok(new { orderId })
                : Results.Problem(payment.Error, statusCode: StatusCodes.Status502BadGateway);
        });
}
