namespace Wms.Data.Lpm;

/// <summary>One row of LPMSIM.dbo.UserWHDetail scoped to Warehouse = 'Online' -- backs
/// the "Online WH Users Add/Remove" grid on the Ecom Production Reports page.</summary>
public sealed record OnlineWhUserRow(
    string   Empcode,
    string   UserName,
    string   FullName,
    bool     Active,
    string?  AddedUser,
    DateTime CreateTs);

/// <summary>One (Date, Empcode) row of YOTO box-prep production from USA.dbo.VUPCBOXDET,
/// for an employee currently registered as an active Online WH user.</summary>
public sealed record EcomYotoProductionRow(
    DateTime TrnDate,
    string   Empcode,
    string?  UserName,
    string?  FullName,
    long     BoxCount,
    long     Qty);
