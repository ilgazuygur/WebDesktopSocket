using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SocketWeb.Data;

namespace Socket.Tests.TestInfrastructure;

// Boots the real SocketWeb Program.cs (so integration tests exercise
// actual routing, DI wiring, and the real /ws endpoint) but swaps the
// real MySQL DbContext registration for the EF Core InMemory provider, so
// these tests never need a live database. Each instance gets its own
// isolated in-memory database.
public class InMemoryWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program.cs reads ConnectionStrings:ChatDb at startup and throws
        // if it's missing - this placeholder just satisfies that check;
        // the DbContext registration below replaces the MySQL provider
        // entirely before anything tries to use it.
        builder.UseSetting("ConnectionStrings:ChatDb", "Server=unused;Database=unused;User=unused;Password=unused;");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ChatDbContext>>();
            services.AddDbContext<ChatDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}
