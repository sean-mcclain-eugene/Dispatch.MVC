using System.Text.Json;
using Dispatch.Mvc.Data;
using Dispatch.Mvc.Models;
using Dispatch.Mvc.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Mvc.Controllers;

public class JobController : Controller
{
    private readonly AppDbContext _db;
    private readonly IBackgroundTaskQueue _queue;

    public JobController(AppDbContext db, IBackgroundTaskQueue queue)
    {
        _db = db;
        _queue = queue;
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
            CreatedUtc = DateTime.UtcNow
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        // Return the status page immediately. The hosted service picks this up.
        _queue.QueueJob(job.JobId);

        return RedirectToAction(nameof(Status), new { id = job.JobId });
    }

    [HttpGet]
    public async Task<IActionResult> Status(Guid id)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();

        if (job.IsDetached && job.Status is not (JobStatuses.Completed or JobStatuses.Failed))
        {
            return RedirectToAction(nameof(Detached), new { id });
        }

        if (job.Status is JobStatuses.Completed or JobStatuses.Failed)
        {
            return RedirectToAction(nameof(Session), new { id });
        }

        return View(ToProgress(job));
    }

    /// <summary>
    /// JSON feed the status page polls. Cheap reads, no HTML.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Progress(Guid id)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();
        return Json(ToProgress(job));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Detach(Guid id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.JobId == id);
        if (job is null) return NotFound();

        if (job.Status is JobStatuses.Completed or JobStatuses.Failed)
        {
            return RedirectToAction(nameof(Session), new { id });
        }

        job.IsDetached = true;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Detached), new { id });
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

        if (job.Status is JobStatuses.Completed or JobStatuses.Failed)
        {
            return RedirectToAction(nameof(Session), new { id });
        }

        return View(ToProgress(job));
    }

    /// <summary>
    /// Capability URL emailed to the user. No login — possession of the id is the key.
    /// </summary>
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
        var progress = job.Status is JobStatuses.Completed or JobStatuses.Failed
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
