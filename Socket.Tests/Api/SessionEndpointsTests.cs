using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Socket.Tests.TestInfrastructure;
using SocketWeb.Api;
using SocketWeb.Data;

namespace Socket.Tests.Api;

// Boots the real SocketWeb Program.cs (so this exercises actual routing,
// model binding, and JSON serialization end-to-end) but swaps the real
// MySQL DbContext registration for the EF Core InMemory provider, so
// these tests never need a live database.
public class SessionEndpointsTests : IClassFixture<InMemoryWebApplicationFactory>
{
    private readonly InMemoryWebApplicationFactory _factory;

    public SessionEndpointsTests(InMemoryWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Post_CreatesSession_AndGet_ListsIt()
    {
        var client = _factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/api/sessions", new CreateSessionRequest("My chat"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ChatSessionSummaryDto>();
        Assert.NotNull(created);
        Assert.Equal("My chat", created!.Title);

        var listResponse = await client.GetAsync("/api/sessions");
        listResponse.EnsureSuccessStatusCode();
        var sessions = await listResponse.Content.ReadFromJsonAsync<List<ChatSessionSummaryDto>>();
        Assert.Contains(sessions!, s => s.Id == created.Id);
    }

    [Fact]
    public async Task Post_WithoutTitle_CreatesUntitledSession()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/sessions", new CreateSessionRequest(null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ChatSessionSummaryDto>();
        Assert.Equal(string.Empty, created!.Title);
    }

    [Fact]
    public async Task Post_WithTooLongTitle_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var longTitle = new string('a', 201);

        var response = await client.PostAsJsonAsync("/api/sessions", new CreateSessionRequest(longTitle));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownSession_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_SessionWithMessages_ReturnsThemInOrder()
    {
        var client = _factory.CreateClient();
        var created = await CreateSessionAsync(client, "Chat with history");

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IChatRepository>();
            await repo.AddMessageAsync(created.Id, MessageRoles.User, "Hi");
            await repo.AddMessageAsync(created.Id, MessageRoles.Assistant, "Hello!");
        }

        var response = await client.GetAsync($"/api/sessions/{created.Id}");
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<ChatSessionDetailDto>();

        Assert.Equal(2, detail!.Messages.Count);
        Assert.Equal("Hi", detail.Messages[0].Content);
        Assert.Equal("Hello!", detail.Messages[1].Content);
    }

    [Fact]
    public async Task GetMessages_UnknownSession_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}/messages");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_RenamesSession()
    {
        var client = _factory.CreateClient();
        var created = await CreateSessionAsync(client, "Old title");

        var response = await client.PutAsJsonAsync($"/api/sessions/{created.Id}", new RenameSessionRequest("New title"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var reloaded = await client.GetFromJsonAsync<ChatSessionDetailDto>($"/api/sessions/{created.Id}");
        Assert.Equal("New title", reloaded!.Title);
    }

    [Fact]
    public async Task Put_WithEmptyTitle_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var created = await CreateSessionAsync(client, "Old title");

        var response = await client.PutAsJsonAsync($"/api/sessions/{created.Id}", new RenameSessionRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_UnknownSession_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/api/sessions/{Guid.NewGuid()}", new RenameSessionRequest("New title"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesSession()
    {
        var client = _factory.CreateClient();
        var created = await CreateSessionAsync(client, "To delete");

        var deleteResponse = await client.DeleteAsync($"/api/sessions/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/sessions/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownSession_ReturnsNotFound()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync($"/api/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<ChatSessionSummaryDto> CreateSessionAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new CreateSessionRequest(title));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatSessionSummaryDto>())!;
    }
}
