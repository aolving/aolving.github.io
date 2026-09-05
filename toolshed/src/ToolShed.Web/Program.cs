using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
                      ?? "Data Source=App_Data/toolshed.db"));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;

        // Invitations prove the address, so a member is confirmed the moment they redeem one.
        options.SignIn.RequireConfirmedAccount = true;

        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Password.RequiredUniqueChars = 4;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.Name = "__Host-ToolShed";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    // A password change or a lockout kicks live sessions out within a minute.
    options.ValidationInterval = TimeSpan.FromMinutes(1);
});

builder.Services.AddAuthorization(options =>
{
    // Nothing is public unless a page opts out explicitly.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy("RequireAdmin", policy => policy.RequireRole(Roles.Admin));
});

builder.Services.AddRazorPages(options =>
{
    // Sign-in, sign-out, invitation redemption and the error page opt out with an
    // [AllowAnonymous] attribute each. Deliberately not a folder-wide convention:
    // AllowAnonymous metadata beats an [Authorize] attribute on the same endpoint,
    // so a folder rule would quietly expose /Account/Manage.
    options.Conventions.AuthorizeFolder("/Admin", "RequireAdmin");
});

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-ToolShed-CSRF";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// One upload batch, plus a little slack for the rest of the form. Kestrel has to
// agree with the form limit or it cuts the request off before binding sees it.
const long maxUploadBytes = ImageValidator.MaxBytes * ImageValidator.MaxPhotosPerTool;

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes + 1024 * 1024;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var isCredentialPost = HttpMethods.IsPost(context.Request.Method)
                               && context.Request.Path.StartsWithSegments("/Account", StringComparison.OrdinalIgnoreCase);

        return isCredentialPost
            ? RateLimitPartition.GetFixedWindowLimiter($"auth:{client}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            })
            : RateLimitPartition.GetFixedWindowLimiter($"general:{client}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PhotoStorage>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<BookingService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// Photo bytes never sit in wwwroot; they are streamed to signed-in members only.
app.MapGet("/photos/{id:int}", async (
    int id,
    ApplicationDbContext db,
    PhotoStorage storage,
    CancellationToken cancellationToken) =>
{
    var photo = await db.ToolPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    if (photo is null)
    {
        return Results.NotFound();
    }

    var stream = storage.OpenRead(photo);
    return stream is null
        ? Results.NotFound()
        : Results.File(stream, photo.ContentType, enableRangeProcessing: true);
}).RequireAuthorization();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    await DatabaseInitializer.InitializeAsync(scope.ServiceProvider, app.Configuration, logger);
}

app.Run();
