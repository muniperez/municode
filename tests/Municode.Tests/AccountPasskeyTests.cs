using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Municode.Api.Auth;
using Municode.Common.Contracts;
using Municode.Common.Data;
using Municode.Common.Models;
using Municode.Common.Services;

namespace Municode.Tests;

public sealed class AccountPasskeyTests : IAsyncLifetime
{
    private const string Console = "https://console.example.test";
    private const string Org = "org_passkeys";
    private static readonly string IdentityId = Guid.NewGuid().ToString("D");
    private static readonly string KratosSessionId = Guid.NewGuid().ToString("D");
    private readonly SqliteConnection connection = new($"Data Source=passkeys-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False");
    private readonly FakeKratos kratos = new();
    private Factory factory = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new Factory(connection, kratos);
        factory.CreateClient().Dispose();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkspaceDbContext>();
        await db.Database.MigrateAsync();
        db.Organizations.Add(new() { Id = Org, Name = "Passkeys", Slug = "passkeys" });
        db.Users.Add(new() { Id = "owner", KratosIdentityId = IdentityId, Email = "owner@example.test" });
        db.OrganizationMembers.Add(new() { Id = "member-owner", OrganizationId = Org, UserId = "owner", Role = OrganizationRole.Owner });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task PasskeySignInCreatesABrowserSessionThatApprovesCliLogin()
    {
        using var browser = Client();
        var capabilities = await browser.GetFromJsonAsync<JsonElement>("/api/account/capabilities");
        Assert.True(capabilities.GetProperty("passkey").GetBoolean());

        var started = await browser.GetFromJsonAsync<JsonElement>("/api/account/flows/login/browser?returnTo=%2Fcli%2Flogin%3Fsession%3Dcli_1");
        var nodes = started.GetProperty("flow").GetProperty("ui").GetProperty("nodes").EnumerateArray().ToArray();
        var challenge = Assert.Single(nodes, node => node.GetProperty("group").GetString() == "passkey");
        Assert.Equal("passkey_challenge", challenge.GetProperty("name").GetString());
        Assert.Equal(FakeKratos.Challenge, challenge.GetProperty("value").GetString());
        Assert.DoesNotContain(nodes, node => node.GetProperty("name").GetString() is "passkey_login" or "passkey_login_trigger" or "webauthn_script");
        Assert.Contains(nodes, node => node.GetProperty("name").GetString() == "password");
        Assert.Contains("return_to=https%3A%2F%2Fconsole.example.test%2Fcli%2Flogin%3Fsession%3Dcli_1", kratos.Requests[^1]);

        var signedIn = await SubmitPasskeyAsync(browser, started.GetProperty("flow").GetProperty("id").GetString()!);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        using (var submitted = JsonDocument.Parse(kratos.LoginBodies.Single()))
        {
            Assert.Equal("passkey", submitted.RootElement.GetProperty("method").GetString());
            Assert.Equal(FakeKratos.Assertion, submitted.RootElement.GetProperty("passkey_login").GetString());
            Assert.Equal(["csrf_token", "method", "passkey_login"],
                submitted.RootElement.EnumerateObject().Select(property => property.Name).Order());
        }
        var (cookie, csrf, session) = await ReadSessionAsync(signedIn);
        Assert.False(session.GetProperty("keepSignedIn").GetBoolean());

        using var cli = Client();
        var created = await (await cli.PostAsync("/api/cli-login-sessions", null)).Content.ReadFromJsonAsync<JsonElement>();
        using var approval = Client(cookie, csrf);
        var approved = await approval.PostAsJsonAsync(
            $"/api/cli-login-sessions/{created.GetProperty("sessionId").GetString()}/approve", new { organizationId = Org });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    [Fact]
    public async Task ReauthenticatingTheSignedInUserKeepsTheRememberedSessionChoice()
    {
        string cookie;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<BrowserSessionService>();
            cookie = (await sessions.CreateAsync("owner", IdentityId, KratosSessionId, remembered: true, default)).Cookie;
        }
        using var browser = Client(cookie);
        var started = await browser.GetFromJsonAsync<JsonElement>("/api/account/flows/login/browser?returnTo=%2Fsettings%2Faccount");
        var response = await SubmitPasskeyAsync(browser, started.GetProperty("flow").GetProperty("id").GetString()!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (rotated, _, session) = await ReadSessionAsync(response);
        Assert.True(session.GetProperty("keepSignedIn").GetBoolean());
        Assert.NotEqual(cookie, rotated);
        await using var verify = factory.Services.CreateAsyncScope();
        var user = await verify.ServiceProvider.GetRequiredService<WorkspaceDbContext>().Users.SingleAsync(x => x.Id == "owner");
        Assert.False(user.KeepSignedIn);
    }

    [Fact]
    public async Task SettingsFlowListsRemovablePasskeysAndEnrollmentOptionsOnlyWhenEnabled()
    {
        var flow = await Flows(passkeyEnabled: true)
            .SendAsync("settings", "start", null, "/settings/account", null, new Dictionary<string, string>(), default);
        var passkeys = flow.Flow!.Ui.Nodes.Where(node => node.Group == "passkey").ToArray();
        Assert.Equal(["passkey_create_data", "passkey_remove", "passkey_remove"], passkeys.Select(node => node.Name).Order());
        var named = passkeys.Single(node => node.Value == "0a1b");
        Assert.Equal("MacBook", named.Passkey!.DisplayName);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00Z"), named.Passkey.AddedAt);
        Assert.True(named.Disabled);
        Assert.Null(passkeys.Single(node => node.Value == "ff00").Passkey!.DisplayName);
        Assert.Contains(flow.Flow.Ui.Nodes, node => node.Name == "password");

        var disabled = await Flows(passkeyEnabled: false)
            .SendAsync("settings", "start", null, "/settings/account", null, new Dictionary<string, string>(), default);
        Assert.DoesNotContain(disabled.Flow!.Ui.Nodes, node => node.Group == "passkey");
    }

    [Theory]
    [InlineData(true, "login", """{"csrf_token":"c","method":"passkey","passkey_remove":"0a1b"}""")]
    [InlineData(true, "settings", """{"csrf_token":"c","method":"passkey","passkey_login":"{}"}""")]
    [InlineData(true, "settings", """{"csrf_token":"c","method":"passkey","passkey_remove":"../x"}""")]
    [InlineData(true, "settings", """{"csrf_token":"c","method":"passkey","passkey_settings_register":"not-json"}""")]
    [InlineData(true, "settings", """{"csrf_token":"c","method":"password","password":"p","passkey_remove":"0a1b"}""")]
    [InlineData(true, "settings", """{"csrf_token":"c","method":"passkey","passkey_remove":"0a1b","passkey_settings_register":"{}"}""")]
    [InlineData(false, "login", """{"csrf_token":"c","method":"passkey","passkey_login":"{}"}""")]
    public async Task PasskeySubmissionsAreLimitedToOneOperationOfTheEnabledFlow(bool enabled, string kind, string body)
    {
        using var fields = JsonDocument.Parse(body);
        var error = await Assert.ThrowsAsync<AccountFlowException>(() => Flows(enabled).SendAsync(
            kind, "submit", Guid.NewGuid(), null, fields.RootElement, new Dictionary<string, string>(), default));
        Assert.Equal("auth_flow_invalid", error.Code);
        Assert.Empty(kratos.LoginBodies);
    }

    private KratosBrowserFlowClient Flows(bool passkeyEnabled) => new(new HttpClient(kratos), Options.Create(new KratosOptions
    {
        PublicBaseUrl = "http://kratos:4433/", DashboardOrigin = Console, PasskeyEnabled = passkeyEnabled
    }));

    private static Task<HttpResponseMessage> SubmitPasskeyAsync(HttpClient browser, string flowId) =>
        browser.PostAsync($"/api/account/flows/login?flow={flowId}", JsonContent.Create(new Dictionary<string, string>
        {
            ["csrf_token"] = "flow-csrf", ["method"] = "passkey", ["passkey_login"] = FakeKratos.Assertion
        }));

    private static async Task<(string Cookie, string Csrf, JsonElement Session)> ReadSessionAsync(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';')[0])
            .Single(value => value.StartsWith("__Host-municode-session=", StringComparison.Ordinal))
            .Split('=', 2)[1];
        var session = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session");
        return (cookie, session.GetProperty("csrfToken").GetString()!, session);
    }

    private HttpClient Client(string? cookie = null, string? csrf = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Origin", Console);
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", "__Host-municode-session=" + cookie);
        if (csrf is not null) client.DefaultRequestHeaders.Add("X-Municode-CSRF", csrf);
        return client;
    }

    private sealed class Factory(SqliteConnection connection, FakeKratos kratos) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Municode"] = connection.ConnectionString, ["Console:BaseUrl"] = Console,
                ["Cors:AllowedOrigins:0"] = Console, ["Kratos:PublicBaseUrl"] = "http://kratos:4433/",
                ["Kratos:DashboardOrigin"] = Console, ["Kratos:PasskeyEnabled"] = "true"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IKratosSessionValidator>();
                services.AddSingleton<IKratosSessionValidator>(new ActiveSessions());
                services.AddHttpClient<KratosBrowserFlowClient>().ConfigurePrimaryHttpMessageHandler(() => kratos);
            });
        }
    }

    private sealed class ActiveSessions : IKratosSessionValidator
    {
        public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeKratos : HttpMessageHandler
    {
        public const string Challenge = """{"publicKey":{"challenge":"Y2hhbGxlbmdl","rpId":"console.example.test","userVerification":"preferred"}}""";
        public const string Assertion = """{"id":"AQI","rawId":"AQI","type":"public-key","response":{"authenticatorData":"AA","clientDataJSON":"AA","signature":"AA","userHandle":"AA"}}""";
        private const string CreateData = """{"publicKey":{"rp":{"name":"Municode","id":"console.example.test"},"user":{"id":"dQ","name":"owner@example.test","displayName":"owner@example.test"},"challenge":"Yw"}}""";
        private readonly string loginFlow = Guid.NewGuid().ToString("D");
        private readonly string settingsFlow = Guid.NewGuid().ToString("D");
        public List<string> Requests { get; } = [];
        public List<string> LoginBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests) Requests.Add(path);
            if (request.Method == HttpMethod.Get && path.StartsWith("/self-service/login/browser?", StringComparison.Ordinal))
                return Json(Flow(loginFlow, "login", [
                    Node("default", "csrf_token", "hidden", "flow-csrf"),
                    Node("default", "identifier", "text", ""),
                    new { group = "webauthn", type = "script", attributes = new { id = "webauthn_script", type = "text/javascript" }, messages = Array.Empty<object>(), meta = new { } },
                    Node("passkey", "passkey_login_trigger", "button", ""),
                    Node("passkey", "passkey_login", "hidden", ""),
                    Node("passkey", "passkey_challenge", "hidden", Challenge),
                    Node("password", "password", "password", ""),
                    Node("password", "method", "submit", "password")
                ]));
            if (request.Method == HttpMethod.Post && path == $"/self-service/login?flow={loginFlow}")
            {
                LoginBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(new { session = new { id = KratosSessionId } });
            }
            if (request.Method == HttpMethod.Get && path == "/sessions/whoami")
                return Json(new { id = KratosSessionId, active = true, identity = new { id = IdentityId } });
            if (request.Method == HttpMethod.Get && path.StartsWith("/self-service/settings/browser?", StringComparison.Ordinal))
                return Json(Flow(settingsFlow, "settings", [
                    Node("default", "csrf_token", "hidden", "flow-csrf"),
                    Node("password", "password", "password", ""),
                    Node("password", "method", "submit", "password"),
                    Node("passkey", "passkey_remove", "submit", "0a1b", disabled: true,
                        context: new { display_name = "MacBook", added_at = "2026-11-20T10:00:00Z" }),
                    Node("passkey", "passkey_remove", "submit", "ff00", context: new { display_name = "unnamed" }),
                    Node("passkey", "passkey_remove", "submit", "not-hex"),
                    Node("passkey", "passkey_register_trigger", "button", ""),
                    Node("passkey", "passkey_settings_register", "hidden", ""),
                    Node("passkey", "passkey_create_data", "hidden", CreateData)
                ]));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static object Flow(string id, string kind, object[] nodes) => new
        {
            id, type = "browser", expires_at = DateTimeOffset.UtcNow.AddMinutes(10),
            ui = new { action = $"http://kratos:4433/self-service/{kind}?flow={id}", method = "POST", nodes, messages = Array.Empty<object>() }
        };

        private static object Node(string group, string name, string type, string value, bool disabled = false, object? context = null) => new
        {
            group, type = "input",
            attributes = new { name, type, value, required = false, disabled },
            messages = Array.Empty<object>(),
            meta = new { label = context is null ? null : new { id = 1050020, text = "Remove passkey", type = "info", context } }
        };

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
