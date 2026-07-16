using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public class SyncDataCountService(IOnPremConnectionResolver resolver)
{
    private const int ConnectTimeoutSeconds = 30;
    private const int CommandTimeoutSeconds = 60;

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

    private string OnPremCs() =>
        new SqlConnectionStringBuilder(resolver.GetOnPremBackupConnectionString())
            { ConnectTimeout = ConnectTimeoutSeconds }.ConnectionString;

    private string CountryCs(string country, string dataName) =>
        new SqlConnectionStringBuilder(resolver.GetCountryConnectionString(country))
            { InitialCatalog = dataName, ConnectTimeout = ConnectTimeoutSeconds }.ConnectionString;

    public static readonly string[] AllCountries = ["KSA", "Qatar", "Bahrain", "Kuwait", "Malaysia"];

    // Active countries — only these are queried; others show blanks
    private static readonly HashSet<string> ActiveCountries = new(["KSA"], StringComparer.OrdinalIgnoreCase);

    public async Task<List<SyncRowMulti>> GetMultiCountryAsync(DateTime date, CancellationToken ct = default)
    {
        // Run each active country in parallel; inactive ones get empty counts
        var countryTasks = AllCountries.Select(async country =>
        {
            if (!ActiveCountries.Contains(country))
                return (country, Descriptions.Select(d => (d, new CountryCount(null, null, null, null))).ToList());

            // Resolve dataName for this country
            string? dataName = null;
            string? prefix   = null;
            string  dbName   = country;
            bool    isKsa    = string.Equals(country, "KSA", StringComparison.OrdinalIgnoreCase);

            try
            {
                await using var setup = new SqlConnection(OnPremCs());
                await setup.OpenAsync(ct);
                dataName = await WhBoxItemsSource.ResolveDataNameAsync(setup, country, ct);
            }
            catch (Exception ex)
            {
                var err = $"Setup: {ex.Message}";
                return (country, Descriptions.Select(d => (d, new CountryCount(null, err, null, err))).ToList());
            }

            if (string.IsNullOrWhiteSpace(dataName) || !DataNameToPrefix.TryGetValue(dataName, out prefix))
            {
                var err = $"No config for '{country}'";
                return (country, Descriptions.Select(d => (d, new CountryCount(null, err, null, err))).ToList());
            }

            dbName = DataNameToDbName.GetValueOrDefault(dataName, dataName.ToUpperInvariant());
            var onpremCs  = OnPremCs();
            var countryCs = CountryCs(country, dataName);

            var rowTasks = Descriptions.Select(async desc =>
            {
                var regTask = QueryOneAsync(desc, countryCs, dbName, dataName, prefix, isKsa, date.Date, date.Date, isHo: false, ct);
                var hoTask  = QueryOneAsync(desc, onpremCs,  dbName, dataName, prefix, isKsa, date.Date, date.Date, isHo: true,  ct);
                await Task.WhenAll(regTask, hoTask);
                return (desc, new CountryCount(regTask.Result.Count, regTask.Result.Error, hoTask.Result.Count, hoTask.Result.Error));
            });

            return (country, (await Task.WhenAll(rowTasks)).ToList());
        });

        var countryResults = await Task.WhenAll(countryTasks);

        // Pivot: description → country → CountryCount
        return Descriptions.Select((desc, i) => new SyncRowMulti(
            desc,
            countryResults.ToDictionary(
                cr => cr.country,
                cr => cr.Item2.First(r => r.Item1 == desc).Item2,
                StringComparer.OrdinalIgnoreCase)
        )).ToList();
    }

    public static List<SyncRow> AllErrorRows(string msg) =>
        Descriptions.Select(d => new SyncRow(d, null, null, msg, msg)).ToList();

    public async Task<List<string>> GetCountriesAsync(CancellationToken ct = default)
    {
        await using var c = new SqlConnection(OnPremCs());
        await c.OpenAsync(ct);
        var rows = await c.QueryAsync<string>(new CommandDefinition(@"
            SELECT DISTINCT SIMCountry
              FROM BFLDATA.dbo.DataSettings
             WHERE SIMCountry IS NOT NULL AND LTRIM(RTRIM(SIMCountry)) <> ''
               AND SIMCountry <> 'UAE'
               AND DataName   IS NOT NULL AND LTRIM(RTRIM(DataName))   <> ''
             ORDER BY SIMCountry",
            commandTimeout: 30, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<List<SyncRow>> GetCountsAsync(SyncFilter f, CancellationToken ct = default)
    {
        string? dataName;
        string? prefix;
        string  dbName;
        bool    isKsa = string.Equals(f.Country, "KSA", StringComparison.OrdinalIgnoreCase);
        var     from  = f.DateFrom.Date;
        var     to    = f.DateTo.Date;

        try
        {
            await using var setup = new SqlConnection(OnPremCs());
            await setup.OpenAsync(ct);
            dataName = await WhBoxItemsSource.ResolveDataNameAsync(setup, f.Country, ct);
        }
        catch (Exception ex)
        {
            return AllErrorRows($"OnPremBackup connection failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(dataName))
            return AllErrorRows($"No DataName found for '{f.Country}' in BFLDATA.dbo.DataSettings.");

        if (!DataNameToPrefix.TryGetValue(dataName, out prefix))
            return AllErrorRows($"No prefix mapping for DataName '{dataName}'.");

        dbName = DataNameToDbName.GetValueOrDefault(dataName, dataName.ToUpperInvariant());

        string onpremCs;
        string countryCs;
        try { onpremCs  = OnPremCs(); }
        catch (Exception ex) { return AllErrorRows($"OnPremBackup CS error: {ex.Message}"); }
        try { countryCs = CountryCs(f.Country, dataName); }
        catch (Exception ex) { return AllErrorRows($"Country CS error: {ex.Message}"); }

        var tasks = Descriptions.Select(desc =>
            RunBothAsync(desc, onpremCs, countryCs, dbName, dataName, prefix, isKsa, from, to, ct)).ToList();

        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<SyncRow> RunBothAsync(
        string desc, string onpremCs, string countryCs,
        string dbName, string dataName, string prefix, bool isKsa,
        DateTime from, DateTime to, CancellationToken ct)
    {
        var regTask = QueryOneAsync(desc, countryCs, dbName, dataName, prefix, isKsa, from, to, isHo: false, ct);
        var hoTask  = QueryOneAsync(desc, onpremCs,  dbName, dataName, prefix, isKsa, from, to, isHo: true,  ct);
        await Task.WhenAll(regTask, hoTask);
        var (rCount, rErr) = regTask.Result;
        var (hCount, hErr) = hoTask.Result;
        return new SyncRow(desc, rCount, hCount, rErr, hErr);
    }

    private static async Task<(int? Count, string? Error)> QueryOneAsync(
        string desc, string cs,
        string dbName, string dataName, string prefix, bool isKsa,
        DateTime from, DateTime to, bool isHo, CancellationToken ct)
    {
        var dn = dataName;
        var p  = new DynamicParameters();
        p.Add("@from",    from);
        p.Add("@to",      to);
        p.Add("@prefix",  prefix + "%");
        p.Add("@cPrefix", prefix + "%");
        p.Add("@dbName",  dbName);

        var sql = (desc, isHo) switch
        {
            ("Transfer Header", false) when isKsa =>
                "SELECT COUNT(TrfNo) FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%')",
            ("Transfer Header", false) =>
                "SELECT COUNT(TrfNo) FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND TrfNo LIKE @prefix",
            ("Transfer Header", true) when isKsa =>
                $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%')",
            ("Transfer Header", true) =>
                $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND TrfNo LIKE @prefix",

            ("Transfer Detail", false) when isKsa =>
                "SELECT COUNT(TrfNo) FROM TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%'))",
            ("Transfer Detail", false) =>
                "SELECT COUNT(TrfNo) FROM TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND TrfNo LIKE @prefix)",
            ("Transfer Detail", true) when isKsa =>
                $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND (TrfNo LIKE 'SN%' OR TrfNo LIKE 'SR%' OR TrfNo LIKE 'SP%'))",
            ("Transfer Detail", true) =>
                $"SELECT COUNT(TrfNo) FROM [{dn}]..TransferDetail WITH(NOLOCK) WHERE TrfNo IN (SELECT TrfNo FROM [{dn}]..TransferHeader WITH(NOLOCK) WHERE CAST(TrfDate AS DATE) BETWEEN @from AND @to AND TrfNo LIKE @prefix)",

            ("RF Pair", false) =>
                "SELECT COUNT(SN) FROM rfpair WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to AND TrfNo LIKE @prefix",
            ("RF Pair", true) =>
                $"SELECT COUNT(SN) FROM [{dn}]..rfpair WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to AND TrfNo LIKE @prefix",

            ("Sales Price", false) =>
                "SELECT COUNT(UPC) FROM RFSalesPrice WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to",
            ("Sales Price", true) =>
                $"SELECT COUNT(UPC) FROM [{dn}]..RFSalesPrice WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to",

            ("Building Completion", false) =>
                "SELECT COUNT(DISTINCT ContNo) FROM BFLDATA..BuildingCompletion WITH(NOLOCK) WHERE ContNo LIKE @cPrefix AND CAST(TrnDate AS DATE) BETWEEN @from AND @to",
            ("Building Completion", true) =>
                "SELECT COUNT(DISTINCT ContNo) FROM BFLDATA.dbo.BuildingCompletion WITH(NOLOCK) WHERE ContNo LIKE @cPrefix AND CAST(TrnDate AS DATE) BETWEEN @from AND @to",

            ("UPC Box", false) when isKsa =>
                "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to AND Remarks NOT LIKE '%KSA transfer AutoBox-Create %'",
            ("UPC Box", false) =>
                "SELECT COUNT(boxno) FROM usa..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to",
            ("UPC Box", true) when isKsa =>
                $"SELECT COUNT(boxno) FROM [{dn}]..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to AND Remarks NOT LIKE '%KSA transfer AutoBox-Create %'",
            ("UPC Box", true) =>
                $"SELECT COUNT(boxno) FROM [{dn}]..upcboxhead WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to",

            ("Goods Issue Plt", false) =>
                "SELECT COUNT(SrNo) FROM BFLDATA..vGoodsIssuePlt WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",
            ("Goods Issue Plt", true) =>
                $"SELECT COUNT(SrNo) FROM [{dn}]..vGoodsIssuePlt WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",

            ("PLT Delivery", false) =>
                "SELECT COUNT(SrNo) FROM BFLDATA..PLTDeliveryHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",
            ("PLT Delivery", true) =>
                $"SELECT COUNT(SrNo) FROM [{dn}]..PLTDeliveryHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",

            ("Goods Issue", false) =>
                "SELECT COUNT(Sn) FROM BFLDATA..GoodsIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",
            ("Goods Issue", true) =>
                $"SELECT COUNT(Sn) FROM [{dn}]..GoodsIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",

            ("GRN", false) =>
                "SELECT COUNT(1) FROM GRNHeaderRF WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",
            ("GRN", true) =>
                $"SELECT COUNT(1) FROM [{dn}]..GRNHeaderRF WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",

            ("USA Org File", false) =>
                "SELECT COUNT(DISTINCT ContNo) FROM usa..usaorgfile WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to AND ContNo LIKE @cPrefix",
            ("USA Org File", true) =>
                "SELECT COUNT(DISTINCT ContNo) FROM usa..usaorgfile WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to AND ContNo LIKE @cPrefix",

            ("USA Purchase", false) =>
                "SELECT COUNT(DISTINCT ContNo) FROM usa..USAPurchase WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to AND ContNo LIKE @cPrefix",
            ("USA Purchase", true) =>
                "SELECT COUNT(DISTINCT ContNo) FROM usa..USAPurchase WITH(NOLOCK) WHERE CAST(TrnDate AS DATE) BETWEEN @from AND @to AND ContNo LIKE @cPrefix",

            ("PLT Issue", false) =>
                "SELECT COUNT(SrNo) FROM BFLDATA..PLTIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",
            ("PLT Issue", true) =>
                $"SELECT COUNT(SrNo) FROM [{dn}]..PLTIssueHead WITH(NOLOCK) WHERE CAST(EntryDate AS DATE) BETWEEN @from AND @to",

            ("Cont Receipt", false) =>
                "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA..ContReceipt WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) BETWEEN @from AND @to AND TCMNo LIKE @cPrefix",
            ("Cont Receipt", true) =>
                "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA.dbo.ContReceipt WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) BETWEEN @from AND @to AND TCMNo LIKE @cPrefix",

            ("Cont Receipt Export", false) =>
                "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA..ContReceiptExport WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) BETWEEN @from AND @to AND Country = @dbName",
            ("Cont Receipt Export", true) =>
                "SELECT COUNT(DISTINCT TCMNo) FROM BFLDATA.dbo.ContReceiptExport WITH(NOLOCK) WHERE CAST(ReceiptDt AS DATE) BETWEEN @from AND @to AND Country = @dbName",

            _ => null
        };

        if (sql is null) return (null, "No query defined");

        try
        {
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            var count = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(sql, p, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
            return (count, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
