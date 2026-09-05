using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;

namespace ToolShed.Web.Pages;

public class IndexModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _clock;

    public IndexModel(ApplicationDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    [BindProperty(SupportsGet = true)]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    public IReadOnlyList<string> Categories { get; private set; } = [];

    public IReadOnlyList<ToolCard> Tools { get; private set; } = [];

    public record ToolCard(int Id, string Name, string Category, string OwnerName, int? PrimaryPhotoId, DateOnly? OnLoanUntil);

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().Date);

        Categories = await _db.Tools
            .Where(t => t.IsListed)
            .Select(t => t.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        var query = _db.Tools.AsNoTracking().Where(t => t.IsListed);

        if (!string.IsNullOrWhiteSpace(Query))
        {
            var term = Query.Trim();
            query = query.Where(t => EF.Functions.Like(t.Name, $"%{term}%")
                                     || EF.Functions.Like(t.Description!, $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(Category))
        {
            query = query.Where(t => t.Category == Category);
        }

        Tools = await query
            .OrderBy(t => t.Name)
            .Select(t => new ToolCard(
                t.Id,
                t.Name,
                t.Category,
                t.Owner!.DisplayName,
                t.Photos.Where(p => p.IsPrimary).Select(p => (int?)p.Id).FirstOrDefault()
                    ?? t.Photos.Select(p => (int?)p.Id).FirstOrDefault(),
                t.Bookings
                    .Where(b => b.Status == BookingStatus.Approved && b.StartDate <= today && b.EndDate >= today)
                    .Select(b => (DateOnly?)b.EndDate)
                    .FirstOrDefault()))
            .ToListAsync();
    }
}
