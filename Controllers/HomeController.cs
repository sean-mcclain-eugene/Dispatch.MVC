using System.Diagnostics;
using Dispatch.Mvc.Data;
using Dispatch.Mvc.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Mvc.Controllers;

public class HomeController : Controller
{
    private readonly AppDbContext _db;

    public HomeController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var recent = await _db.Jobs
            .AsNoTracking()
            .OrderByDescending(j => j.CreatedUtc)
            .Take(8)
            .ToListAsync();

        ViewBag.Catalog = JobCatalog.All;
        ViewBag.Recent = recent;
        return View(new StartJobRequest());
    }

    public IActionResult Pattern() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        ViewBag.RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        return View();
    }
}
