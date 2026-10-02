using StackExchange.Redis;

namespace Search.Caching;

/// Read-through cache in front of Elasticsearch.
public sealed class ProductCache(IDatabase redis, IProductSearch search)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6); // every key gets the same TTL

    public async Task<Product?> GetAsync(string sku, CancellationToken ct)
    {
        var cached = await redis.StringGetAsync($"product:{sku}");
        if (cached.HasValue) return Product.Deserialize(cached!);

        // Cache miss: every concurrent request for the same sku goes to Elasticsearch (no request coalescing).
        var product = await search.GetBySkuAsync(sku, ct);
        if (product is not null) await redis.StringSetAsync($"product:{sku}", product.Serialize(), Ttl);
        return product;
    }
}
