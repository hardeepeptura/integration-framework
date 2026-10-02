using IntegrationFramework.Api.Services;
using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// Per-workflow sharing: the owner (or an admin) grants view/edit access to other
/// corporate users by email. Shares cascade-delete with the workflow.
/// </summary>
[ApiController]
[Route("api/workflows/{workflowId:guid}/shares")]
public class SharesController(MetadataDbContext db, CurrentUserService currentUser) : ControllerBase
{
    public class ShareRequest
    {
        public string? Email { get; set; }
        public string? Permission { get; set; }
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<WorkflowShareDto>>> List(Guid workflowId)
    {
        var workflow = await db.Workflows.Include(w => w.Shares).FirstOrDefaultAsync(w => w.Id == workflowId);
        if (workflow is null) return NotFound();
        if (!await CanManageAsync(workflow)) return Forbid();
        return Ok(workflow.Shares.OrderBy(s => s.Email).Select(WorkflowShareDto.From));
    }

    /// <summary>Add or update a share grant (upsert by email).</summary>
    [HttpPut]
    public async Task<ActionResult<WorkflowShareDto>> Upsert(Guid workflowId, [FromBody] ShareRequest request)
    {
        var workflow = await db.Workflows.Include(w => w.Shares).FirstOrDefaultAsync(w => w.Id == workflowId);
        if (workflow is null) return NotFound();
        if (!await CanManageAsync(workflow)) return Forbid();

        var email = request.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest(new { error = "A valid corporate email is required." });
        var permission = request.Permission?.ToLowerInvariant() switch
        {
            WorkflowShare.PermissionEdit => WorkflowShare.PermissionEdit,
            WorkflowShare.PermissionView => WorkflowShare.PermissionView,
            _ => null
        };
        if (permission is null)
            return BadRequest(new { error = "Permission must be 'view' or 'edit'." });
        if (workflow.OwnerEmail == email)
            return BadRequest(new { error = "This user already owns the workflow." });

        var share = workflow.Shares.FirstOrDefault(s => s.Email == email);
        if (share is null)
        {
            share = new WorkflowShare { WorkflowId = workflowId, Email = email, Permission = permission };
            db.WorkflowShares.Add(share);
        }
        else
        {
            share.Permission = permission;
        }
        await db.SaveChangesAsync();
        return Ok(WorkflowShareDto.From(share));
    }

    [HttpDelete("{shareId:guid}")]
    public async Task<IActionResult> Delete(Guid workflowId, Guid shareId)
    {
        var workflow = await db.Workflows.Include(w => w.Shares).FirstOrDefaultAsync(w => w.Id == workflowId);
        if (workflow is null) return NotFound();
        if (!await CanManageAsync(workflow)) return Forbid();

        var share = workflow.Shares.FirstOrDefault(s => s.Id == shareId);
        if (share is null) return NotFound();
        db.WorkflowShares.Remove(share);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<bool> CanManageAsync(Core.Entities.Workflow workflow)
    {
        var me = await currentUser.ResolveAsync(User);
        if (me is null) return true; // SSO-off/local mode
        if (me.IsAdmin) return true;
        return workflow.OwnerEmail is not null && workflow.OwnerEmail == me.Email;
    }
}
