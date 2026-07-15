using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Compares record counts between the Regional (country) server and the
/// HO (OnPremBackup) server for a given country and date range.
/// </summary>
public class SyncDataCountService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 120;

    // Maps DataName (linked-server name) to TrfNo/ContNo prefix
    private static readonly Dictionary<string, string> DataNameToPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bflksa"]     = "SA",
        ["bflkuwait"]  = "KA",
        ["bflqatar"]   = "QR",
        ["bflmys"]     = "MY",
        ["bflbahrain"] = "BH",
        ["bfloman"]    = "OM",
    };

    // Maps DataName to the actual DB name on the country server
    private static readonly Dictionary<string, string> DataNameToDbName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bflksa"]     = "BFLKSA",
        ["bflkuwait"]  = "BFLKUWAIT",
        ["bflqatar"]   = "BFLQATAR",
        ["bflmys"]     = "BFLMYS",
        ["bflbahrain"] = "BFLBAHRAIN",
        ["bfloman"]    = "BFLOMAN",
    };

    private SqlConnection OpenOnPrem()
    {
        var b = new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    private SqlConnection OpenCountry(string country, string dataName)
    {
        var b = new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
            { InitialCatalog = dataName, ConnectTimeout = ConnectTimeoutSeconds };
        var c = new SqlConnection(b.ConnectionString);
        c.Open();
        return c;
    }

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = OpenOnPrem();
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT SIMCountry
              FROM BFLDATA.dbo.DataSettings
             WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
               AND SIMCountry <> 'UAE'
               AND DataName   IS NOT NULL AND LTRIM(RTRIM(DataName))   <> ''
             ORDER BY SIMCountry",
            commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<SyncRow>> GetCountsAsync(SyncFilter f, CancellationToken ct = default)
    {
        await using var onprem = OpenOnPrem();

        var dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, f.Country, ct);
        if (string.IsNullOrWhiteSpace(dataName))
            throw new InvalidOperationException(
                $"No DataName found for country '{f.Country}'.");

        if (!DataNameToPrefix.TryGetValue(dataName, out var prefix))
            throw new InvalidOperationException(
                $"No prefix mapping for DataName '{dataName}'.");

        var dbName   = DataNameToDbName.GetValueOrDefault(dataName, dataName.ToUpperInvariant());
        var isKsa    = string.Equals(f.Country, "KSA", StringComparison.OrdinalIgnoreCase);
        var from     = f.DateFrom.Date;
        var to       = f.DateTo.Date;

        await using var regional = OpenCountry(f.Country, dataName);

        var regionalTask = QueryRegionalAsync(regional, dbName, prefix, isKsa, from, to, ct);
        var hoTask       = QueryHoAsync(onprem, dataName, prefix, isKsa, from, to, ct);

        await Task.WhenAll(regionalTask, hoTask);

        var reg = regionalTask.Result;
        var ho  = hoTask.Result;

        var descriptions = new[]
        {
            "Transfer Header", "Transfer Detail", "RF Pair", "Sales Price",
            "Building Completion", "UPC Box", "Goods Issue Plt", "PLT Delivery",
            "Goods Issue", "GRN", "USA Org File", "USA Purchase",
            "PLT Issue", "Cont Receipt", "Cont Receipt Export",
        };

        return descriptions
            .Select(d => new SyncRow(d, reg.GetValueOrDefault(d), ho.GetValueOrDefault(d)))
            .ToList();
    }

    private async Task<Dictionary<string, int>> QueryRegionalAsync(
        SqlConnection conn, string dbName, string prefix, bool isKsa,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@from",   from);
        p.Add("@to",     to);
        p.Add("@prefix", prefix + "%");
        p.Add("@cPrefix", prefix + "%");

        var results = new Dictionary<string, int>();

        // TransferHeader
        var trfSql = isKsa
            ? $"SELECT COUNT(TrfNo) FROM [{dbName}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%')"
            : $"SELECT COUNT(TrfNo) FROM [{dbName}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix";
        results["Transfer Header"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(trfSql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // TransferDetail
        var detSql = isKsa
            ? $"SELECT COUNT(TrfNo) FROM [{dbName}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dbName}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%'))"
            : $"SELECT COUNT(TrfNo) FROM [{dbName}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dbName}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix)";
        results["Transfer Detail"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(detSql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // RFPair
        results["RF Pair"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(SN) FROM [{dbName}]..rfpair WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to AND TrfNo LIKE @prefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // RFSalesPrice
        results["Sales Price"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(UPC) FROM [{dbName}]..RFSalesPrice WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // BuildingCompletion (BFLDATA cross-db on country server)
        results["Building Completion"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT ContNo) FROM BFLDATA..BuildingCompletion WITH(NOLOCK) WHERE ContNo LIKE @cPrefix AND CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // UpcBoxHead (usa cross-db on country server)
        var upcSql = isKsa
            ? "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND Remarks NOT LIKE '%KSA transfer AutoBox-Create %'"
            : "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to";
        results["UPC Box"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(upcSql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // vGoodsIssuePlt
        results["Goods Issue Plt"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(SrNo) FROM BFLDATA..vGoodsIssuePlt WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // PLTDeliveryHead
        results["PLT Delivery"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(SrNo) FROM BFLDATA..PLTDeliveryHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // GoodsIssueHead
        results["Goods Issue"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(Sn) FROM BFLDATA..GoodsIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // GRNHeaderRF
        results["GRN"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(1) FROM [{dbName}]..GRNHeaderRF WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // USAOrgFile
        results["USA Org File"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT ContNo) FROM usa..usaorgfile WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // USAPurchase
        results["USA Purchase"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT ContNo) FROM usa..USAPurchase WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // PLTIssueHead
        results["PLT Issue"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(SrNo) FROM BFLDATA..PLTIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // ContReceipt
        results["Cont Receipt"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA..ContReceipt WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND TCMNo LIKE @cPrefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // ContReceiptExport
        p.Add("@dbName", dbName);
        results["Cont Receipt Export"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA..ContReceiptExport WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND Country = @dbName",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return results;
    }

    private async Task<Dictionary<string, int>> QueryHoAsync(
        SqlConnection conn, string dataName, string prefix, bool isKsa,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var dn = dataName; // linked-server name e.g. "bflksa"
        var p  = new DynamicParameters();
        p.Add("@from",   from);
        p.Add("@to",     to);
        p.Add("@prefix", prefix + "%");
        p.Add("@cPrefix", prefix + "%");

        var dbName = DataNameToDbName.GetValueOrDefault(dataName, dataName.ToUpperInvariant());
        var results = new Dictionary<string, int>();

        // TransferHeader
        var trfSql = isKsa
            ? $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%')"
            : $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix";
        results["Transfer Header"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(trfSql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // TransferDetail
        var detSql = isKsa
            ? $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%'))"
            : $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix)";
        results["Transfer Detail"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(detSql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // RFPair
        results["RF Pair"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(SN) FROM [{dn}]..rfpair WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to AND TrfNo LIKE @prefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // RFSalesPrice
        results["Sales Price"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(UPC) FROM [{dn}]..RFSalesPrice WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // BuildingCompletion (BFLDATA local on OnPremBackup)
        results["Building Completion"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT ContNo) FROM BFLDATA.dbo.BuildingCompletion WITH(NOLOCK) WHERE ContNo LIKE @cPrefix AND CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // UpcBoxHead (usa linked server on OnPremBackup)
        var upcSql = isKsa
            ? "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND Remarks NOT LIKE '%KSA transfer AutoBox-Create %'"
            : "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to";
        results["UPC Box"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(upcSql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // vGoodsIssuePlt
        results["Goods Issue Plt"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(SrNo) FROM BFLDATA.dbo.vGoodsIssuePlt WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // PLTDeliveryHead
        results["PLT Delivery"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(SrNo) FROM BFLDATA.dbo.PLTDeliveryHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // GoodsIssueHead
        results["Goods Issue"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(Sn) FROM BFLDATA.dbo.GoodsIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // GRNHeaderRF
        results["GRN"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(1) FROM [{dn}]..GRNHeaderRF WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // USAOrgFile
        results["USA Org File"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT ContNo) FROM usa..usaorgfile WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // USAPurchase
        results["USA Purchase"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT ContNo) FROM usa..USAPurchase WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // PLTIssueHead
        results["PLT Issue"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(SrNo) FROM BFLDATA.dbo.PLTIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // ContReceipt
        results["Cont Receipt"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA.dbo.ContReceipt WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND TCMNo LIKE @cPrefix",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        // ContReceiptExport
        p.Add("@dbName", dbName);
        results["Cont Receipt Export"] = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA.dbo.ContReceiptExport WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND Country = @dbName",
            p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

        return results;
    }
}
