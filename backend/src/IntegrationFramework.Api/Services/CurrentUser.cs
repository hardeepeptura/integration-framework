using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Services;

/// <summary>
/// The resolved platform user for the current request. Null when the request is
/// anonymous (SSO disabled and no test principal) — controllers then behave
/// exactly as before roles existed (no filtering, full access), which keeps
/// local development and the no-SSO test harness unchanged.
/// </summary>
public record CurrentUser(AppUser User)
{
    public Guid Id => User.Id;
    public string Email => User.Email;
    public string Role => User.Role;
    public bool IsAdmin => User.Role == AppUser.RoleAdmin;
}

/// <summary>
/// Resolves the authenticated corporate principal to an AppUser row, provisioning
/// one on first login. The very first user of an empty installation is bootstrapped
/// as an admin so role management is possible before any user exists.
/// </summary>
public class CurrentUserService(MetadataDbContext db)
{
    public virtual async Task<CurrentUser?> ResolveAsync(System.Security.Claims.ClaimsPrincipal principal)
    {
        if (!(principal.Identity?.IsAuthenticated ?? false)) return null;

        // preferred_username is the Entra ID UPN; fall back to the email claim.
        var email =
            principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? principal.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email)) return null;
        email = email.ToLowerInvariant();

        var displayName =
            principal.FindFirst("name")?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value;

        var user = await db.AppUsers.SingleOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            // First user bootstraps as admin; everyone else joins as contributor.
            var first = !await db.AppUsers.AnyAsync();
            user = new AppUser
            {
                Email = email,
                DisplayName = displayName,
                Role = first ? AppUser.RoleAdmin : AppUser.RoleContributor
            };
            db.AppUsers.Add(user);
            await db.SaveChangesAsync();
        }
        else if (displayName is not null && user.DisplayName != displayName)
        {
            user.DisplayName = displayName;
            await db.SaveChangesAsync();
        }

        return new CurrentUser(user);
    }
}

/// <summary>
/// Workflow-level access decisions. Rules:
/// admins see everything; owners manage their workflows; shares grant view/edit;
/// ownerless (pre-ownership) workflows are visible to all authenticated users but
/// mutable only by admins; anonymous (SSO off) bypasses everything.
/// </summary>
public class WorkflowAccessService(MetadataDbContext db)
{
    public async Task<bool> CanViewAsync(CurrentUser? user, Core.Entities.Workflow workflow)
    {
        if (user is null) return true; // SSO-off/local mode
        if (user.IsAdmin) return true;
        if (workflow.OwnerEmail is null) return true; // legacy unrestricted visibility
        if (workflow.OwnerEmail == user.Email) return true;
        return await db.WorkflowShares.AnyAsync(s =>
            s.WorkflowId == workflow.Id && s.Email == user.Email);
    }

    public async Task<bool> CanEditAsync(CurrentUser? user, Core.Entities.Workflow workflow)
    {
        if (user is null) return true; // SSO-off/local mode
        if (user.IsAdmin) return true;
        if (workflow.OwnerEmail is null) return false; // legacy workflows: admin-only mutation
        if (workflow.OwnerEmail == user.Email) return true;
        return await db.WorkflowShares.AnyAsync(s =>
            s.WorkflowId == workflow.Id && s.Email == user.Email
            && s.Permission == WorkflowShare.PermissionEdit);
    }

    /// <summary>Owner/admin only — delete and share management.</summary>
    public bool CanManage(CurrentUser? user, Core.Entities.Workflow workflow)
    {
        if (user is null) return true; // SSO-off/local mode
        if (user.IsAdmin) return true;
        return workflow.OwnerEmail is not null && workflow.OwnerEmail == user.Email;
    }

    /// <summary>ID predicate for list filtering: admins and anonymous see all.</summary>
    public async Task<List<Guid>> VisibleWorkflowIdsAsync(CurrentUser? user)
    {
        if (user is null || user.IsAdmin)
            return await db.Workflows.Select(w => w.Id).ToListAsync();

        var sharedIds = await db.WorkflowShares
            .Where(s => s.Email == user.Email)
            .Select(s => s.WorkflowId)
            .ToListAsync();
        return await db.Workflows
            .Where(w => w.OwnerEmail == null || w.OwnerEmail == user.Email || sharedIds.Contains(w.Id))
            .Select(w => w.Id)
            .ToListAsync();
    }
}
