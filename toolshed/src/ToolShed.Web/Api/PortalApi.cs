using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ToolShed.Contracts;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Web.Api;

/// <summary>
/// The JSON API the iOS and Android apps talk to. It is a thin layer over the same services the
/// website uses (<see cref="BookingService"/>, <see cref="ToolService"/>, <see cref="InvitationService"/>),
/// so a loan made on a phone obeys exactly the rules of a loan made in a browser.
/// </summary>
public static class PortalApi
{
    public static IEndpointRouteBuilder MapPortalApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // ---- sign in / out (the only endpoints that do not need a token) ----
        api.MapPost("/auth/login", LoginAsync).AllowAnonymous();
        api.MapPost("/auth/register", RegisterAsync).AllowAnonymous();
        api.MapPost("/auth/logout", LogoutAsync).RequireAuthorization(ApiTokenDefaults.Policy);

        // ---- everything else needs a device token ----
        var secured = api.MapGroup(string.Empty).RequireAuthorization(ApiTokenDefaults.Policy);

        secured.MapGet("/me", MeAsync);

        secured.MapGet("/tools", ListToolsAsync);
        secured.MapGet("/tools/{id:int}", GetToolAsync);
        secured.MapPost("/tools", CreateToolAsync);
        secured.MapPut("/tools/{id:int}", UpdateToolAsync);
        secured.MapDelete("/tools/{id:int}", DeleteToolAsync);

        // Bearer-authenticated, so there is no cookie for a forged page to ride on; the
        // form-antiforgery check that .NET applies to file uploads does not apply here.
        secured.MapPost("/tools/{id:int}/photos", AddPhotoAsync).DisableAntiforgery();
        secured.MapPost("/tools/{id:int}/photos/{photoId:int}/cover", SetCoverAsync);
        secured.MapDelete("/tools/{id:int}/photos/{photoId:int}", DeletePhotoAsync);

        secured.MapPost("/tools/{id:int}/bookings", RequestBookingAsync);
        secured.MapGet("/bookings", ListBookingsAsync);
        secured.MapPost("/bookings/{id:int}/approve", (int id, DecisionInput? input, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, BookingService bookings, BookingNotifier notifier) =>
            DecideAsync(id, BookingEvent.Approved, input, http, user, db, bookings, notifier));
        secured.MapPost("/bookings/{id:int}/decline", (int id, DecisionInput? input, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, BookingService bookings, BookingNotifier notifier) =>
            DecideAsync(id, BookingEvent.Declined, input, http, user, db, bookings, notifier));
        secured.MapPost("/bookings/{id:int}/cancel", (int id, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, BookingService bookings, BookingNotifier notifier) =>
            DecideAsync(id, BookingEvent.Cancelled, null, http, user, db, bookings, notifier));
        secured.MapPost("/bookings/{id:int}/returned", (int id, HttpContext http, ClaimsPrincipal user, ApplicationDbContext db, BookingService bookings, BookingNotifier notifier) =>
            DecideAsync(id, BookingEvent.Returned, null, http, user, db, bookings, notifier));

