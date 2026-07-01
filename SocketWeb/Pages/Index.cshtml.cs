using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SocketWeb.Pages;

// There isn't much server-side logic here — this page is mostly just a
// shell. All the real-time behavior happens in the browser via
// wwwroot/js/socket-client.js talking to the /ws WebSocket endpoint.
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
