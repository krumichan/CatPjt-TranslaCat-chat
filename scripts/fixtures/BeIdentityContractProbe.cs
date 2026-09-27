using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.ServiceAuthentication;

// 운영 상대나 기존 자격증명은 사용하지 않는 명시적인 로컬 조합 검증이다.
if (Environment.GetEnvironmentVariable("CHAT_IDENTITY_CONTRACT_TEST") != "true") throw new InvalidOperationException("Explicit local test is required.");
var origin = Environment.GetEnvironmentVariable("CHAT_IDENTITY_CONTRACT_ORIGIN")!;
var key = Environment.GetEnvironmentVariable("CHAT_IDENTITY_CONTRACT_KEY")!;
var settings = new ChatServiceTokenSettings { Issuer = "translacat-chat", Audience = "translacat-be", Service = "translacat-chat", Base64SigningKey = key };
var options = new ChatIdentityOptions { Enabled = true, BaseUrl = origin, TimeoutSeconds = 5, ServiceAuthentication = settings };
using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
var resolver = new HttpChatIdentityResolver(client, Options.Create(options), new ProbeEnvironment(), TimeProvider.System);
var lookup = new ChatIdentityLookup("synthetic@example.invalid", 73);

// 실제 C# client가 실제 BE HTTP/filter/controller에서 현재 synthetic 계정을 조회한다.
var result = await resolver.ResolveAsync(lookup, CancellationToken.None);
Require(result == new ChatResolvedIdentity(73, lookup.Subject, "ROLE_ADMIN", true));
Require(await resolver.ResolveAsync(new("absent@example.invalid", 73), CancellationToken.None) is null);
Require(await resolver.ResolveAsync(new(lookup.Subject, 74), CancellationToken.None) is null);
var passed = 3;

// 방향·환경·scope·서명·사용자 결합을 BE 수신 경계에서 실제 거부하는지 확인한다.
foreach (var scenario in new[] { "wrong-environment", "wrong-scope", "wrong-use", "wrong-key", "different-id", "missing-token" })
{
    var tokenSettings = new ChatServiceTokenSettings { Issuer = settings.Issuer, Audience = settings.Audience, Service = settings.Service,
        Base64SigningKey = scenario == "wrong-key" ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)) : key };
    var token = ChatServiceJwt.Issue(tokenSettings, scenario == "wrong-environment" ? "Production" : "Development",
        scenario == "wrong-use" ? "chat-ingress" : "chat-identity", scenario == "wrong-scope" ? "chat:http" : "chat:identity:read", 73, DateTimeOffset.UtcNow);
    using var request = new HttpRequestMessage(HttpMethod.Post, origin + HttpChatIdentityResolver.IdentityPath)
    {
        Content = new StringContent("{\"subject\":\"synthetic@example.invalid\",\"tokenUserId\":" + (scenario == "different-id" ? "74" : "73") + "}", Encoding.UTF8, "application/json")
    };
    if (scenario != "missing-token") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await client.SendAsync(request);
    Require(response.StatusCode == (scenario == "different-id" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized));
    passed++;
}
Console.WriteLine("{\"probe\":\"actual-chat-client-to-be-identity\",\"passed\":" + passed + ",\"failed\":0,\"accountStore\":\"synthetic\"}");

static void Require(bool result)
{
    if (!result) throw new InvalidOperationException("Identity contract assertion failed.");
}

sealed class ProbeEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "IdentityContractProbe";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
