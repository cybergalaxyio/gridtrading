using GridTrading.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class DecimalPersistenceTests
{
    [Fact]
    public async Task DecimalColumnsAcceptScientificNotationFromSqlite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;

        await using (var setup = new TradingDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync(ct);
            setup.Cycles.Add(new CycleEntity
            {
                Id = "cycle",
                StrategyId = "strategy",
                State = "RUNNING",
                FrozenConfigurationJson = "{}",
                FrozenPlanJson = "{}",
                ExitReason = ""
            });
            await setup.SaveChangesAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Cycles SET AccruedFunding = '-6E-06' WHERE Id = 'cycle'";
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var db = new TradingDbContext(options);
        var cycle = await db.Cycles.SingleAsync(ct);

        Assert.Equal(-0.000006m, cycle.AccruedFunding);
    }
}
