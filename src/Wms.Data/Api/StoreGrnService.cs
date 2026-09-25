using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Wms.Data.Configuration;

namespace Wms.Data.Api;

public record StoreGrnSubmitResult(string? Error);

public class StoreGrnService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 30;

    private record StoreLookupRow(string? DataName, string? CostCodeTo, string? LocCodeTo, string? ShopLetter);

    /// <summary>Resolves a store's DataName/CostCodeTo/LocCodeTo/ShopLetter from
    /// BFLDATA.dbo.DataSettings on OnPremBackup, keyed by RMSStoreID — the caller's
    /// storeId is matched against RMSStoreID rather than StoreID.</summary>
    private async Task<StoreLookupRow?> ResolveStoreAsync(string storeId, CancellationToken ct)
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        await using var conn = new SqlConnection(b.ConnectionString);
        await conn.OpenAsync(ct);

        return await conn.QuerySingleOrDefaultAsync<StoreLookupRow>(new CommandDefinition(@"
            SELECT DataName, CostCodeTo, LocCodeTo, ShopLetter
              FROM BFLDATA.dbo.DataSettings
             WHERE RMSStoreID = @storeId",
            new { storeId }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
    }

    // A store's GRNHeaderRf_test/GRNDetailRf_test live on the AWS RDS shop DB
    // server, not WmsProductionDb/OnPremBackup — DataSettings itself (the lookup
    // above) stays on OnPremBackup either way, only this write target moves.
    private SqlConnection OpenAwsBflShopDb()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetAwsBflShopDbConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        return new SqlConnection(b.ConnectionString);
    }

    /// <summary>Checks whether any of the request's transfers already has a
    /// GRNHeaderRf_test row for this store (TrfNo + CostCode) — a store re-sending
    /// the same transfer is the most likely real-world cause, so this is checked
    /// up front, before anything is written anywhere.</summary>
    private async Task<string?> FindDuplicateTransferAsync(StoreGrnRequest req, StoreLookupRow store, CancellationToken ct)
    {
        await using var conn = OpenAwsBflShopDb();
        await conn.OpenAsync(ct);
        var tablePrefix = $"[{store.DataName}]..";

        foreach (var transfer in req.Transfers)
        {
            var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($@"
                SELECT TOP 1 1
                  FROM {tablePrefix}GRNHeaderRf_test
                 WHERE TrfNo = @trfNo AND CostCode = @costCode",
                new { trfNo = transfer.TrfNo, costCode = store.CostCodeTo },
                commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            if (exists is not null)
                return $"Transfer No '{transfer.TrfNo}' already exists.";
        }

        return null;
    }

    /// <summary>Mirrors the submitted GRN into the store's own legacy on-prem tables
    /// (GRNHeaderRf_test / GRNDetailRf_test) — one header row per transfer, one detail
    /// row per item line, all sharing that transfer's generated EntryNo.
    ///
    /// EntryNo format: {ShopLetter}O{YY}{00001} — e.g. "JO2600001". The 5-digit
    /// sequence is next-highest for that ShopLetter+year prefix in GRNHeaderRf_test,
    /// read with an UPDLOCK/HOLDLOCK table hint inside a serializable transaction (same
    /// locked-read-then-insert approach BuildingService's WmsBoxSequence uses) so two
    /// concurrent submissions for the same store never collide on the same EntryNo.
    /// </summary>
    private async Task PushToLegacyGrnTablesAsync(StoreGrnRequest req, StoreLookupRow store, CancellationToken ct)
    {
        await using var conn = OpenAwsBflShopDb();
        await conn.OpenAsync(ct);

        var tablePrefix = $"[{store.DataName}]..";

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var yy = (DateTime.Now.Year % 100).ToString("D2");
            var prefix = $"{store.ShopLetter}O{yy}";

            foreach (var transfer in req.Transfers)
            {
                var maxSeq = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($@"
                    SELECT MAX(CAST(RIGHT(EntryNo, 5) AS INT))
                      FROM {tablePrefix}GRNHeaderRf_test WITH (UPDLOCK, HOLDLOCK)
                     WHERE EntryNo LIKE @prefix + '%' AND LEN(EntryNo) = LEN(@prefix) + 5",
                    new { prefix }, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                var entryNo = prefix + ((maxSeq ?? 0) + 1).ToString("D5");

                await conn.ExecuteAsync(new CommandDefinition($@"
                    INSERT INTO {tablePrefix}GRNHeaderRf_test (EntryNo, EntryDate, UserId, TrfNo, CostCode)
                    VALUES (@entryNo, @entryDate, @userId, @trfNo, @costCode)",
                    new
                    {
                        entryNo,
                        entryDate = req.DateTime,
                        userId = req.User,
                        trfNo = transfer.TrfNo,
                        costCode = store.CostCodeTo,
                    },
                    transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

                foreach (var item in transfer.Items)
                {
                    const int scanQty = 1;
                    await conn.ExecuteAsync(new CommandDefinition($@"
                        INSERT INTO {tablePrefix}GRNDetailRf_test
                            (EntryNo, GINNo, TrfNo, Itemcode, RfId, ScanMode, TrfQty, ScanQty, Diff, SerializedCode)
                        VALUES (@entryNo, @ginNo, @trfNo, @itemCode, @rfId, @scanMode, @trfQty, @scanQty, @diff, @serializedCode)",
                        new
                        {
                            entryNo,
                            ginNo = req.GinNo,
                            trfNo = transfer.TrfNo,
                            itemCode = item.ItemCode,
                            rfId = item.Epc,
                            scanMode = "",
                            trfQty = item.TrfQty,
                            scanQty,
                            diff = scanQty - item.TrfQty,
                            serializedCode = item.QrCode,
                        },
                        transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<StoreGrnSubmitResult> SubmitAsync(StoreGrnRequest req, CancellationToken ct = default)
    {
        var store = await ResolveStoreAsync(req.StoreId, ct);
        if (store is null)
            return new StoreGrnSubmitResult($"Unknown storeId '{req.StoreId}' — not found in BFLDATA.dbo.DataSettings.");

        var duplicateError = await FindDuplicateTransferAsync(req, store, ct);
        if (duplicateError is not null)
            return new StoreGrnSubmitResult(duplicateError);

        await PushToLegacyGrnTablesAsync(req, store, ct);

        return new StoreGrnSubmitResult(null);
    }
}
