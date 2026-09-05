# The Tool Shed

An invite-only portal where members list the tools they own, photograph them, and
book each other's tools on loan.

ASP.NET Core 8 Razor Pages, ASP.NET Core Identity, EF Core on SQLite. No JavaScript,
no CDN, no third-party services — it runs from one process and one file.

## Running it

```bash
cd toolshed
dotnet restore
dotnet user-secrets --project src/ToolShed.Web set "Seed:AdminEmail" "you@example.com"
dotnet run --project src/ToolShed.Web
```

Then open <https://localhost:7181>.

On the first run the database is created and a single administrator account is
made from `Seed:AdminEmail`. If you did not also set `Seed:AdminPassword`, a
random password is written **once** to the startup log — sign in with it and
change it under *My account*. After that first account, every other account
must come from an invitation.

Run the tests with `dotnet test`.

## How the invite flow works

1. An admin opens **Invitations**, enters an email address, picks a role and a
   lifetime (1–30 days).
2. The portal generates a 256-bit token and shows the sign-up link **once**.
   Only the SHA-256 hash of the token is stored, so the link cannot be recovered
   from the database — copy it and send it to that person.
3. The recipient opens the link and sets a display name and a password. The
   email address comes from the invitation, not from the form, so a leaked link
   cannot be pointed at a different mailbox.
4. The invitation is marked spent. Re-inviting the same address automatically
   revokes any invitation still outstanding for it.

There is no open registration page: `/Account/Register` without a live token
shows nothing but an explanation.

There is no email sender wired up, deliberately — a household-sized portal does
not need SMTP credentials sitting in config. If you want invitations mailed
automatically, implement `IEmailSender` and send `issued.Token` from
`Pages/Admin/Invitations.cshtml.cs` instead of displaying it.

## How booking works

A loan is an inclusive range of days with a status: `Requested`, `Approved`,
`Declined`, `Cancelled` or `Returned`. Requested and approved loans both hold
their dates; the rest release them.

- Members request dates on a tool's page. Owners approve, decline, cancel, or
  mark a tool returned from **Loans**.
- Overlaps are rejected. The check and the insert run in one transaction, so two
  members racing for the same weekend cannot both win.
- Approving a request auto-declines any other request competing for the same days.
- You cannot book your own tool, book in the past, book more than 180 days ahead,
  or exceed the owner's per-loan day limit.
- Owners can pause a tool (unlist it) without deleting it or its history.

The date maths lives in `Services/DateRange.cs` and
`BookingService.ValidateRange`, both free of EF and HTTP so they are unit tested
directly.

## Security posture

| Concern | How it is handled |
| --- | --- |
| Who can get in | Invitation only. Single use, email-bound, expiring, stored hashed. |
| Default access | `FallbackPolicy` requires an authenticated user, so a new page is private unless it opts out. |
| Admin pages | `/Admin` is gated by a `RequireAdmin` role policy. |
| Passwords | 12 characters minimum, mixed case + digit + symbol, 4 unique characters; hashed by Identity (PBKDF2). |
| Brute force | Identity lockout after 5 failures for 15 minutes, plus an IP rate limit of 10 POSTs per 5 minutes on `/Account`. |
| Account enumeration | Sign-in failures are indistinguishable — wrong password, unknown address and unconfirmed account give one message. |
| Session safety | `__Host-` prefixed cookie, HttpOnly, `Secure`, `SameSite=Lax`, 8-hour sliding expiry; security-stamp revalidation every minute, so a password change or a suspension drops live sessions. |
| CSRF | Antiforgery on every POST (Razor Pages default), `SameSite=Strict` on the antiforgery cookie. |
| Photo uploads | Type decided by magic bytes, not by the file name or the browser's `Content-Type`. JPEG/PNG/GIF/WebP only — **SVG is rejected**, since it can carry script. 8 MB and 6 photos per tool. |
| Stored files | Written outside `wwwroot` under a server-generated GUID name, so nothing is statically served and the uploaded name is never reused. Served only to signed-in members via `/photos/{id}`. |
| XSS | Razor encodes by default; a strict CSP (`script-src 'self'`, no `unsafe-inline`) backs it up, which is why the app ships no inline script or style. |
| Clickjacking | `frame-ancestors 'none'` and `X-Frame-Options: DENY`. |
| Transport | HTTPS redirection and HSTS (1 year, subdomains) outside Development. |
| Authorisation | Every tool and booking handler loads the row scoped to the caller's id, so an id in the URL cannot reach someone else's record. |
| Losing a member | Admins suspend an account from **Members**: sign-in is blocked and live cookies stop working, while their tools and loan history stay intact. |

### Before you put it on the internet

- Put it behind a reverse proxy with a real certificate, and set `AllowedHosts`
  to your own hostname instead of `*`.
- Set `Seed:*` values through user secrets or environment variables, never in
  `appsettings.json`.
- ASP.NET Core Identity's data-protection keys live in the OS key ring by
  default. If you deploy to a container, persist them
  (`PersistKeysToFileSystem`) or every restart signs everyone out.
- Back up `App_Data/` — it holds both the SQLite database and the photos.

## Moving to EF Core migrations

Startup calls `EnsureCreated()`, which is fine while the schema is settling but
cannot evolve a database that already has data in it. When you are ready:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src/ToolShed.Web
```

Then swap `EnsureCreatedAsync()` for `MigrateAsync()` in
`Data/DatabaseInitializer.cs`.

## Layout

```
toolshed/
  src/ToolShed.Web/
    Program.cs                  composition root: Identity, policies, rate limits, /photos endpoint
    Data/                       DbContext and first-run bootstrap
    Models/                     ApplicationUser, Tool, ToolPhoto, Booking, Invitation
    Services/                   invitations, photo storage, image sniffing, booking rules, security headers
    Pages/                      Razor Pages (Account, Tools, Bookings, Admin)
  tests/ToolShed.Tests/         booking rules, date maths, upload sniffing, invitation tokens
```
