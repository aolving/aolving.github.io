using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ToolShed.Web.Pages.Account;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
