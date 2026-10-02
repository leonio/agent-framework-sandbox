using Microsoft.EntityFrameworkCore;
using SdlcStudio.Api.Data;
using Xunit;

namespace SdlcStudio.Tests;

/// <summary>
/// LocalDB only runs on Windows, so CI on Linux cannot create the real database. This test at least proves
/// the model is valid for the SQL Server provider by generating its CREATE script (no connection needed).
/// </summary>
public sealed class SqlServerModelTests
{
    [Fact]
    public void Model_generates_a_sql_server_schema()
    {
        var options = new DbContextOptionsBuilder<StudioDbContext>()
            .UseSqlServer(@"Server=(localdb)\MSSQLLocalDB;Database=SdlcStudio;Trusted_Connection=True")
            .Options;
        using var db = new StudioDbContext(options);

        var script = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [Runs]", script);
        Assert.Contains("datetimeoffset", script);          // native type on SQL Server (ticks only on SQLite)
        Assert.Contains("[Status] nvarchar(40)", script);   // enums stored as strings
    }
}
