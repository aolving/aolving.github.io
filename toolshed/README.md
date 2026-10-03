# The Tool Shed

An invite-only portal where members list the tools they own, photograph them, and
book each other's tools on loan.

ASP.NET Core 8 Razor Pages, ASP.NET Core Identity, EF Core on SQLite. No JavaScript,
no CDN, no third-party services — it runs from one process and one data folder.

## Running it locally

```bash
cd toolshed
dotnet user-secrets --project src/ToolShed.Web set "Seed:AdminEmail" "you@example.com"
dotnet run --project src/ToolShed.Web
```

Then open <https://localhost:7181> (trust the dev certificate with
`dotnet dev-certs https --trust` if the browser complains).

On the first run the database is created and a single administrator is made from
`Seed:AdminEmail`. If you did not also set `Seed:AdminPassword`, a random password is
written **once** to the startup log — sign in with it and change it under *My account*.
That is the only account that does not come from an invitation.

Run the tests with `dotnet test`.

## Deploying it

You need a machine with Docker and a hostname pointing at it.

```bash
cd toolshed
cp .env.example .env     # set PORTAL_DOMAIN and ADMIN_EMAIL (SMTP is optional)
docker compose up -d
docker compose logs toolshed    # the generated admin password is printed here, once
```

Compose runs the portal plus [Caddy](https://caddyserver.com), which obtains and renews
a TLS certificate for `PORTAL_DOMAIN`. The portal is not published on any host port, so
Caddy is the only way in. Everything that matters — database, photos, signing keys — is
in the `toolshed-data` volume; **back that up**.

Behind a proxy the app trusts `X-Forwarded-For`/`Proto` (`Hosting__BehindProxy=true`,
set in the Dockerfile). Only do that when the app is reachable solely through your
proxy, otherwise a visitor could forge their address and sidestep the rate limiter.

The signing keys are stored unencrypted in `App_Data/keys`. That is fine inside a
private volume; if you want them encrypted at rest, configure
`ProtectKeysWith…` in `Program.cs` for your platform.

GitHub Pages cannot host this: it only serves static files, and this is a server
application. The `index.html` at the repository root is just a landing page.

### Configuration

Any setting can be given in `appsettings.json`, user secrets, or an environment
variable (use `__` for `:`).

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings:DefaultConnection` | SQLite database (default `App_Data/toolshed.db`) |
| `Storage:PhotoRoot`, `Storage:KeysDirectory` | Where photos and signing keys live |
| `Seed:AdminEmail`, `Seed:AdminPassword`, `Seed:AdminDisplayName` | First administrator, used only while no members exist |
| `Portal:Name`, `Portal:Tagline` | Branding |
| `AllowedHosts` | Set to your hostname in production |
| `Hosting:BehindProxy` | Trust `X-Forwarded-*` from a reverse proxy |
| `RateLimits:AuthPermits` | Sign-in/reset POSTs per IP per five minutes (default 10) |
| `Smtp:Host`, `Port`, `UseStartTls`, `Username`, `Password`, `FromAddress`, `FromName` | Optional email |

### Email is optional

With no `Smtp:Host` everything still works: invitation and reset links are shown on
screen for you to pass on. With it configured, the portal also emails invitations,
password-reset links, and loan updates (a request to the owner; approval, decline and
return to the borrower; a cancellation to whoever did not cancel). A failed send never
fails the action it belongs to.

## How the invite flow works

1. An admin opens **Invitations**, enters an email address, picks a role and a
   lifetime (1–30 days).
2. The portal generates a 256-bit token and shows the sign-up link **once** (and emails
   it, if SMTP is set up). Only the SHA-256 hash is stored, so the link cannot be
   recovered from the database.
3. The recipient opens the link and sets a display name and password. The address
   comes from the invitation, not the form, so a leaked link cannot be pointed at a
   different mailbox.
4. The invitation is spent. Re-inviting an address revokes any invitation still
   outstanding for it, and an admin can revoke one at any time.

There is no open registration page: `/Account/Register` without a live token shows
nothing but an explanation.

## Looking after members

Under **Members** an admin can suspend and restore accounts (sign-in is blocked and live
sessions end within a minute, but tools and loan history stay), promote or demote
between *Member* and *Admin*, and issue a **password reset link**. An admin cannot
suspend or change the role of their own account, so the portal can never be left with
no admin.

Members can also reset their own password from the sign-in page when email is set up.
Reset links last two hours, work once, and the form gives the same answer whether or
not an address belongs to a member.

## How booking works

A loan is an inclusive range of days with a status: `Requested`, `Approved`,
`Declined`, `Cancelled` or `Returned`. Requested and approved loans both hold their
dates; the rest release them.

- Members request dates on a tool's page, which also shows a six-week availability
  calendar. Owners approve or decline (with an optional note to the borrower), cancel,
  or mark a tool returned from **Loans**.
- Overlaps are rejected. The check and the insert share a transaction, so two members
  racing for the same weekend cannot both win.
- Approving a request auto-declines any other request for the same days.
- You cannot book your own tool, book in the past, more than 180 days ahead, or beyond
  the owner's per-loan day limit.
- An approved loan whose last day has passed and that has not been marked returned is
  flagged **Overdue** for both parties.
- Owners can pause a tool without deleting it or its history.

## Security posture

| Concern | How it is handled |
| --- | --- |
| Who can get in | Invitation only. Single use, email-bound, expiring, stored hashed. |
| Default access | The authorization *fallback* policy requires a signed-in user, so a new page is private unless it opts out. |
| Admin pages | `/Admin` is gated by a `RequireAdmin` role policy. |
| Passwords | 12+ characters, mixed case, digit and symbol; hashed by Identity (PBKDF2). |
| Brute force | Identity lockout after 5 failures for 15 minutes, plus an IP rate limit on `/Account` POSTs. |
| Account enumeration | Sign-in failures and password-reset requests give the same answer whether or not the address is a member. |
| Sessions | `__Host-` cookie, HttpOnly, `Secure`, `SameSite=Lax`, 8-hour sliding expiry; the security stamp is rechecked every minute, so a password change, role change or suspension drops live sessions. |
| CSRF | Antiforgery on every POST, `SameSite=Strict` on the antiforgery cookie. |
| Photo uploads | Type decided by magic bytes, not file name or `Content-Type`. JPEG/PNG/GIF/WebP only — **SVG is rejected**, since it can carry script. 8 MB and 6 photos per tool. |
| Stored files | Written outside `wwwroot` under a generated GUID name; served only to signed-in members via `/photos/{id}`. |
| XSS | Razor encodes by default; a strict CSP (`script-src 'self'`, no inline script or style) backs it up. |
| Clickjacking | `frame-ancestors 'none'` and `X-Frame-Options: DENY`. |
| Transport | HTTPS redirection and HSTS outside Development. |
| Authorisation | Every tool and booking handler loads its row scoped to the caller's id, so an id in a URL cannot reach someone else's record. |
| Email | Subjects have line breaks stripped, so member-supplied text cannot inject headers. |

## Database migrations

The schema is managed by EF Core migrations in `src/ToolShed.Web/Data/Migrations` and
applied automatically at startup. To change the model:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Name> --project src/ToolShed.Web --output-dir Data/Migrations
```

## Tests and CI

`dotnet test` runs three layers:

- **Rule tests** — date maths, loan-window validation, upload sniffing, token handling.
- **Service tests** — the booking rules against a real in-memory SQLite database, so
  queries are translated by the same provider the portal runs on.
- **Integration tests** — the real app is hosted and driven over HTTP: who gets in, the
  invitation flow, photo handling, a full loan from request to return, password reset,
  roles and suspension.

GitHub Actions (`.github/workflows/toolshed-ci.yml`) builds and tests on every push,
then builds the Docker image and smoke-tests the running container.

## Layout

```
toolshed/
  Dockerfile, docker-compose.yml, Caddyfile, .env.example
  src/ToolShed.Web/
    Program.cs                  composition root: Identity, policies, rate limits, /photos endpoint
    Data/                       DbContext, migrations, first-run bootstrap
    Models/                     ApplicationUser, Tool, ToolPhoto, Booking, Invitation
    Services/                   invitations, bookings, notifications, email, photo storage, headers
    Pages/                      Razor Pages (Account, Tools, Bookings, Admin)
  tests/ToolShed.Tests/         rule, service and integration tests
```
