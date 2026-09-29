using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed record TradingControlSettings(bool RequireManualOrderConfirmation, decimal MinimumConfirmationNotional = 0m)
{
    public bool RequiresConfirmation(decimal price, decimal quantity) =>
        RequireManualOrderConfirmation && price * quantity > MinimumConfirmationNotional;
}

public sealed class TradingControlSettingsService(TradingDbContext db)
{
    public async Task<TradingControlSettings> GetAsync(CancellationToken ct) =>
        await db.TradingControlSettings.AsNoTracking()
            .Where(x => x.Id == TradingControlSettingsEntity.SingletonId)
            .Select(x => new TradingControlSettings(x.RequireManualOrderConfirmation, x.MinimumConfirmationNotional))
            .SingleOrDefaultAsync(ct) ?? new(false);

    public async Task<TradingControlSettings> SaveAsync(TradingControlSettings settings, CancellationToken ct)
    {
        if (settings.MinimumConfirmationNotional < 0m)
            throw new TradingProblemException(422, "INVALID_CONFIRMATION_THRESHOLD", "Minimum confirmation amount must be zero or greater.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TradingControlSettings" ("Id", "RequireManualOrderConfirmation", "MinimumConfirmationNotional")
            VALUES ({TradingControlSettingsEntity.SingletonId}, {settings.RequireManualOrderConfirmation}, {settings.MinimumConfirmationNotional.ToString(System.Globalization.CultureInfo.InvariantCulture)})
            ON CONFLICT ("Id") DO UPDATE SET
                "RequireManualOrderConfirmation" = excluded."RequireManualOrderConfirmation",
                "MinimumConfirmationNotional" = excluded."MinimumConfirmationNotional";
            """, ct);
        return settings;
    }

}
