using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ToolShed.Web.Data;
using ToolShed.Web.Models;
using ToolShed.Web.Services;

namespace ToolShed.Tests;

/// <summary>A clock the tests can pin, so "today" never depends on when the suite runs.</summary>
public class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>
/// A real SQLite database held in memory, so queries are translated by the same provider
/// the portal runs on rather than by an in-memory fake that would accept anything.
/// </summary>
public sealed class BookingFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public BookingFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Db = NewContext();
        Db.Database.EnsureCreated();

        Owner = AddUser("owner", "Olive Owner");
        Borrower = AddUser("borrower", "Ben Borrower");
        OtherBorrower = AddUser("other", "Ola Other");

        Tool = new Tool { OwnerId = Owner.Id, Name = "Cordless drill", Category = "Power tools", MaxLoanDays = 7 };
        Db.Tools.Add(Tool);
        Db.SaveChanges();

        Bookings = new BookingService(Db, Clock, NullLogger<BookingService>.Instance);
    }

    public FixedClock Clock { get; } = new();

    public ApplicationDbContext Db { get; }

    public BookingService Bookings { get; }

    public ApplicationUser Owner { get; }

    public ApplicationUser Borrower { get; }

    public ApplicationUser OtherBorrower { get; }

    public Tool Tool { get; }

    public DateOnly Today => Bookings.Today;

    public static DateRange Days(DateOnly start, int length) => new(start, start.AddDays(length - 1));

    private ApplicationDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

    private ApplicationUser AddUser(string name, string display)
    {
        var user = new ApplicationUser { UserName = name, Email = $"{name}@example.test", DisplayName = display };
        Db.Users.Add(user);
        Db.SaveChanges();
        return user;
    }

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
