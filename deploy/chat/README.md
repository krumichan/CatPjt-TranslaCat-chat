# CHAT 전용 실행 구성 — 배포 준비

현재 로컬 상태(2026-09-27): 실제 계정/BE proxy/CHAT/MySQL9.5/Redis/Chrome 검증 후 BE 구 Chat 소스와 로컬14table을 제거했다. 운영은 미배포다. 검증용 서비스는 정지했고 DB·개발키는 보존했다. 최신 재실행 방법과 LL 소유 옛 runtime 잔류는 [Fresh Start 최종 보고](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/migration/chat/10-fresh-start-and-legacy-removal.md)를 따른다.

로컬 검증 산출물은 CHAT 저장소 기준 `../.codex-workspace/verification/chat/TestResults/`에 모은다. 이 문서의 과거 실행 기록에 있는 `TestResults/...` 경로는 같은 하위 경로의 중앙 보관본을 가리킨다. 실행 스크립트와 manifest 경계 검사도 중앙 경로를 사용한다.

상태: `NOT_DEPLOYED / CUTOVER_PENDING`. 이 디렉터리는 실행 구성을 준비하고 격리 검증한 산출물이다. 2026-09-27 LL 수정권 인계 뒤 실제 BE identity/공통 profile·relationship·storage HTTP adapter와 방향별 인증을 연결했다. 최신 실행 결과는 [환경·인증 장부](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/operations/configuration-and-secrets.md)를 따른다. 기본 예제는 상대 서비스 기능을 활성화하지 않으므로 키·DB·Redis 값만 넣어도 `/api/ready`는 `503 NOT_READY`이며, 이를 가짜 계정이나 인증 우회로 해제하지 않는다.

현재 구현·검증·인계 상태는 [07 장부](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/migration/chat/07-autonomous-migration.md), [08 인계](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/migration/chat/08-shared-handoff.md), [09 DB/실행 책임](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/migration/chat/09-chat-data-and-runtime.md)을 함께 확인한다. 이 README만으로 배포·기존 데이터 이관·BE 트래픽 전환을 승인하지 않는다.

## 구성과 격리

