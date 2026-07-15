using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

/// <summary>
/// Compares record counts between the Regional (country) server and the
/// HO (OnPremBackup/UAE) server for a given country and date range.
/// Each table query is independent — a failure on one row does not block others.
/// </summary>
public class SyncDataCountService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 60;
    private const int CommandTimeoutSeconds = 120;

    private static readonly Dictionary<string, string> DataNameToPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bflksa"]     = "SA",
        ["bflkuwait"]  = "KA",
        ["bflqatar"]   = "QR",
        ["bflmys"]     = "MY",
        ["bflbahrain"] = "BH",
        ["bfloman"]    = "OM",
    };

    private static readonly Dictionary<string, string> DataNameToDbName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bflksa"]     = "BFLKSA",
        ["bflkuwait"]  = "BFLKUWAIT",
        ["bflqatar"]   = "BFLQATAR",
        ["bflmys"]     = "BFLMYS",
        ["bflbahrain"] = "BFLBAHRAIN",
        ["bfloman"]    = "BFLOMAN",
    };

    private static readonly string[] Descriptions =
    {
        "Transfer Header", "Transfer Detail", "RF Pair", "Sales Price",
        "Building Completion", "UPC Box", "Goods Issue Plt", "PLT Delivery",
        "Goods Issue", "GRN", "USA Org File", "USA Purchase",
        "PLT Issue", "Cont Receipt", "Cont Receipt Export",
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
        using var c = OpenOnPrem();
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
        string? setupError = null;
        string? dataName   = null;
        string? prefix     = null;
        string  dbName     = f.Country.ToUpperInvariant();
        var     isKsa      = string.Equals(f.Country, "KSA", StringComparison.OrdinalIgnoreCase);
        var     from       = f.DateFrom.Date;
        var     to         = f.DateTo.Date;

        SqlConnection? onprem   = null;
        SqlConnection? regional = null;

        try
        {
            onprem   = OpenOnPrem();
            dataName = await WhBoxItemsSource.ResolveDataNameAsync(onprem, f.Country, ct);
            if (string.IsNullOrWhiteSpace(dataName))
                setupError = $"No DataName found for country '{f.Country}' in BFLDATA.dbo.DataSettings.";
            else if (!DataNameToPrefix.TryGetValue(dataName, out prefix))
                setupError = $"No prefix mapping for DataName '{dataName}'.";
            else
                dbName = DataNameToDbName.GetValueOrDefault(dataName, dataName.ToUpperInvariant());

            if (setupError is null)
                regional = OpenCountry(f.Country, dataName!);
        }
        catch (Exception ex)
        {
            setupError = ex.Message;
        }

        if (setupError is not null || regional is null || onprem is null)
        {
            // Return all rows with the setup error so the table is visible
            return Descriptions.Select(d => new SyncRow(d, null, null, setupError, setupError)).ToList();
        }

        try
        {
            var queries = Descriptions.Select(desc => RunBothAsync(
                desc, regional, onprem, dbName, dataName!, prefix!, isKsa, from, to, ct)).ToList();
            return (await Task.WhenAll(queries)).ToList();
        }
        finally
        {
            await regional.DisposeAsync();
            await onprem.DisposeAsync();
        }
    }

    private async Task<SyncRow> RunBothAsync(
        string desc, SqlConnection reg, SqlConnection ho,
        string dbName, string dataName, string prefix, bool isKsa,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var regTask = QueryOneAsync(desc, reg, dbName, dataName, prefix, isKsa, from, to, isHo: false, ct);
        var hoTask  = QueryOneAsync(desc, ho,  dbName, dataName, prefix, isKsa, from, to, isHo: true,  ct);
        await Task.WhenAll(regTask, hoTask);
        var (rCount, rErr) = regTask.Result;
        var (hCount, hErr) = hoTask.Result;
        return new SyncRow(desc, rCount, hCount, rErr, hErr);
    }

    private async Task<(int? Count, string? Error)> QueryOneAsync(
        string desc, SqlConnection conn,
        string dbName, string dataName, string prefix, bool isKsa,
        DateTime from, DateTime to, bool isHo, CancellationToken ct)
    {
        var dn = dataName; // linked-server name on HO (e.g. "bflksa")
        var p  = new DynamicParameters();
        p.Add("@from",   from);
        p.Add("@to",     to);
        p.Add("@prefix", prefix + "%");
        p.Add("@cPrefix", prefix + "%");
        p.Add("@dbName", dbName);

        try
        {
            var sql = (desc, isHo) switch
            {
                ("Transfer Header", false) when isKsa =>
                    $"SELECT COUNT(TrfNo) FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%')",
                ("Transfer Header", false) =>
                    $"SELECT COUNT(TrfNo) FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix",
                ("Transfer Header", true) when isKsa =>
                    $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%')",
                ("Transfer Header", true) =>
                    $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix",

                ("Transfer Detail", false) when isKsa =>
                    $"SELECT COUNT(TrfNo) FROM TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%'))",
                ("Transfer Detail", false) =>
                    $"SELECT COUNT(TrfNo) FROM TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix)",
                ("Transfer Detail", true) when isKsa =>
                    $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%'))",
                ("Transfer Detail", true) =>
                    $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) >= @from AND CAST(TrfDate AS DATE) <= @to AND TrfNo LIKE @prefix)",

                ("RF Pair", false) =>
                    $"SELECT COUNT(SN) FROM rfpair WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to AND TrfNo LIKE @prefix",
                ("RF Pair", true) =>
                    $"SELECT COUNT(SN) FROM [{dn}]..rfpair WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to AND TrfNo LIKE @prefix",

                ("Sales Price", false) =>
                    "SELECT COUNT(UPC) FROM RFSalesPrice WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",
                ("Sales Price", true) =>
                    $"SELECT COUNT(UPC) FROM [{dn}]..RFSalesPrice WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",

                ("Building Completion", false) =>
                    "SELECT COUNT(DISTINCT ContNo) FROM BFLDATA..BuildingCompletion WITH(NOLOCK) WHERE ContNo LIKE @cPrefix AND CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",
                ("Building Completion", true) =>
                    "SELECT COUNT(DISTINCT ContNo) FROM BFLDATA.dbo.BuildingCompletion WITH(NOLOCK) WHERE ContNo LIKE @cPrefix AND CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",

                ("UPC Box", false) when isKsa =>
                    "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND Remarks NOT LIKE '%KSA transfer AutoBox-Create %'",
                ("UPC Box", false) =>
                    "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",
                ("UPC Box", true) when isKsa =>
                    "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND Remarks NOT LIKE '%KSA transfer AutoBox-Create %'",
                ("UPC Box", true) =>
                    "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to",

                ("Goods Issue Plt", false) =>
                    "SELECT COUNT(SrNo) FROM BFLDATA..vGoodsIssuePlt WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
                ("Goods Issue Plt", true) =>
                    "SELECT COUNT(SrNo) FROM BFLDATA.dbo.vGoodsIssuePlt WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",

                ("PLT Delivery", false) =>
                    "SELECT COUNT(SrNo) FROM BFLDATA..PLTDeliveryHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
                ("PLT Delivery", true) =>
                    "SELECT COUNT(SrNo) FROM BFLDATA.dbo.PLTDeliveryHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",

                ("Goods Issue", false) =>
                    "SELECT COUNT(Sn) FROM BFLDATA..GoodsIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
                ("Goods Issue", true) =>
                    "SELECT COUNT(Sn) FROM BFLDATA.dbo.GoodsIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",

                ("GRN", false) =>
                    $"SELECT COUNT(1) FROM GRNHeaderRF WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
                ("GRN", true) =>
                    $"SELECT COUNT(1) FROM [{dn}]..GRNHeaderRF WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",

                ("USA Org File", false) =>
                    "SELECT COUNT(DISTINCT ContNo) FROM usa..usaorgfile WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",
                ("USA Org File", true) =>
                    "SELECT COUNT(DISTINCT ContNo) FROM usa..usaorgfile WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",

                ("USA Purchase", false) =>
                    "SELECT COUNT(DISTINCT ContNo) FROM usa..USAPurchase WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",
                ("USA Purchase", true) =>
                    "SELECT COUNT(DISTINCT ContNo) FROM usa..USAPurchase WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) >= @from AND CAST(TrnDate AS DATE) <= @to AND ContNo LIKE @cPrefix",

                ("PLT Issue", false) =>
                    "SELECT COUNT(SrNo) FROM BFLDATA..PLTIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",
                ("PLT Issue", true) =>
                    "SELECT COUNT(SrNo) FROM BFLDATA.dbo.PLTIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) >= @from AND CAST(EntryDate AS DATE) <= @to",

                ("Cont Receipt", false) =>
                    "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA..ContReceipt WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND TCMNo LIKE @cPrefix",
                ("Cont Receipt", true) =>
                    "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA.dbo.ContReceipt WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND TCMNo LIKE @cPrefix",

                ("Cont Receipt Export", false) =>
                    "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA..ContReceiptExport WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND Country = @dbName",
                ("Cont Receipt Export", true) =>
                    "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA.dbo.ContReceiptExport WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) >= @from AND CAST(ReceiptDt AS DATE) <= @to AND Country = @dbName",

                _ => null
            };

            if (sql is null) return (null, "No query defined");
            var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return (count, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