        return app;
    }

    // ================================================================ helpers

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static IResult Error(int status, string message) => Results.Json(new ApiError(message), statusCode: status);

    private static bool TryValidate(object model, out string error)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true))
        {
            error = string.Empty;
            return true;
        }

        error = string.Join(" ", results.Select(r => r.ErrorMessage));
        return false;
    }

    private static string LoansUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}/Bookings";

    private static async Task<UserDto> ToUserDtoAsync(ApplicationUser user, UserManager<ApplicationUser> users) =>
        new(user.Id, user.DisplayName, user.Email ?? string.Empty, user.Location, await users.IsInRoleAsync(user, Roles.Admin));

    // ================================================================ auth

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        ApiTokenService tokens)
    {
        if (!TryValidate(request, out var invalid))
        {
            return Error(StatusCodes.Status400BadRequest, invalid);
        }

        var user = await users.FindByEmailAsync(request.Email.Trim());

        // One message for an unknown address, a wrong password and an unconfirmed account.
        const string refused = "That email and password combination was not recognised.";
        if (user is null)
        {
            return Error(StatusCodes.Status401Unauthorized, refused);
        }

        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return Error(StatusCodes.Status401Unauthorized, "This account is locked. Try again later, or ask an administrator.");
        }

        if (!result.Succeeded || !await signIn.CanSignInAsync(user))
        {
            return Error(StatusCodes.Status401Unauthorized, refused);
        }

        var (token, record) = await tokens.IssueAsync(user, request.DeviceName);
        return Results.Ok(new AuthResponse(token, record.ExpiresUtc, await ToUserDtoAsync(user, users)));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<ApplicationUser> users,
        InvitationService invitations,
        ApiTokenService tokens)
    {
        if (!TryValidate(request, out var invalid))
        {
            return Error(StatusCodes.Status400BadRequest, invalid);
        }

        // Email and access code are checked together, with one answer for every kind of failure.
        var invitation = await invitations.VerifyAsync(request.Email, request.AccessCode);
        if (invitation is null)
        {
            return Error(StatusCodes.Status400BadRequest, InvitationService.NotValidMessage);
        }

        // An invitation tied to an address creates that address; an open code uses the one supplied.
        var email = InvitationService.AccountEmail(invitation, request.Email);
        if (invitation.IsOpen && await users.FindByEmailAsync(email) is not null)
        {
            // Not redeemed, so the code is still good for a different address.
            return Error(StatusCodes.Status400BadRequest, "An account with that email already exists. Try signing in instead.");
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            EmailConfirmed = true
        };

        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            return Error(StatusCodes.Status400BadRequest, string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(user, invitation.Role);
        await invitations.RedeemAsync(invitation, user.Id);

        // Roles were added after the user was created, so re-read to pick up the final stamp.
        user = (await users.FindByIdAsync(user.Id))!;
        var (token, record) = await tokens.IssueAsync(user, request.DeviceName);
        return Results.Ok(new AuthResponse(token, record.ExpiresUtc, await ToUserDtoAsync(user, users)));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, ApiTokenService tokens)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await tokens.RevokeAsync(header["Bearer ".Length..].Trim());
        }

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(ClaimsPrincipal principal, UserManager<ApplicationUser> users)
    {
        var user = await users.FindByIdAsync(UserId(principal));
        return user is null
            ? Error(StatusCodes.Status401Unauthorized, "The account no longer exists.")
            : Results.Ok(await ToUserDtoAsync(user, users));
    }

    // ================================================================ tools

    private static async Task<IResult> ListToolsAsync(
        string? query, string? category, bool? mine,
        ClaimsPrincipal principal, ApplicationDbContext db, BookingService bookings)
    {
        var userId = UserId(principal);
        var today = bookings.Today;

        var tools = db.Tools.AsNoTracking().AsQueryable();
        tools = mine == true
            ? tools.Where(t => t.OwnerId == userId)
            : tools.Where(t => t.IsListed);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            tools = tools.Where(t => EF.Functions.Like(t.Name, $"%{term}%")
                                     || EF.Functions.Like(t.Description!, $"%{term}%"));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            tools = tools.Where(t => t.Category == category);
        }

        var result = await tools
            .OrderBy(t => t.Name)
            .Select(t => new ToolSummaryDto(
                t.Id,
                t.Name,
                t.Category,
                t.Owner!.DisplayName,
                t.OwnerId == userId,
                t.IsListed,
                t.Photos.Where(p => p.IsPrimary).Select(p => (int?)p.Id).FirstOrDefault()
                    ?? t.Photos.Select(p => (int?)p.Id).FirstOrDefault(),
                t.Bookings
                    .Where(b => b.Status == BookingStatus.Approved && b.StartDate <= today && b.EndDate >= today)
                    .Select(b => (DateOnly?)b.EndDate)
                    .FirstOrDefault()))
            .ToListAsync();

        return Results.Ok(result);
    }

    private static async Task<ToolDetailDto?> LoadDetailAsync(int id, string userId, ApplicationDbContext db, BookingService bookings)
    {
        var tool = await db.Tools.AsNoTracking()
            .Include(t => t.Owner)
            .Include(t => t.Photos)
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tool is null)
        {
            return null;
        }

        var held = await bookings.HeldRangesAsync(id);
        return new ToolDetailDto(
            tool.Id,
            tool.Name,
            tool.Category,
            tool.Description,
            tool.PickupLocation,
            tool.MaxLoanDays,
            tool.IsListed,
            tool.Owner!.DisplayName,
            tool.OwnerId == userId,
            tool.Photos.OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Id)
                .Select(p => new PhotoDto(p.Id, p.Caption, p.IsPrimary)).ToList(),
            held.Select(b => new HeldRangeDto(b.StartDate, b.EndDate, b.Status == BookingStatus.Approved)).ToList());
    }

    private static async Task<IResult> GetToolAsync(int id, ClaimsPrincipal principal, ApplicationDbContext db, BookingService bookings)
    {
        var detail = await LoadDetailAsync(id, UserId(principal), db, bookings);
        return detail is null ? Error(StatusCodes.Status404NotFound, "That tool does not exist.") : Results.Ok(detail);
    }

    private static IResult FromToolResult(ToolResult result, Func<IResult> onSuccess) => result.Failure switch
    {
        ToolFailure.None => onSuccess(),
        ToolFailure.NotFound => Error(StatusCodes.Status404NotFound, result.Error!),
        _ => Error(StatusCodes.Status400BadRequest, result.Error!)
    };

    private static async Task<IResult> CreateToolAsync(
        ToolInput input, ClaimsPrincipal principal, ToolService tools, ApplicationDbContext db, BookingService bookings)
    {
        if (!TryValidate(input, out var invalid))
        {
            return Error(StatusCodes.Status400BadRequest, invalid);
        }

        var userId = UserId(principal);
        var result = await tools.CreateAsync(userId, input, []);
        if (!result.Succeeded)
        {
            return FromToolResult(result, () => Results.Empty);
        }

        var detail = await LoadDetailAsync(result.Tool!.Id, userId, db, bookings);
        return Results.Created($"/api/v1/tools/{result.Tool.Id}", detail);
    }

    private static async Task<IResult> UpdateToolAsync(
        int id, ToolInput input, ClaimsPrincipal principal, ToolService tools, ApplicationDbContext db, BookingService bookings)
    {
        if (!TryValidate(input, out var invalid))
        {
            return Error(StatusCodes.Status400BadRequest, invalid);
        }

        var userId = UserId(principal);
        var result = await tools.UpdateAsync(id, userId, input);
        return result.Succeeded
            ? Results.Ok(await LoadDetailAsync(id, userId, db, bookings))
            : FromToolResult(result, () => Results.Empty);
    }

    private static async Task<IResult> DeleteToolAsync(int id, ClaimsPrincipal principal, ToolService tools)
    {
        var result = await tools.DeleteAsync(id, UserId(principal));
        return FromToolResult(result, () => Results.NoContent());
    }

    private static async Task<IResult> AddPhotoAsync(int id, IFormFile file, ClaimsPrincipal principal, ToolService tools)
    {
        var result = await tools.AddPhotosAsync(id, UserId(principal), [file]);
        return FromToolResult(result, () =>
            Results.Created($"/photos/{result.Photo!.Id}", new PhotoDto(result.Photo.Id, result.Photo.Caption, result.Photo.IsPrimary)));
    }

    private static async Task<IResult> SetCoverAsync(int id, int photoId, ClaimsPrincipal principal, ToolService tools)
    {
        var result = await tools.SetCoverAsync(id, UserId(principal), photoId);
        return FromToolResult(result, () => Results.NoContent());
    }

    private static async Task<IResult> DeletePhotoAsync(int id, int photoId, ClaimsPrincipal principal, ToolService tools)
    {
        var result = await tools.DeletePhotoAsync(id, UserId(principal), photoId);
        return FromToolResult(result, () => Results.NoContent());
    }

    // ================================================================ bookings

    private static BookingDto ToDto(Booking booking, string userId, DateOnly today)
    {
        var iAmOwner = booking.Tool!.OwnerId == userId;
        return new BookingDto(
            booking.Id,
            booking.ToolId,
            booking.Tool.Name,
            iAmOwner ? booking.Borrower!.DisplayName : booking.Tool.Owner!.DisplayName,
            iAmOwner,
            booking.StartDate,
            booking.EndDate,
            booking.Status.ToString(),
            booking.IsOverdueOn(today),
            booking.BorrowerNote,
            booking.OwnerNote);
    }

    private static Task<Booking?> LoadBookingAsync(ApplicationDbContext db, int id) =>
        db.Bookings.AsNoTracking()
            .Include(b => b.Tool).ThenInclude(t => t!.Owner)
            .Include(b => b.Borrower)
            .FirstOrDefaultAsync(b => b.Id == id);

    private static async Task<IResult> RequestBookingAsync(
        int id, BookingRequestInput input, HttpContext http, ClaimsPrincipal principal,
        ApplicationDbContext db, BookingService bookings, BookingNotifier notifier)
    {
        if (!TryValidate(input, out var invalid))
        {
            return Error(StatusCodes.Status400BadRequest, invalid);
        }

        var userId = UserId(principal);
        var result = await bookings.RequestAsync(id, userId, new DateRange(input.StartDate, input.EndDate), input.Note?.Trim());
        if (!result.Succeeded)
        {
            return Error(StatusCodes.Status400BadRequest, result.Error!);
        }

        await notifier.NotifyAsync(result.Booking!.Id, BookingEvent.Requested, userId, LoansUrl(http));
        var booking = await LoadBookingAsync(db, result.Booking.Id);
        return Results.Created($"/api/v1/bookings/{booking!.Id}", ToDto(booking, userId, bookings.Today));
    }

    private static async Task<IResult> ListBookingsAsync(ClaimsPrincipal principal, ApplicationDbContext db, BookingService bookings)
    {
        var userId = UserId(principal);
        var today = bookings.Today;

        var incoming = await db.Bookings.AsNoTracking()
            .Include(b => b.Tool).ThenInclude(t => t!.Owner)
            .Include(b => b.Borrower)
            .Where(b => b.Tool!.OwnerId == userId)
            .OrderBy(b => b.Status == BookingStatus.Requested ? 0 : 1)
            .ThenBy(b => b.StartDate)
            .ToListAsync();

        var outgoing = await db.Bookings.AsNoTracking()
            .Include(b => b.Tool).ThenInclude(t => t!.Owner)
            .Include(b => b.Borrower)
            .Where(b => b.BorrowerId == userId)
            .OrderByDescending(b => b.StartDate)
            .ToListAsync();

        return Results.Ok(new BookingsDto(
            incoming.Select(b => ToDto(b, userId, today)).ToList(),
            outgoing.Select(b => ToDto(b, userId, today)).ToList()));
    }

    private static async Task<IResult> DecideAsync(
        int id, BookingEvent bookingEvent, DecisionInput? input, HttpContext http, ClaimsPrincipal principal,
        ApplicationDbContext db, BookingService bookings, BookingNotifier notifier)
    {
        if (input is not null && !TryValidate(input, out var invalid))
        {
            return Error(StatusCodes.Status400BadRequest, invalid);
        }

        var userId = UserId(principal);
        var note = string.IsNullOrWhiteSpace(input?.Note) ? null : input!.Note!.Trim();

        var result = bookingEvent switch
        {
            BookingEvent.Approved => await bookings.ApproveAsync(id, userId, note),
            BookingEvent.Declined => await bookings.DeclineAsync(id, userId, note),
            BookingEvent.Returned => await bookings.MarkReturnedAsync(id, userId),
            _ => await bookings.CancelAsync(id, userId)
        };

        if (!result.Succeeded)
        {
            return Error(StatusCodes.Status400BadRequest, result.Error!);
        }

        await notifier.NotifyAsync(id, bookingEvent, userId, LoansUrl(http));
        var booking = await LoadBookingAsync(db, id);
        return Results.Ok(ToDto(booking!, userId, bookings.Today));
    }
}
