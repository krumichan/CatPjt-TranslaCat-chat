#!/bin/sh
set -eu
# 값은 shell source/eval하지 않는다. export는 변수 대입만 수행한다.
if [ "${1:-}" = "--migrate-database" ]; then
    exec dotnet /app/TranslaCat.Chat.Api.dll "$@"
fi
while IFS= read -r line || [ -n "$line" ]; do
    [ -n "$line" ] || continue
    name=${line%%=*}
    case "$name" in *[!A-Za-z0-9_]*|'') echo 'INVALID_ENV_NAME' >&2; exit 2;; esac
    export "$line"
done < /run/config/runtime.env
exec dotnet /app/TranslaCat.Chat.Api.dll "$@"
