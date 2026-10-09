using IntegrationFramework.Core.Data;
using IntegrationFramework.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IntegrationFramework.Api.Controllers;

/// <summary>
/// Project groupings for workflow segregation. Grouping only: creating, editing
/// and deleting projects does not change workflow access (owner/share/admin rules
/// are untouched). A project with workflows cannot be deleted — move or delete
/// them first, so nothing is silently orphaned or lost.
/// </summary>
[ApiController]
[Route("api/projects")]
public class ProjectsController(MetadataDbContext db) : ControllerBase
{
    public class ProjectRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProjectDto>>> List() =>
        Ok(await db.Projects
            .OrderBy(p => p.Name)
            .Select(p => new ProjectDto(p.Id, p.Name, p.Description,
                p.Workflows.Count, p.CreatedAt, p.UpdatedAt))
            .ToListAsync());

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProjectDto>> Get(Guid id)
    {
        var project = await db.Projects.FindAsync([id]);
        if (project is null) return NotFound();
        var count = await db.Workflows.CountAsync(w => w.ProjectId == id);
        return Ok(new ProjectDto(project.Id, project.Name, project.Description, count,
            project.CreatedAt, project.UpdatedAt));
    }

    [HttpPost]
    public async Task<ActionResult<ProjectDto>> Create([FromBody] ProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (await db.Projects.AnyAsync(p => p.Name == request.Name))
            return Conflict(new { error = "A project with this name already exists." });

        var project = new Project
        {
            Name = request.Name.Trim(),
            Description = request.Description
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = project.Id },
            new ProjectDto(project.Id, project.Name, project.Description, 0,
                project.CreatedAt, project.UpdatedAt));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProjectDto>> Update(Guid id, [FromBody] ProjectRequest request)
    {
        var project = await db.Projects.FindAsync([id]);
        if (project is null) return NotFound();

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { error = "Name cannot be empty." });
            if (name != project.Name && await db.Projects.AnyAsync(p => p.Name == name))
                return Conflict(new { error = "A project with this name already exists." });
            project.Name = name;
        }
        if (request.Description is not null) project.Description = request.Description;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var count = await db.Workflows.CountAsync(w => w.ProjectId == id);
        return Ok(new ProjectDto(project.Id, project.Name, project.Description, count,
            project.CreatedAt, project.UpdatedAt));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var project = await db.Projects.FindAsync([id]);
        if (project is null) return NotFound();

        // Nothing is silently orphaned: an empty project deletes, a non-empty one
        // explains what to do first.
        var count = await db.Workflows.CountAsync(w => w.ProjectId == id);
        if (count > 0)
            return Conflict(new
            {
                error = $"Project '{project.Name}' still contains {count} workflow(s). Move or delete them before deleting the project."
            });

        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
