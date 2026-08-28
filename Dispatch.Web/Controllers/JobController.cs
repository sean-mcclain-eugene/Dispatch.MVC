using System.Text.Json;
using Dispatch.Core.Data;
using Dispatch.Core.Models;
using Dispatch.Core.Services;
using Dispatch.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Controllers;

/// <summary>
/// IIS / Kestrel only. Inserts a Pending row and holds the attached lease.
/// Dispatch.Worker claims the row from the database — there is no in-process queue.
/// </summary>
public class JobController : Controller
{
    private readonly AppDbContext _db;

    public JobController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(StartJobRequest request)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Catalog = JobCatalog.All;
            ViewBag.Recent = await _db.Jobs.AsNoTracking()
                .OrderByDescending(j => j.CreatedUtc).Take(8).ToListAsync();
            return View("~/Views/Home/Index.cshtml", request);
        }

        var catalog = JobCatalog.Get(request.Kind);
        var now = DateTime.UtcNow;
        var job = new LongRunningJob
        {
            JobId = Guid.NewGuid(),
            Kind = catalog.Kind,
            Title = catalog.Title,
            NotifyEmail = request.NotifyEmail.Trim(),
            Status = JobStatuses.Pending,
            IsDetached = false,
            CurrentStep = 0,
            StepCount = catalog.Steps.Count,
            StepLabel = "Queued",
            CompletedStepsJson = "[]",
            PublicBaseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}",
            LastHeartbeatUtc = now,
            CreatedUtc = now
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Status), new { id = job.JobId });
    }

    [HttpGet]
    public async Task<IActionResult> Status(Guid id)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();

        if (job.IsDetached && !JobStatuses.IsTerminal(job.Status))
        {
            return RedirectToAction(nameof(Detached), new { id });
        }

        if (JobStatuses.IsTerminal(job.Status))
        {
            return RedirectToAction(nameof(Session), new { id });
        }

        return View(ToProgress(job));
    }

    [HttpGet]
    public async Task<IActionResult> Progress(Guid id)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();

        if (!job.IsDetached && !JobStatuses.IsTerminal(job.Status))
        {
            var now = DateTime.UtcNow;
            await _db.Jobs.Where(j => j.JobId == id && !j.IsDetached)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastHeartbeatUtc, now));
            job.LastHeartbeatUtc = now;
        }

        return Json(ToProgress(job));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Detach(Guid id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();

        if (JobStatuses.IsTerminal(job.Status))
        {
            return RedirectToAction(nameof(Session), new { id });
        }

        job.IsDetached = true;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Detached), new { id });
    }

    [HttpPost]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Abandon(Guid id)
    {
        await _db.Jobs
            .Where(j => j.JobId == id
                        && !j.IsDetached
                        && j.Status != JobStatuses.Completed
                        && j.Status != JobStatuses.Failed
                        && j.Status != JobStatuses.Abandoned)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, JobStatuses.Abandoned)
                .SetProperty(x => x.CompletedUtc, DateTime.UtcNow)
                .SetProperty(x => x.LockUntilUtc, JobClaimer.Unlocked)
                .SetProperty(x => x.Error, "Browser closed the attached session."));

        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> Detached(Guid id)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();

        if (!job.IsDetached)
        {
            return RedirectToAction(nameof(Status), new { id });
        }

        if (JobStatuses.IsTerminal(job.Status))
        {
            return RedirectToAction(nameof(Session), new { id });
        }

        return View(ToProgress(job));
    }

    [HttpGet]
    public async Task<IActionResult> Session(Guid id)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();
        return View(job);
    }

    private static JobProgressDto ToProgress(LongRunningJob job)
    {
        var catalog = JobCatalog.Get(job.Kind);
        var completed = ParseList(job.CompletedStepsJson);
        var progress = JobStatuses.IsTerminal(job.Status) && job.Status != JobStatuses.Abandoned
            ? 1d
            : job.StepCount == 0
                ? 0d
                : Math.Clamp((completed.Count + (job.Status == JobStatuses.Running ? 0.35 : 0)) / job.StepCount, 0, 0.99);

        return new JobProgressDto(
            job.JobId,
            job.Kind,
            job.Title,
            job.Status,
            job.IsDetached,
            job.CurrentStep,
            job.StepCount,
            job.StepLabel,
            catalog.Steps,
            completed,
            progress,
            job.NotifyEmail,
            job.CreatedUtc,
            job.CompletedUtc);
    }

    private static IReadOnlyList<string> ParseList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
