namespace Wms.Core;

/// <summary>
/// Menu key constants — one per nav item that participates in the
/// per-user menu-access grants. Mirrored in [Authorize(Policy=…)]
/// on each page and in NavMenu's per-item AuthorizeView.
/// </summary>
public static class MenuKeys
{
    public const string PENDING_FOR_COUNTING      = "PENDING_FOR_COUNTING";
    public const string ROBO_SORTING              = "ROBO_SORTING";
    public const string LPM_MANUAL_BUILDING       = "LPM_MANUAL_BUILDING";
    public const string CONTAINER_ALLOCATION      = "CONTAINER_ALLOCATION";
    public const string OTS_FOR_PO_ALLOCATION     = "OTS_FOR_PO_ALLOCATION";
    public const string OTS_FOR_CDC_ALLOCATION    = "OTS_FOR_CDC_ALLOCATION";
    public const string CDC_BOX_ALLOCATION        = "CDC_BOX_ALLOCATION";
    public const string MANUAL_ALLOCATION_UPLOAD  = "MANUAL_ALLOCATION_UPLOAD";
    public const string OPEN_CONTAINER            = "OPEN_CONTAINER";
    public const string ROBOTIC_CHUTE_MAPPING     = "ROBOTIC_CHUTE_MAPPING";
    public const string ROBOTIC_CHUTE_MAPPING_TECHNO = "ROBOTIC_CHUTE_MAPPING_TECHNO";

    public const string DATASYNC_CONTAINER_ALLOCATION = "DATASYNC_CONTAINER_ALLOCATION";
    public const string DATASYNC_MASTER               = "DATASYNC_MASTER";
    public const string DATASYNC_BOXES_TO_WMSPROD     = "DATASYNC_BOXES_TO_WMSPROD";
    public const string ITEM_ENCODING             = "ITEM_ENCODING";
    public const string MAX_CAP_UPLOAD            = "MAX_CAP_UPLOAD";
    public const string LPM_PRODUCTION            = "LPM_PRODUCTION";

    public const string RPT_PENDING_PURCHASE      = "RPT_PENDING_PURCHASE";
    public const string RPT_MISSING_EXCESS        = "RPT_MISSING_EXCESS";
    public const string RPT_NON_LPM_WH_STOCK      = "RPT_NON_LPM_WH_STOCK";
    public const string RPT_LPM_WH_STOCK          = "RPT_LPM_WH_STOCK";
    public const string RPT_PRODUCTION_SUMMARY    = "RPT_PRODUCTION_SUMMARY";
    public const string RPT_WAREHOUSE_BOXES       = "RPT_WAREHOUSE_BOXES";
    public const string RPT_TRANSFER_GIN_GRN      = "RPT_TRANSFER_GIN_GRN";
    public const string RPT_COUNTING_COMPLETION   = "RPT_COUNTING_COMPLETION";
    public const string RPT_PO_COUNTING           = "RPT_PO_COUNTING";
    public const string RPT_JAFZA_DIVISION_PROD   = "RPT_JAFZA_DIVISION_PROD";
    public const string RPT_SYNC_DATA_COUNT       = "RPT_SYNC_DATA_COUNT";
    public const string RPT_SHIPMENT_STATUS       = "RPT_SHIPMENT_STATUS";
    public const string RPT_WAREHOUSE_SOH_SUMMARY = "RPT_WAREHOUSE_SOH_SUMMARY";
    public const string RPT_ECOM_STOCK_VARIANCE   = "RPT_ECOM_STOCK_VARIANCE";
    public const string RPT_SOH_MONTHLY_SUMMARY   = "RPT_SOH_MONTHLY_SUMMARY";
    public const string RPT_YOTO_VNA_DASHBOARD    = "RPT_YOTO_VNA_DASHBOARD";
    public const string RPT_WAREHOUSE_INCENTIVES  = "RPT_WAREHOUSE_INCENTIVES";
    public const string RPT_ECOM_PRODUCTION_REPORTS = "RPT_ECOM_PRODUCTION_REPORTS";
    public const string RPT_REPORT_ASSISTANT = "RPT_REPORT_ASSISTANT";

