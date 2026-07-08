using Wms.Data.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Wms.Data.Lpm;

public record ShopDataNameRow(string? ShopName, string? DataName);

/// <summary>Backs the WH-Racks report. Reads the ShopName/DataName lookup
/// from dbo.WMS_DataSettings on the Azure WMS DB (mirrored from
/// bfldata.dbo.DataSettings by the Master Data Sync feature).</summary>
public class RacksService(IOnPremConnectionResolver resolver)
{
    private SqlConnection OpenWms()
    {
        var c = new SqlConnection(resolver.GetWmsAzureConnectionString());
        c.Open();
        return c;
    }

    public async Task<List<ShopDataNameRow>> GetShopDataNamesAsync(CancellationToken ct = default)
    {
        await using var conn = OpenWms();
        var rows = await conn.QueryAsync<ShopDataNameRow>(new CommandDefinition(@"
            SELECT ShopName, Dataname AS DataName
              FROM dbo.WMS_DataSettings WITH (NOLOCK)
             ORDER BY ShopName", cancellationToken: ct));
        return rows.ToList();
    }
}
