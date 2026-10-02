namespace Contoso.Shop.Application.Orders;

public sealed class OrderService(IOrderRepository orders)
{
    public Task<Order?> FindAsync(string id, CancellationToken ct) => orders.FindAsync(id, ct);

    public async Task MarkPaidAsync(string id, string providerReference, CancellationToken ct)
    {
        var order = await orders.FindAsync(id, ct) ?? throw new KeyNotFoundException(id);
        await orders.SaveAsync(order with { Status = OrderStatus.Paid, PaymentReference = providerReference }, ct);
    }
}
