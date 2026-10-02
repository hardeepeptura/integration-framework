using IntegrationFramework.Api.Services;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// Admin-only user and role management. SSO provisions users automatically on
/// first login; admins promote/demote between contributor and admin here.
/// </summary>
[ApiController]
[Route("api/users")]
public class UsersController(
    MetadataDbContext db, CurrentUserService currentUser) : ControllerBase
{
    public class RoleRequest { public string? Role { get; set; } }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> List()
    {
        var me = await currentUser.ResolveAsync(User);
        if (me is not null && !me.IsAdmin) return Forbid();
        var users = await db.AppUsers.OrderBy(u => u.Email).ToListAsync();
        return Ok(users.Select(UserDto.From));
    }

    [HttpPut("{id:guid}/role")]
    public async Task<ActionResult<UserDto>> SetRole(Guid id, [FromBody] RoleRequest request)
    {
        var me = await currentUser.ResolveAsync(User);
        if (me is not null && !me.IsAdmin) return Forbid();

        var role = request.Role?.ToLowerInvariant();
        if (role is not (AppUser.RoleAdmin or AppUser.RoleContributor))
            return BadRequest(new { error = "Role must be 'admin' or 'contributor'." });

        var user = await db.AppUsers.FindAsync([id]);
        if (user is null) return NotFound();

        // Never remove the last admin: someone must always be able to manage users.
        if (user.Role == AppUser.RoleAdmin && role != AppUser.RoleAdmin)
        {
            var adminCount = await db.AppUsers.CountAsync(u => u.Role == AppUser.RoleAdmin);
            if (adminCount <= 1)
                return Conflict(new { error = "Cannot demote the last admin." });
        }

        user.Role = role;
        await db.SaveChangesAsync();
        return Ok(UserDto.From(user));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var me = await currentUser.ResolveAsync(User);
        if (me is not null && !me.IsAdmin) return Forbid();
        if (me is not null && me.Id == id)
            return Conflict(new { error = "You cannot delete your own account." });

        var user = await db.AppUsers.FindAsync([id]);
        if (user is null) return NotFound();

        // Never remove the last admin either.
        if (user.Role == AppUser.RoleAdmin)
        {
            var adminCount = await db.AppUsers.CountAsync(u => u.Role == AppUser.RoleAdmin);
            if (adminCount <= 1)
                return Conflict(new { error = "Cannot delete the last admin." });
        }

        db.AppUsers.Remove(user);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
