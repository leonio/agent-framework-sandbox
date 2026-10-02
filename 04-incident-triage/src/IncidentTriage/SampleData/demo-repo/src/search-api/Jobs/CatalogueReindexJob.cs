using StackExchange.Redis;

namespace Search.Jobs;

public sealed class CatalogueReindexJob(IServer redisServer, IProductSearch search)
{
    public async Task RunAsync(CancellationToken ct)
    {
        // Clear the product cache so stale prices are never served after the reindex.
        await redisServer.FlushDatabaseAsync();
        await search.ReindexAllAsync(ct);
    }
}
