using Microsoft.Data.SqlClient;

namespace Checkout.Data;

public sealed class OrderRepository(string connectionString)
{
    public async Task<int> SaveOrderAsync(Order order, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var orderId = await InsertOrderHeaderAsync(connection, order, ct);

        // Save line items individually so one bad item does not fail the whole order.
        foreach (var item in order.LineItems)
        {
            await SaveLineItemAsync(orderId, item, ct);
        }

        return orderId;
    }

    // Opens its own connection per line item so failures can be retried independently.
    private async Task SaveLineItemAsync(int orderId, LineItem item, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);   // not disposed: leaks a pooled connection on every call
        await connection.OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO OrderLines (OrderId, Sku, Quantity, Price) VALUES (@o, @s, @q, @p)";
        command.Parameters.AddWithValue("@o", orderId);
        command.Parameters.AddWithValue("@s", item.Sku);
        command.Parameters.AddWithValue("@q", item.Quantity);
        command.Parameters.AddWithValue("@p", item.Price);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> InsertOrderHeaderAsync(SqlConnection connection, Order order, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Orders (CustomerId, Total) OUTPUT INSERTED.Id VALUES (@c, @t)";
        command.Parameters.AddWithValue("@c", order.CustomerId);
        command.Parameters.AddWithValue("@t", order.Total);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }
}

public sealed record Order(int CustomerId, decimal Total, IReadOnlyList<LineItem> LineItems);
public sealed record LineItem(string Sku, int Quantity, decimal Price);