    public const string ADMIN_USERS               = "ADMIN_USERS";
    public const string ADMIN_WH_MASTER           = "ADMIN_WH_MASTER";
    public const string ADMIN_AUDIT_LOG           = "ADMIN_AUDIT_LOG";
    public const string ADMIN_NIGHTLY_BATCHES     = "ADMIN_NIGHTLY_BATCHES";
    public const string ADMIN_PENDING_GOODS_RECEIPT_EMAIL = "ADMIN_PENDING_GOODS_RECEIPT_EMAIL";

    public const string TCM_LABORATORY             = "TCM_LABORATORY";

    /// <summary>One catalogue entry per menu item.</summary>
    /// <param name="Category">
    /// Optional sub-heading within Group (e.g. "Inbound" under "Reports").
    /// Null renders the item directly under its Group with no sub-heading.
    /// </param>
    public sealed record MenuEntry(
        string Key,
        string Group,
        string Label,
        string Url,
        IReadOnlyList<string> DefaultRoles,
        string? Category = null);

    /// <summary>
    /// Source of truth for the menu inventory. DefaultRoles is informational
    /// only (shown next to each checkbox on the Menu Access admin screen as a
    /// hint of who a role-based system would grant this to) — it does NOT
    /// drive authorization. Actual access is decided solely by explicit
    /// per-user grants in WmsUserMenuAccess (see MenuAccessHandler): a user
    /// with zero grants sees nothing beyond the Admin bypass.
    /// </summary>
    public static readonly IReadOnlyList<MenuEntry> All = new[]
    {
        new MenuEntry(PENDING_FOR_COUNTING,  "Container Counting",  "Pending for Counting",      "counting/pending-for-counting", new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),
        new MenuEntry(ROBO_SORTING,          "Container Counting",  "Robo Sorting",              "counting/robo-sorting",     new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),
        new MenuEntry(LPM_MANUAL_BUILDING,   "Container Counting",  "LPM Manual Counting",       "counting/manual",           new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),
        new MenuEntry(CONTAINER_ALLOCATION,  "Container Counting",  "Container Allocation",      "counting/container-allocation", new[] { Roles.Admin, Roles.WHManager }),
        new MenuEntry(OTS_FOR_PO_ALLOCATION, "Container Counting",  "OTS for PO Allocation",     "counting/ots-po-allocation", new[] { Roles.Admin, Roles.WHManager }),
        new MenuEntry(OTS_FOR_CDC_ALLOCATION,"Container Counting",  "OTS for CDC Box Allocation","counting/ots-cdc-allocation", new[] { Roles.Admin, Roles.WHManager }),
        new MenuEntry(CDC_BOX_ALLOCATION,    "Container Counting",  "CDC Box Allocation",        "counting/cdc-box-allocation", new[] { Roles.Admin, Roles.WHManager }),
        new MenuEntry(MANUAL_ALLOCATION_UPLOAD, "Container Counting","Manual Allocation Upload",  "counting/manual-allocation-upload", new[] { Roles.Admin, Roles.WHManager }),
        new MenuEntry(OPEN_CONTAINER,        "Container Counting",  "Open Container",            "counting/open-container",   new[] { Roles.Admin, Roles.WHManager, Roles.WHSupervisor }),

        new MenuEntry(ROBOTIC_CHUTE_MAPPING,  "Robotic",             "Chute Mapping (Jafza)",      "robotic/chute-mapping",     new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),
        new MenuEntry(ROBOTIC_CHUTE_MAPPING_TECHNO, "Robotic",       "Chute Mapping (Techno)",     "robotic/chute-mapping-techno", new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),
        new MenuEntry(TCM_LABORATORY,         "TCM Laboratory",      "Upload TCM File",            "tcm-laboratory",            new[] { Roles.Admin, Roles.WHManager, Roles.WHSupervisor }),

        new MenuEntry(DATASYNC_CONTAINER_ALLOCATION, "Data Sync",   "Container Allocation Data Sync", "datasync/container-allocation", new[] { Roles.Admin, Roles.WHManager }),
        new MenuEntry(DATASYNC_MASTER,               "Data Sync",   "Master Data Sync",               "datasync/master",               new[] { Roles.Admin, Roles.WHManager }),
        new MenuEntry(DATASYNC_BOXES_TO_WMSPROD,     "Data Sync",   "Boxes Data Sync to WMSPROD",     "datasync/boxes-to-wmsprod",     new[] { Roles.Admin, Roles.WHManager }),

        new MenuEntry(ITEM_ENCODING,         "Item Encoding",       "Item Encoding",             "encoding",                  new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),

        new MenuEntry(MAX_CAP_UPLOAD,         "Operations",          "Warehouse Min/Max Cap Upload", "capacity/max-cap-upload", new[] { Roles.Admin, Roles.WHManager }),

        new MenuEntry(LPM_PRODUCTION,        "Production to Stores","LPM Production",            "production/lpm",            new[] { Roles.Admin, Roles.WHAssociate, Roles.WHSupervisor, Roles.WHManager }),

        new MenuEntry(RPT_PENDING_PURCHASE,  "Reports",             "Pending Goods Receipt",          "reports/pending-purchase",         new[] { Roles.Admin, Roles.Reports, Roles.WHManager, Roles.WHSupervisor }),
        new MenuEntry(RPT_MISSING_EXCESS,    "Reports",             "Missing / Excess Items from Production", "reports/missing-excess",   new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_NON_LPM_WH_STOCK,  "Reports",             "Non-LPM WH Stock Report",   "reports/non-lpm-wh-stock",  new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_LPM_WH_STOCK,      "Reports",             "LPM WH Stock Report",       "reports/lpm-wh-stock",      new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_PRODUCTION_SUMMARY,"Reports",             "Production Summary Report", "reports/production-summary",new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_WAREHOUSE_BOXES,   "Reports",             "Warehouse Boxes",           "reports/warehouse-boxes",   new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_TRANSFER_GIN_GRN,  "Reports",             "Transfer/GIN/GRN History",  "reports/transfer-gin-grn",  new[] { Roles.Admin, Roles.Reports }, "Outbound"),
        new MenuEntry(RPT_COUNTING_COMPLETION,"Reports",            "Counting Completion Report","reports/counting-completion",new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_PO_COUNTING,       "Reports",             "PO Counting Report",       "reports/po-counting",       new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_JAFZA_DIVISION_PROD,"Reports",            "JAFZA Production Report",  "reports/jafza-division-production",new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_SHIPMENT_STATUS,   "Reports",             "Shipment Status",           "reports/shipment-status",  new[] { Roles.Admin, Roles.Reports }, "Inbound"),
        new MenuEntry(RPT_WAREHOUSE_SOH_SUMMARY, "Reports",         "Warehouse SOH Summary",     "reports/warehouse-soh-summary", new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_ECOM_STOCK_VARIANCE, "Reports",           "ECOM Stock Variance Report","reports/ecom-stock-variance", new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_SOH_MONTHLY_SUMMARY, "Reports",           "SOH Monthly Summary",      "reports/soh-monthly-summary", new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_YOTO_VNA_DASHBOARD,  "Reports",           "YOTO VNA Dashboard",      "reports/yoto-vna-dashboard", new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_ECOM_PRODUCTION_REPORTS, "Reports",       "Ecom Production Report",  "reports/ecom-production-reports", new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_WAREHOUSE_INCENTIVES, "Reports",          "Warehouse Incentives",    "reports/warehouse-incentives", new[] { Roles.Admin, Roles.Reports }),
        new MenuEntry(RPT_SYNC_DATA_COUNT,   "IT",                 "Sync Data Count",           "reports/sync-data-count",   new[] { Roles.Admin, Roles.Reports }),

        new MenuEntry(ADMIN_USERS,           "Admin",               "Users & Roles",             "admin/users",               new[] { Roles.Admin }),
        new MenuEntry(RPT_REPORT_ASSISTANT,  "Admin",               "Report Assistant (Chat)",   "reports/report-assistant",  new[] { Roles.Admin }),
        new MenuEntry(ADMIN_WH_MASTER,       "Admin",               "WH Master",                 "admin/wh-master",           new[] { Roles.Admin }),
        new MenuEntry(ADMIN_AUDIT_LOG,       "Admin",               "Audit Log",                 "admin/audit",               new[] { Roles.Admin }),
        new MenuEntry(ADMIN_NIGHTLY_BATCHES, "Admin",               "Nightly Batches Status",    "admin/nightly-batches",     new[] { Roles.Admin }),
        new MenuEntry(ADMIN_PENDING_GOODS_RECEIPT_EMAIL, "Admin",   "Pending Goods Receipt Email","admin/pending-goods-receipt-email", new[] { Roles.Admin }),
    };

    /// <summary>Claim type emitted per granted menu by WmsClaimsTransformer.</summary>
    public const string ClaimType = "aiwms_menu";
}
