using Microsoft.AspNetCore.Authorization;
using Wms.Core;

namespace Wms.Web.Auth;

/// <summary>Requirement: user can access the given menu key.</summary>
public sealed class MenuAccessRequirement(string menuKey) : IAuthorizationRequirement
{
    public string MenuKey { get; } = menuKey;
}

/// <summary>
/// Passes the requirement when the user is Admin (special bypass) OR has an
/// explicit aiwms_menu grant for this MenuKey. WmsUserMenuAccess is the sole
/// allowlist for non-Admin users — a user with zero grant rows sees nothing,
/// by design (Admin must explicitly grant each menu item via Users.razor's
/// Menu Access screen). There is intentionally no role-based fallback: an
/// earlier version fell back to each menu's DefaultRoles when a user had no
/// grants configured, which meant "nothing ticked" silently resolved to
/// "everything their role allows" instead of "nothing" — the opposite of
/// what the Menu Access checkboxes visually communicate.
/// </summary>
public sealed class MenuAccessHandler : AuthorizationHandler<MenuAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext ctx, MenuAccessRequirement req)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        // 1) Admin bypass.
        if (user.IsInRole(Roles.Admin)) { ctx.Succeed(req); return Task.CompletedTask; }

        // 2) Explicit allowlist: only succeeds if this exact MenuKey was granted.
        if (user.HasClaim(c => c.Type == MenuKeys.ClaimType && c.Value == req.MenuKey))
            ctx.Succeed(req);

        return Task.CompletedTask;
    }
}
