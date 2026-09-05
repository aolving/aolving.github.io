using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Pages.Tools;

public class MineModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public MineModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public IReadOnlyList<Row> Tools { get; private set; } = [];

    public record Row(int Id, string Name, string Category, bool IsListed, int PhotoCount, int? PrimaryPhotoId, int PendingRequests);

    public async Task OnGetAsync()
    {
        var userId = _userManager.GetUserId(User)!;

        Tools = await _db.Tools.AsNoTracking()
            .Where(t => t.OwnerId == userId)
            .OrderBy(t => t.Name)
            .Select(t => new Row(
                t.Id,
                t.Name,
                t.Category,
                t.IsListed,
                t.Photos.Count,
                t.Photos.Where(p => p.IsPrimary).Select(p => (int?)p.Id).FirstOrDefault()
                    ?? t.Photos.Select(p => (int?)p.Id).FirstOrDefault(),
                t.Bookings.Count(b => b.Status == BookingStatus.Requested)))
            .ToListAsync();
    }
}
