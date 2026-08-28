using Dispatch.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Controllers;

public class InboxController : Controller
{
    private readonly AppDbContext _db;

    public InboxController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var messages = await _db.InboxMessages
            .AsNoTracking()
            .OrderByDescending(m => m.SentUtc)
            .ToListAsync();
        return View(messages);
    }

    public async Task<IActionResult> Detail(Guid id)
    {
        var message = await _db.InboxMessages.FirstOrDefaultAsync(m => m.Id == id);
        if (message is null) return NotFound();

        if (!message.IsRead)
        {
            message.IsRead = true;
            await _db.SaveChangesAsync();
        }

        return View(message);
    }
}
