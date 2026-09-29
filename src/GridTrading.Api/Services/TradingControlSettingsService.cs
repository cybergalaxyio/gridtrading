using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed record TradingControlSettings(bool RequireManualOrderConfirmation);

public sealed class TradingControlSettingsService(TradingDbContext db)
{
    public async Task<TradingControlSettings> GetAsync(CancellationToken ct) => new(
        await db.TradingControlSettings.AsNoTracking()
            .Where(x => x.Id == TradingControlSettingsEntity.SingletonId)
            .Select(x => x.RequireManualOrderConfirmation).SingleOrDefaultAsync(ct));

    public async Task<TradingControlSettings> SaveAsync(TradingControlSettings settings, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TradingControlSettings" ("Id", "RequireManualOrderConfirmation")
            VALUES ({TradingControlSettingsEntity.SingletonId}, {settings.RequireManualOrderConfirmation})
            ON CONFLICT ("Id") DO UPDATE SET
                "RequireManualOrderConfirmation" = excluded."RequireManualOrderConfirmation";
            """, ct);
        return settings;
    }

}
