#!/bin/sh
set -eu

# 환경은 host 생성 전에 결정한다. Local/Prod 별칭이나 서로 다른 환경 값은 허용하지 않는다.
environment=${DOTNET_ENVIRONMENT:?DOTNET_ENVIRONMENT is required}
case "$environment" in
    Development|Production) ;;
    *) echo 'CHAT environment must be Development or Production.' >&2; exit 1 ;;
esac
if [ -n "${ASPNETCORE_ENVIRONMENT:-}" ] && [ "$ASPNETCORE_ENVIRONMENT" != "$environment" ]; then
    echo 'CHAT environment variables conflict.' >&2
    exit 1
fi
ASPNETCORE_ENVIRONMENT=$environment
export ASPNETCORE_ENVIRONMENT

# 원문을 shell 환경에 복사하지 않는다. 파일 로딩·크기·중복 secret 검사는 managed provider가 담당한다.
Chat__Database__ConnectionString_FILE=${Chat__Database__ConnectionString_FILE:-/run/secrets/chat_database_connection}
Chat__Authentication__Base64SigningKey_FILE=${Chat__Authentication__Base64SigningKey_FILE:-/run/secrets/chat_jwt_signing_key}
Chat__Redis__Password_FILE=${Chat__Redis__Password_FILE:-/run/secrets/chat_redis_password_api}
export Chat__Database__ConnectionString_FILE Chat__Authentication__Base64SigningKey_FILE Chat__Redis__Password_FILE

exec dotnet /app/TranslaCat.Chat.Api.dll "$@"