- `chat-api`: SDK `10.0.401-noble`로 publish하고 ASP.NET runtime `10.0.12-noble`에서 non-root 실행한다. Linux image 태그는 [SDK 공식 registry](https://mcr.microsoft.com/v2/dotnet/sdk/tags/list)와 [공식 ASP.NET image 목록](https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md)에서 확인했다. 패키지는 기존 CHAT 프로젝트의 고정 버전을 사용한다.
- `chat-redis`: `redis:7.4.10-alpine`와 로컬 확인 digest `sha256:e7723ff73d963f5cc6d9c4643ea3d989527a402a319239054e9472a7fb9219a2`를 고정한다. ASP.NET과 별도 process/container다. Redis는 호스트에 port를 publish하지 않고 internal network에만 연결한다.
- API만 기본 `127.0.0.1:5085`에서 접근한다. BE proxy의 로컬 합성 검증과 공개 ingress/TLS·실제 사용자 트래픽 검증은 구분한다. Redis와 API를 같은 host에 놓는 구성은 HA가 아니다.
- `chat-private`는 Redis 통신용이다. API의 `chat-egress`는 소유 서비스/DB에 연결할 수 있게 분리했다. Compose가 BE/LL의 network·DB·Redis를 재시작하거나 재사용하지 않는다.
- 별도 DB container는 여기에 포함하지 않았다. 격리 MySQL 검증은 기존 `scripts/Start-ChatIntegrationRuntime.ps1` 등 CHAT runtime 도구를 사용한다. 최종 업무 catalog는 `translacat_chat`이며 API startup은 migration/seed/DB 복사를 수행하지 않는다.

## 비밀 설정

`deploy/chat/.env.local.example`은 `Development`, `.env.prod.example`은 `Production`을 사용한다. 예제에는 비밀 원문이나 실제 운영 host/zone/path가 없으며, 필수 빈 값 때문에 그대로 실행할 수 없다. `.env.example`은 두 템플릿을 안내하는 호환 파일이다. 실제 값은 ignored `.env.local`/`.env.prod`에, secret은 저장소 밖 또는 ignored `.secrets/development` 등에 별도 보관한다. 파일 접근 권한은 해당 운영자와 runtime만 읽도록 제한한다. Compose secret은 서비스별 파일 mount로 전달하며 비밀번호를 Compose environment 값이나 Dockerfile에 넣지 않는다. [Docker Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/)

| 파일 | 내용 | 주의 |
|---|---|---|
| `chat_redis_password` | 암호학적 난수 32 byte의 64자리 hex 문자열 | 줄바꿈 외 공백/쉼표 불가. 예제에 고정 비밀번호 없음. |
| `chat_database_connection` | MySQL connection string, `Database=translacat_chat` | CHAT catalog에 필요한 runtime 권한만 부여. CREATE/DROP/migration 계정과 분리. |
| `chat_jwt_signing_key` | 기존 발급 계약과 일치하는 Base64 서명 키 | 새 사용자/토큰 발급 체계를 만들지 않는다. 키만으로 계정 adapter가 구현되지는 않는다. |
| `chat_ingress_signing_key` | BE→CHAT HS256 전용 Base64 키 | 내부 auth overlay에서 mount. 사용자 JWT와 다른 방향 키 재사용 금지. |
| `chat_identity_signing_key` | CHAT→BE HS256 전용 Base64 키 | identity/Core가 같은 호출 방향을 공유하되 tokenUse/scope를 분리. |
| `chat_ai_api_key` | CHAT→AI 전용 raw API key 문자열 | reply/translation 두 경로만, AI global/LL key 재사용 금지. |

`start-chat-api.sh`는 secret 원문을 읽거나 환경변수로 복사하지 않는다. `Chat__Database__ConnectionString_FILE`, `Chat__Authentication__Base64SigningKey_FILE`, `Chat__Redis__Password_FILE`에 `/run/secrets/...` 경로를 전달하며, 실제 managed configuration provider가 읽는다. `_FILE`은 .NET의 자동 기능이 아니라 CHAT의 allowlist 구현이다. 동일 secret의 값과 파일을 동시에 주면 거절하고, 파일은 최대16KiB/끝부분 줄바꿈 제거 규칙을 적용한다. 환경값/CLI보다 파일이 무조건 우선하는 것으로 설명하지 않는다. 자세한 provider 순서는 [설정·비밀정보 계약](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/operations/configuration-and-secrets.md)에 기록한다.

`DOTNET_ENVIRONMENT`가 실행 기준이며 함께 지정한 `ASPNETCORE_ENVIRONMENT`는 정확히 같아야 한다. shell과 managed host가 상충값 및 `Local`/`Prod` 별칭을 거절한다. shell은 인자를 그대로 managed entrypoint에 전달한다. `launchSettings.json`은 이미지나 Production에 포함하지 않는다. API image에는 비밀 없는 `appsettings.json`, `appsettings.Development.json`, `appsettings.Production.json` 세 파일만 정확한 대소문자로 포함한다. User Secrets/실제 env/private key/TestResults/Git metadata는 Docker context에서 제외한다. Git ignore와 Docker ignore는 별도로 검사한다.

Redis는 `Chat:Redis:Endpoint=chat-redis:6379`, `User=chat`, `Password_FILE`, `UseTls=false`, `AllowPrivatePlaintext=true`를 기본 Compose의 internal network에서만 명시한다. 기존 ConnectionString과 structured 설정은 함께 주지 않는다. 실제 외부 Redis를 연결할 경우 endpoint/ACL/TLS와 network 정책을 별도로 구성·검증해야 한다. 현재 Compose에는 외부 Redis port나 TLS 종료를 자동으로 열지 않는다. Docker 관리자/호스트 root가 secret mount와 프로세스 메모리에 접근할 수 있는 신뢰 경계는 남는다.

`CHAT_SOURCE_TIME_ZONE`은 실제 BE/DB 시각 정책을 확인한 명시 값이어야 하므로 공개 예제에서는 비워 둔다. 검증 도구의 `Etc/UTC`는 합성 fixture 예시이며 기존 데이터의 시간대 확정이 아니다. `CHAT_REDIS_NAMESPACE`는 동일 환경의 모든 CHAT replica에서 같게, 다른 환경에서는 다르게 사용한다. Redis DB 번호 변경은 채널 격리를 대신하지 않는다.

## 방향별 연결과 개발 키 도구

`compose.internal-auth.yaml`은 기본 Compose와 함께 제공 서비스의 내부 인증을 연결한다. BE→CHAT ingress, CHAT→BE identity/Core, CHAT→AI의 세 secret 파일을 각각 mount한다. AI reply와 translation은 같은 CHAT→AI 방향의 API key를 공유할 수 있지만 사용자 JWT 키나 반대 방향의 서비스 키와 같아서는 안 된다. 제공 서비스와 인증의 `Enabled`는 true로 고정하고 이를 끄던 `CHAT_*_ENABLED` 환경변수는 사용하지 않는다. 실제 상대 origin/issuer/audience/service와 `CHAT_BROWSER_ORIGIN`을 공급해야 하며, 누락된 키·주소는 기존 validator가 거절한다. `CHAT_ALLOWED_HOSTS`는 실제 API로 전달되는 Host를 허용하고 `*`로 자동 완화하지 않는다. secret mount만으로 실제 서비스 연결 완료라고 하지 않는다.

```powershell
# Development 전용 신규 파일 생성. 기존 파일이 하나라도 있으면 덮어쓰지 않는다.
./scripts/New-ChatDevelopmentKeys.ps1 -Environment Development

# 별도 준비한 User Secrets로 직접 dotnet을 실행한다. 검사만 할 때는 -ValidateOnly를 붙인다.
./scripts/Start-ChatLocal.ps1 -Environment Development

# 선택 JSON은 flat 환경변수 사전이다. 비밀은 절대 _FILE 경로로만 주고 기존 빌드를 사용할 수 있다.
./scripts/Start-ChatLocal.ps1 -Environment Development -Port 5079 -ConfigurationFile C:/private/chat-local-environment.json -NoBuild

# 구성만 검사한다. 서비스 기동이나 Production key 생성 명령이 아니다.
docker compose --env-file deploy/chat/.env.local -f deploy/chat/compose.yaml config --quiet
docker compose --env-file deploy/chat/.env.prod -f deploy/chat/compose.yaml -f deploy/chat/compose.internal-auth.yaml config --quiet
```

개발 키 도구는 누락된 신규 내부 방향키3종만32byte CSPRNG로 생성하고 현재 사용자 전용 directory에 저장한다. 기존 파일은 재사용하며 반복 실행해도 바뀌지 않는다. `-DryRun`은 생성/공급 예정 상태만 반환한다. 보호된 기존 원본을 메모리로 전달하는 `-ExistingValues`는 같은 방향의 누락 수신 파일만 원자적으로 채우며 mismatch에서는 덮어쓰지 않는다. 사용자 JWT는 기존 원본 재사용만 허용하고 Redis/DB 비밀번호는 생성하지 않는다. 전용 Redis 실행 스크립트가 요구하는 기존64자리hex 비밀번호를 따로 공급해야 한다. 서비스 HMAC은 Base64 decode32..128bytes, AI key는 opaque 문자열 그대로 소비한다. 원문/hash/token을 출력하지 않고 Production 생성은 거절한다. 현재 일상 실행은 `scripts/Initialize-ChatDevelopment.ps1`과 `scripts/Start-ChatDevelopment.ps1`, 공개 `deploy/local/Development.json`을 사용한다. 상세 대응과 실제 검증은 [중앙 실행 안내](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/operations/configuration-and-secrets.md)와 [설정 대응표](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/operations/environment-variable-matrix.md)를 따른다.

Visual Studio/직접 `dotnet`의 기본 개발 secret 저장 방식은 User Secrets이며 Docker로 자동 전달되지 않는다. User Secrets는 개발 PC의 비암호화 저장소다. Compose `.env`는 interpolation 입력이고, `environment`/secret mount로 명시한 값만 컨테이너에 전달된다. 두 실행 경로의 설정을 혼동하지 않는다. [User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0), [Compose environment](https://docs.docker.com/compose/how-tos/environment-variables/set-environment-variables/)

`Start-ChatLocal.ps1 -ConfigurationFile`은 절대 경로의 UTF-8 JSON 객체를 받는다. 모든 값은 문자열이며 키는 실제 CHAT Options의 `Chat__...`, `Logging__LogLevel__...`, `AllowedHosts`, 두 환경명과 `ASPNETCORE_URLS`로 제한한다. 예를 들어 `{"Chat__Database__ConnectionString_FILE":"C:/private/chat-database-connection","Chat__Core__Enabled":"true","Chat__Core__BaseUrl":"http://127.0.0.1:8080"}`처럼 사용한다. JSON의 DB/Redis 연결 문자열·password·서명 키·API key 원문, 중복 키, 임의 process 환경, 상대/없는 secret 파일 경로는 거절한다. `ASPNETCORE_URLS`를 지정하면 `-Port`의 `http://127.0.0.1:<port>`와 정확히 같아야 한다. JSON 설정은 자식 실행에만 공급하고 성공·실패 뒤 호출 process의 환경을 복원한다. `-ValidateOnly`는 실행 인자/파일 구조 검사이며 managed Options의 전체 유효성, secret 내용과 실제 연결을 검사하지 않는다. `scripts/Test-ChatLocalLaunch.ps1`은 서버를 시작하지 않는 합성 실행 sink로 전달·거절·복원을 검증한다.

별도 migration 권한의 `CHAT_DATABASE_CONNECTION_FILE`을 process 환경에 공급한 뒤 schema를 준비할 때는 다음 기존 Infrastructure design-time factory를 사용한다. API 프로젝트에는 EF Design 참조가 없으므로 startup project를 API로 지정하지 않는다. 이 명령은 runtime DML 자격증명이나 서버 startup에 자동 연결하지 않는다.

```powershell
dotnet ef database update --project TranslaCat.Chat.Infrastructure --startup-project TranslaCat.Chat.Infrastructure --no-build
```

`Test-ChatDeploymentConfiguration.ps1 [-IncludeCompose]`는 Git ignore 판정, 비밀 없는 예제, Development key의32byte/방향 분리/기존값 보존/Production 거절과 선택 Compose 매핑을 검사한다. `Test-ChatDeploymentImage.ps1 -Image <local-tag>`는 합성 sentinel만 들어 있는 context를 Docker의 실제 ignore 규칙으로 빌드하고, 준비된 Linux image의3개 JSON 포함/private 경로 제외를 읽기 전용으로 검사한다. 이 검사는 실제 secret 내용을 복사하지 않고 DB/Provider를 호출하지 않는다. 최종 이미지의 application startup smoke는 기존 `Test-ChatDeploymentStartup.ps1`을 따로 실행한다.

## Redis 권한과 데이터

시작 스크립트는 secret이나 namespace가 없거나 잘못되면 종료한다. `default` 사용자는 차단하고 `chat` 사용자에게 해당 namespace의 key/channel과 Presence·Pub/Sub에 필요한 명령만 허용한다. ACL 파일에는 SHA-256 password hash만 기록한다. `FLUSHDB`, `FLUSHALL`, `CONFIG SET`, 다른 서비스 key/channel은 허용하지 않는다. ACL은 key와 Pub/Sub channel을 별도로 제한한다. [Redis ACL](https://redis.io/docs/latest/operate/oss_and_stack/management/security/acl/)

| 분류 | Key/channel | 성격·만료·복구 |
|---|---|---|
| Session lease | `<scope>:user:{id}:session:<sessionId>` | 연결 상태, 기본 TTL 60초. 살아 있는 local socket만 20초마다 refresh. |
| 사용자 session index | `<scope>:user:{id}:sessions` | expiry score ZSET, 기본 TTL 60초. Lua에서 stale score 제거. |
| 전이 중복 방지 | `<scope>:user:{id}:state` | ONLINE/OFFLINE, 기본 110초 TTL. 다중 app Lua claim. |
| Presence fan-out | `<scope>:presence:events` | Pub/Sub. 지속 저장·replay 없음. |
| 읽음/방 이벤트 fan-out | `<scope>:realtime:events` | Pub/Sub. 실제 상태는 DB 재조회로 복구. |
| client 내부 조회/채널 | `<scope>:tiebreaker`, `<scope>:configuration` | StackExchange.Redis 내부 이름도 namespace 안에 한정. |

기본 grace는 30초다. worker는 100ms 간격으로 due 여부를 검사하므로 deadline 이전에 OFFLINE을 만들지 않으며 polling/실행 부하만큼 늦을 수 있다. 유실된 disconnect의 lease는 native TTL로 사라진다. 원본에 없는 전역 만료 event sweep을 구현했다고 보고하지 않는다. 오래된 연결 ID를 새 process로 옮기지 않고 실제 재접속/재등록으로 Presence를 재구성한다.

Redis 저장값은 현재 이행한 연결 상태와 Pub/Sub에 한정하므로 `save ""`, `appendonly no`, tmpfs 구성이다. 재시작 시 사용자 메시지/읽음/AI 작업의 DB 데이터는 이 Redis에서 복구하지 않는다. 이후 durable queue를 추가하면 이 설정을 그대로 적용할 수 없으며 persistence/backup/drain/재처리 계약을 먼저 정해야 한다.

Redis `maxmemory=256mb`, container 상한 `384m`, `noeviction`은 검증 출발값이다. silent eviction으로 lease를 없애는 대신 부족 시 write 오류가 나타날 수 있으므로 OOM·메모리·connection·Pub/Sub 손실·재연결을 관측해야 한다. PING 성공은 쓰기 여유나 전체 애플리케이션 준비 완료를 뜻하지 않는다. 실제 부하 sizing/soak/host 장애/HA/backup 복구는 미검증이다. [Redis eviction 정책](https://redis.io/docs/latest/develop/reference/eviction/)

## 검증 및 이후 실행

아래는 **이전 V2 단계의 기록**이다. 당시 adapter 인계 전 결과를 현재 최신 실행 결과로 재사용하지 않는다. 환경·인증 작업의 최신 결과는 [현재 검증 장부](../../../CatPjt-TranslaCat-docs/repositories/chat/docs/operations/configuration-and-secrets.md)에 별도로 남긴다.

1. `docker compose --env-file deploy/chat/.env.example -f deploy/chat/compose.yaml config --quiet`: 종료 코드 0. 구조·필수 설정 참조 확인이며 서비스 시작은 하지 않았다.
2. `scripts/Test-ChatRedisDeployment.ps1`: 2026-09-27 00:57 KST 실행, 종료 코드 0. 배포 Redis 설정/스크립트를 그대로 mount한 별도 합성 container에서 인증 healthcheck와 익명 `NOAUTH`를 확인했다. 실제 StackExchange.Redis 3.3.1 client로 PING, Lua, native TTL, owner 값, 두 client Pub/Sub, 다른 namespace 접근 거부, FLUSHDB/CONFIG SET 거부, 잘못된 인증 거부 등 12개 check가 통과했다.
3. 검증 manifest: `TestResults/V2/RedisAcl/2ba3828da80b46a8911f202c940bf35f/manifest.json`, 상태 `VERIFIED_CLEANED`. 정확한 container ID와 run/owner label을 확인한 뒤 이 fixture만 정리했다. 실제 비밀번호를 문서에 기록하지 않았다.
4. `docker build --file deploy/chat/Dockerfile --tag translacat-chat:validation-20260927 .`: 종료 코드 0. 공식 Linux SDK image 안에서 4개 프로젝트 restore와 Release publish가 실제 통과했고 로컬 image를 생성했다. image ID는 `sha256:2612989fb5d0dbeb46f9dcef5cded7610974d1db5d3e41169d328bef0dc460f3`이다. API container/Compose app은 시작하지 않았다.

위4개는 초기 실행 기록이다. 최종 소스로 `docker build --file deploy/chat/Dockerfile --tag translacat-chat:validation-v2-reviewed-20260927 .`를 다시 실행해 종료0을 확인했다. image digest는 `sha256:aaaf5b571359a46bbd3528c8d69a1b07ad2d4973ae6968768184d9717a64dc4d`이다.

이어 `scripts/Test-ChatDeploymentStartup.ps1 -Image translacat-chat:validation-v2-reviewed-20260927`로 실제 production Compose와 entrypoint를 별도 합성 project에서 기동했다(종료0). API+전용 ACL Redis에서 Health200, readiness503, Redis/realtime READY, 누락된 Presence publicId/image upload capability NOT_CONFIGURED, 무인증 업무401을 확인했다. DB는 컨테이너 자체의 닫힌127.0.0.1:1로 한정했으므로 이 smoke는 DB 통합 검증이 아니다. 정확한 ID/run/owner/project label 확인 후 이번 컨테이너2/네트워크2를 정리했고 잔여0을 재확인했다. 증거는 `TestResults/V2/DeploymentSmoke/2238eaa415a24e81a14dfe17e05c13da/{manifest.json,http-observations.json}`이다.

기존 읽음/Redis/worker/HTTP 통합 테스트, ACL probe, Linux startup smoke는 서로 다른 검증이다. 실제 DB integration은 별도 manifest의 MySQL8.4.11에서 수행했다. 외부 계정 연결/BE proxy/브라우저 FE E2E/운영 전환은 미검증이다. 주 작업 장부의740개 전체 실행 결과도 함께 확인한다.

다음 명령은 운영 게이트 해제 후 사용할 절차 예시이며 이번 작업에서는 실행하지 않는다. 먼저 실제 소유 서비스 endpoint·DB migration·source time zone·ingress 구성이 검증되어야 한다. `--env-file`에는 준비한 비밀 **경로** 설정 파일을 지정한다. 환경에 맞는 `.env.local` 또는 `.env.prod`를 선택하고 내부 인증 overlay도 함께 적용한다.

```powershell
docker compose --env-file deploy/chat/.env.prod -f deploy/chat/compose.yaml -f deploy/chat/compose.internal-auth.yaml config --quiet
docker compose --env-file deploy/chat/.env.prod -f deploy/chat/compose.yaml -f deploy/chat/compose.internal-auth.yaml build chat-api
docker compose --env-file deploy/chat/.env.prod -f deploy/chat/compose.yaml -f deploy/chat/compose.internal-auth.yaml up -d chat-redis chat-api
docker compose --env-file deploy/chat/.env.prod -f deploy/chat/compose.yaml -f deploy/chat/compose.internal-auth.yaml ps
Invoke-RestMethod http://127.0.0.1:5085/api/health
Invoke-RestMethod http://127.0.0.1:5085/api/ready
docker compose --env-file deploy/chat/.env.prod -f deploy/chat/compose.yaml -f deploy/chat/compose.internal-auth.yaml stop chat-api chat-redis
```

기존 `/api/health`의 UP은 liveness다. `/api/ready`의 DB·Redis·realtime·identity·authentication 결과를 따로 확인한다. 공통 계정 adapter 미구성을 200으로 바꾸거나 probe 결과를 fake로 채우지 않는다. 키/데이터 삭제, `down -v`, 전체 Redis flush, 기존 BE/LL 서비스 중단은 위 절차에 포함하지 않는다.

전환 전에는 단일 writer/worker, 현재 사용자 데이터의 보존·ID/cursor/행 수/참조 검증, 재실행 가능한 이관, reconnect/grace 계획, 공통 계정 API 가용성, BE proxy의 upgrade/timeout/disconnect, TLS·secret rotation·용량·복구·rollback을 별도 검증한다. 이번 구성 준비는 기존 데이터 복사나 일반 `translacat_chat`/BE/LL 데이터 초기화를 실행하지 않는다.
