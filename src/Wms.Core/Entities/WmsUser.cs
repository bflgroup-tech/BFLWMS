namespace Wms.Core.Entities;

public class WmsUser
{
    public string Username { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? Country { get; set; }
    public string? Warehouse { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<WmsUserRole> UserRoles { get; set; } = new();
}

public class WmsWHMaster
{
    public string Country { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreateTS { get; set; }
    public string CreatedBy { get; set; } = "";
}

public class WmsRole
{
    public string RoleCode { get; set; } = "";
    public string RoleName { get; set; } = "";
    public DateTime CreateTS { get; set; }
}

public class WmsUserRole
{
    public string Username { get; set; } = "";
    public string RoleCode { get; set; } = "";
    public DateTime CreateTS { get; set; }
    public WmsUser? User { get; set; }
    public WmsRole? Role { get; set; }
}

public class WmsUserMenuAccess
{
    public string   Username  { get; set; } = "";
    public string   MenuKey   { get; set; } = "";
    public DateTime GrantedTS { get; set; }
    public string   GrantedBy { get; set; } = "";
}

/// <summary>Per-user grant of the countries whose data the user is allowed to see in
/// reports/pages. Admin role bypasses this table (sees all). Empty rows = no access.</summary>
public class WmsUserCountryAccess
{
    public string   Username  { get; set; } = "";
    public string   Country   { get; set; } = "";
    public DateTime GrantedTS { get; set; }
    public string   GrantedBy { get; set; } = "";
}

/// <summary>Per-user grant of hideable page sub-sections (Wms.Core.SectionKeys). Admin
/// role bypasses this table (sees all). Empty rows = section hidden.</summary>
public class WmsUserSectionAccess
{
    public string   Username   { get; set; } = "";
    public string   SectionKey { get; set; } = "";
    public DateTime GrantedTS  { get; set; }
    public string   GrantedBy  { get; set; } = "";
}

/// <summary>Per-user grant of specific stores within the Transfer/GIN/GRN History
/// report's STORE dropdown. Admin role bypasses this table. Unlike
/// WmsUserCountryAccess/WmsUserSectionAccess, this is opt-in, not restrict-by-default:
/// a user with NO rows here sees every store their country access already allows
/// (unchanged from before this table existed) — only a user who has at least one row
/// is narrowed down to just those stores. A restrict-by-default table would have
/// silently hidden every store from every existing user the moment it was introduced.</summary>
public class WmsUserStoreAccess
{
    public string   Username  { get; set; } = "";
    public string   StoreName { get; set; } = "";
    public DateTime GrantedTS { get; set; }
    public string   GrantedBy { get; set; } = "";
}
