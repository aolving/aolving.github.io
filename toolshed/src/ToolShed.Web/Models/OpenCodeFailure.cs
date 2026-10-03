namespace ToolShed.Web.Models;

/// <summary>
/// One wrong guess at an open access code. Open codes have no email address to lock against, so wrong
/// guesses are counted across the whole portal and trip a circuit breaker (see InvitationService).
/// </summary>
public class OpenCodeFailure
{
    public int Id { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}
