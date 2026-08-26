namespace Wms.Data.Lpm;

/// <summary>One row per RefNo group ("AEINT" = Total Imports, "AELOC" = Local Purchased).</summary>
public record YotoOffloadGroupRow(
    string Group,
    int    Containers,
    int    Pallets,
    int    Boxes
);

/// <summary>One row per RefNo group for containers received but not yet offloaded.</summary>
public record YotoPendingGroupRow(
    string Group,
    int    Containers,
    int    Qty // SUM() over vUSAOrder.Qty (an int column) stays int in SQL Server -- must match exactly for Dapper's record-constructor materialization
);

/// <summary>One row per period (month or in-month week) for the cumulative inbound summary.</summary>
public record YotoInboundPeriodRow(
    string PeriodLabel,
    int    PeriodIndex, // month number (1-12), or week number within the selected month (1-5)
    int    Containers,
    int    Pallets,
    int    Boxes,
    int    Pcs // same int-not-long reasoning as YotoPendingGroupRow.Qty
);

/// <summary>One period's (month or in-month week) totals within an Internal Transfer Summary box.</summary>
public record YotoInternalTransferPeriodRow(
    string PeriodLabel,
    int    PeriodIndex,
    int    Trips,
    int    Pallets,
    int    Boxes,
    int    Quantity
);

/// <summary>
/// One "box" of the Internal Transfer Summary (bfldata..vPLTDeliveryDetails) --
/// e.g. "Inbound from JAFZA" or "Outbound to Online" -- spanning every month of the
/// selected year, or every week of the selected month. CountLabel varies per box
/// ("NO. OF CONTAINERS" / "NO. OF TRAILERS" / "NO. OF GIN") even though Trips is
/// always the same underlying COUNT(DISTINCT trailerno). Warehouse is the partner
/// warehouse on the non-YOTO side ("JAFZA"/"TECHNO"/"ONLINE"), null for the two
/// Total Inbound/Outbound boxes which aren't tied to one specific partner.
/// </summary>
public record YotoInternalTransferBox(
    string Label,
    string CountLabel,
    string? Warehouse,
    List<YotoInternalTransferPeriodRow> Periods
);
