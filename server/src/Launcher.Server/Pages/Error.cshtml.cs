using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Launcher.Server.Pages;

/// <summary>Pagina di errore: risponde anche alle richieste POST rieseguite dopo un errore.</summary>
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public void OnGet()
    {
    }

    public void OnPost()
    {
    }
}
