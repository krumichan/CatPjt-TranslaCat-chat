using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Core;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.MembershipQuery;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Application.OpenRooms;

if (Environment.GetEnvironmentVariable("CHAT_CORE_CONTRACT_TEST") != "true") throw new InvalidOperationException("Explicit test mode required.");
var origin = Environment.GetEnvironmentVariable("CHAT_CORE_CONTRACT_ORIGIN")!;
var key = Environment.GetEnvironmentVariable("CHAT_CORE_CONTRACT_KEY")!;
if (!Uri.TryCreate(origin, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback || endpoint.Scheme != "http")
    throw new InvalidOperationException("Only the owned loopback fixture is allowed.");

// 운영 composition과 같은 client/signer/port alias다. 서버의 계정 repository·storage만 합성이다.
var settings = new Dictionary<string,string?>
{
    ["Chat:Core:Enabled"]="true", ["Chat:Core:BaseUrl"]=origin, ["Chat:Core:TimeoutSeconds"]="5",
    ["Chat:Core:ServiceAuthentication:Issuer"]="translacat-chat", ["Chat:Core:ServiceAuthentication:Audience"]="translacat-be",
    ["Chat:Core:ServiceAuthentication:Service"]="translacat-chat", ["Chat:Core:ServiceAuthentication:Base64SigningKey"]=key
};
var services = new ServiceCollection();
services.AddLogging(logging=>logging.ClearProviders());
services.AddSingleton(TimeProvider.System);
services.AddChatCore(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),"Development");
await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var scoped = scope.ServiceProvider;
int passed = 0;
void Check(bool condition) { if (!condition) throw new InvalidOperationException("Synthetic Core interoperability assertion failed."); passed++; }
const long id = 9007199254740993L;

// 실행/검증: JSON long ID, 현재 계정·nullable profile 및 기존 표시 이름 분기를 확인한다.
var messages = scoped.GetRequiredService<IChatMessageProfileReader>();
var message = await messages.GetUserAsync(id,default);
Check(message.UserId == id && message.Name == "username" && message.Email == "synthetic@example.invalid" && message.ProfileImageUrl is null);
var accounts = scoped.GetRequiredService<IChatRoomAccountReader>();
await accounts.EnsureUserExistsAsync(id,default); passed++;
Check(await accounts.GetAuditIdentityAsync(id,default) == "synthetic@example.invalid");
var direct = await accounts.GetDirectPartnerAsync(id,default);
Check(direct.PublicId == "public-large" && direct.DisplayName == "username" && direct.Online is null);
var directory = scoped.GetRequiredService<IChatMembershipDirectory>();
Check((await directory.FindByIdAsync(id,default))?.Id == id);
Check((await directory.FindByPublicIdAsync("public-large",default))?.Nickname == "username");
Check(await directory.FindByIdAsync(99,default) is null);
Check(!await directory.AreFriendsAsync(id,74,default));
Check(!await directory.IsBlockedBetweenAsync(id,74,default));
var profiles = scoped.GetRequiredService<IChatMemberProfileReader>();
Check((await profiles.GetSummaryAsync(id,default)).Nickname == "username");
Check(await profiles.GetFriendStatusAsync(id,74,default) == "NONE");
Check((await scoped.GetRequiredService<IChatNotificationProfileReader>().GetDisplayAsync(id,default)).DisplayName == "username");
Check(await scoped.GetRequiredService<IChatPresenceProfileReader>().FindPublicIdAsync(id,default) == "public-large");
Check(await scoped.GetRequiredService<IChatAiUserNameReader>().GetUserNameAsync(id,default) == "username");

// raw binary 업로드·URL·삭제는 BE의 실제 validator/controller를 거쳐 테스트 전용 memory storage로 간다.
var objects = scoped.GetRequiredService<IChatProfileImageObjectStore>();
var keyName = "open-chat-profiles/73/"+Guid.NewGuid().ToString("D")+".png";
await objects.StoreAsync(keyName,"image/png",new byte[]{137,80,78,71,13,10,26,10},default); passed++;
Check(await scoped.GetRequiredService<IOpenProfileStorage>().ResolveUrlAsync(keyName,default) == "https://synthetic.invalid/"+keyName);
await objects.DeleteAsync(keyName,default); passed++;

// exact operation scope, prefix, 이미지 검증을 실제 Java 인증/입력 경계에서 거절한다.
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false, UseCookies=false }) { BaseAddress=endpoint };
var tokens = scoped.GetRequiredService<IChatCoreAccessTokenProvider>();
using var wrongScope = new HttpRequestMessage(HttpMethod.Post,"/internal/v1/chat/accounts/lookup")
{
    Content=JsonContent.Create(new { userIds=new[]{id},publicIds=Array.Empty<string>() })
};
wrongScope.Headers.Authorization=new AuthenticationHeaderValue("Bearer",tokens.CreateToken("chat:storage:read"));
using var denied=await http.SendAsync(wrongScope);
Check(denied.StatusCode==HttpStatusCode.Unauthorized);
try { await objects.DeleteAsync("user-profiles/73/owned-by-core.png",default); throw new Exception("Expected rejection."); }
catch (ChatCoreUnavailableException) { passed++; }
try { await objects.StoreAsync(keyName,"image/png",new byte[]{1,2,3},default); throw new Exception("Expected rejection."); }
catch (ChatCoreUnavailableException) { passed++; }
Check(await messages.ResolveObjectUrlAsync(keyName,default) == "https://synthetic.invalid/"+keyName);
Console.WriteLine(JsonSerializer.Serialize(new { probe="chat-be-core-http",passed,failed=0,storage="synthetic-memory",accounts="synthetic-repository",actualProviderCalls=0 }));
