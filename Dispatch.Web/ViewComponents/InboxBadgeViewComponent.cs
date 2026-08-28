using Dispatch.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.ViewComponents;

public class InboxBadgeViewComponent : ViewComponent
{
    private readonly AppDbContext _db;

    public InboxBadgeViewComponent(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var unread = await _db.InboxMessages.CountAsync(m => !m.IsRead);
        return View(unread);
    }
}
