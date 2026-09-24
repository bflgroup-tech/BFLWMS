using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Api;

public record StoreStocktakeSubmitResult(string? Error);

public class StoreStocktakeService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 30;

    private record StoreLookupRow(string? DataName);

    /// <summary>Resolves a store's DataName from BFLDATA.dbo.DataSettings on
    /// OnPremBackup, keyed by StoreID — same source/validation store/grn uses.</summary>
    private async Task<StoreLookupRow?> ResolveStoreAsync(string storeId, CancellationToken ct)
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        await using var conn = new SqlConnection(b.ConnectionString);
        await conn.OpenAsync(ct);

        return await conn.QuerySingleOrDefaultAsync<StoreLookupRow>(new CommandDefinition(@"
            SELECT DataName
              FROM BFLDATA.dbo.DataSettings
             WHERE StoreID = @storeId",
            new { storeId }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // Same server the GRN legacy tables live on — DataSettings itself stays on
    // OnPremBackup either way, only this write target is AwsBflShopDb.
    private SqlConnection OpenAwsBflShopDb()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetAwsBflShopDbConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        return new SqlConnection(b.ConnectionString);
    }

    /// <summary>Writes the submitted stocktake into the store's own
    /// stocktaking_report table — one row per item, header fields (StockCountID,
    /// StoreId, Trndate) repeated on every row since it's a flat table, unlike
    /// the GRN legacy tables' header/detail split.</summary>
    public async Task<StoreStocktakeSubmitResult> SubmitAsync(StoreStocktakeRequest req, CancellationToken ct = default)
    {
        var store = await ResolveStoreAsync(req.StoreId, ct);
        if (store is null)
            return new StoreStocktakeSubmitResult($"Unknown storeId '{req.StoreId}' — not found in BFLDATA.dbo.DataSettings.");

        await using var conn = OpenAwsBflShopDb();
        await conn.OpenAsync(ct);
        var tablePrefix = $"[{store.DataName}]..";

        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($@"
            SELECT TOP 1 1
              FROM {tablePrefix}stocktaking_report
             WHERE StockCountID = @stockCountId",
            new { stockCountId = req.StockCountId }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        if (exists is not null)
            return new StoreStocktakeSubmitResult($"StockCountID '{req.StockCountId}' already exists.");

        foreach (var item in req.Items)
        {
            await conn.ExecuteAsync(new CommandDefinition($@"
                INSERT INTO {tablePrefix}stocktaking_report
                    (StockCountID, StoreId, Trndate, itemcode, ean, quantity, soh, variance)
                VALUES (@stockCountId, @storeId, @trnDate, @itemCode, @ean, @quantity, @soh, @variance)",
                new
                {
                    stockCountId = req.StockCountId,
                    storeId = req.StoreId,
                    trnDate = req.TrnDate,
                    itemCode = item.ItemCode,
                    ean = item.Ean,
                    quantity = item.Quantity,
                    soh = item.Soh,
                    variance = item.Variance,
                },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        return new StoreStocktakeSubmitResult(null);
    }

    /// <summary>Writes submitted stocktake scan results into the store's own
    /// stocktaking_result table — one row per item, header fields (StockCountID,
    /// StoreId, Trndate, Time1, username) repeated on every row, same flat-table
    /// shape as stocktaking_report. Trndate/Time1 split the request's single
    /// trnDate into its date and time-of-day parts.</summary>
    public async Task<StoreStocktakeSubmitResult> SubmitResultAsync(StoreStocktakeResultRequest req, CancellationToken ct = default)
    {
        var store = await ResolveStoreAsync(req.StoreId, ct);
        if (store is null)
            return new StoreStocktakeSubmitResult($"Unknown storeId '{req.StoreId}' — not found in BFLDATA.dbo.DataSettings.");

        await using var conn = OpenAwsBflShopDb();
        await conn.OpenAsync(ct);
        var tablePrefix = $"[{store.DataName}]..";

        var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($@"
            SELECT TOP 1 1
              FROM {tablePrefix}stocktaking_result
             WHERE StockCountID = @stockCountId",
            new { stockCountId = req.StockCountId }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        if (exists is not null)
            return new StoreStocktakeSubmitResult($"StockCountID '{req.StockCountId}' already exists.");

        foreach (var item in req.Items)
        {
            await conn.ExecuteAsync(new CommandDefinition($@"
                INSERT INTO {tablePrefix}stocktaking_result
                    (StockCountID, StoreId, Trndate, Time1, username, itemcode, ean, QrCode, rfid, quantity)
                VALUES (@stockCountId, @storeId, @trndate, @time1, @username, @itemCode, @ean, @qrCode, @rfid, @quantity)",
                new
                {
                    stockCountId = req.StockCountId,
                    storeId = req.StoreId,
                    trndate = req.TrnDate.Date,
                    time1 = req.TrnDate.TimeOfDay,
                    username = req.Username,
                    itemCode = item.ItemCode,
                    ean = item.Ean,
                    qrCode = item.QrCode,
                    rfid = item.Rfid,
                    quantity = item.Quantity,
                },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        }

        return new StoreStocktakeSubmitResult(null);
    }
}
