#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

sha="${1:?Pass the exact release commit SHA}"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid release SHA.' >&2; exit 2; }
[[ "$(git rev-parse HEAD)" == "$sha" ]] || { echo 'Release checkout does not match SHA.' >&2; exit 2; }
root="$(pwd -P)"
releases="$root/deploy/chat/.releases"
release="$releases/$sha"
image="translacat-chat:$sha"
export CHAT_IMAGE="$image"
[[ -f "$release/.complete" && "$(cat "$release/.complete")" == "$sha" && -s "$release/.env" ]] || {
    echo 'The protected CHAT release is missing or incomplete.' >&2; exit 2;
}

setting() {
    sed -n "s/^${2}=//p" "$1/.env"
}

check_release_permissions() {
    local directory="$1" owner="$2" file
    [[ "$(stat -c '%a' "$releases")" == 711 ]]
    [[ "$(stat -c '%a' "$directory")" == 711 ]]
    [[ "$(stat -c '%a' "$directory/secrets")" == 711 ]]
    [[ "$(stat -c '%u:%a' "$directory/.env")" == "$owner:600" ]]

    # file 기반 Compose secret은 원본 파일의 Linux 그룹 권한을 그대로 검증한다.
    [[ "$(stat -c '%u:%g:%a' "$directory/secrets/chat_redis_password")" == "$owner:20002:640" ]]
    for file in chat_redis_password_api chat_database_connection chat_jwt_signing_key \
        chat_ingress_signing_key chat_identity_signing_key chat_ai_api_key; do
        [[ "$(stat -c '%u:%g:%a' "$directory/secrets/$file")" == "$owner:20001:640" ]]
    done
    cmp -s "$directory/secrets/chat_redis_password" "$directory/secrets/chat_redis_password_api"

    for file in compose.yaml compose.internal-auth.yaml compose.production.yaml \
        redis.conf start-chat-redis.sh check-chat-redis.sh; do
        [[ "$(stat -c '%u:%a' "$directory/$file")" == "$owner:644" ]]
    done
}

validate_release() {
    local directory="$1" name value host
    for name in DOTNET_ENVIRONMENT ASPNETCORE_ENVIRONMENT; do
        [[ "$(setting "$directory" "$name")" == Production ]] || {
            echo "Production setting is incomplete: $name" >&2; return 2;
        }
    done
    for name in CHAT_SERVICE_INGRESS_ENABLED CHAT_IDENTITY_ENABLED CHAT_CORE_ENABLED CHAT_AI_ENABLED CHAT_TRANSLATION_ENABLED; do
        [[ "$(setting "$directory" "$name")" == true ]] || {
            echo "Production capability is not enabled: $name" >&2; return 2;
        }
    done

    # 실제 서비스가 읽는 필수값과 보호 파일을 교체 전에 확인한다.
    for name in CHAT_API_PORT CHAT_ALLOWED_HOSTS CHAT_BROWSER_ORIGIN CHAT_REDIS_NAMESPACE CHAT_SOURCE_TIME_ZONE \
        CHAT_REDIS_PASSWORD_FILE CHAT_DATABASE_CONNECTION_FILE CHAT_JWT_SIGNING_KEY_FILE \
        CHAT_SERVICE_INGRESS_KEY_FILE CHAT_IDENTITY_BASE_URL CHAT_IDENTITY_KEY_FILE \
        CHAT_CORE_BASE_URL CHAT_AI_BASE_URL CHAT_AI_API_KEY_FILE; do
        value="$(setting "$directory" "$name")"
        [[ -n "$value" && "$value" != *$'\n'* ]] || {
            echo "Production setting is incomplete: $name" >&2; return 2;
        }
        if [[ "$name" == *_FILE ]]; then
            [[ "$value" == "$directory"/secrets/* && -s "$value" ]] || {
                echo "Protected Production file is missing: $name" >&2; return 2;
            }
        fi
    done
    for name in CHAT_BROWSER_ORIGIN CHAT_IDENTITY_BASE_URL CHAT_CORE_BASE_URL CHAT_AI_BASE_URL; do
        [[ "$(setting "$directory" "$name")" == https://* ]] || {
            echo "HTTPS origin is required: $name" >&2; return 2;
        }
    done
    value="$(setting "$directory" CHAT_API_PORT)"
    [[ "$value" =~ ^[1-9][0-9]{3,4}$ ]] && ((value >= 1024 && value <= 65535)) || {
        echo 'CHAT_API_PORT is missing or invalid.' >&2; return 2;
    }
    host="$(setting "$directory" CHAT_ALLOWED_HOSTS)"
    host="${host%%;*}"
    [[ "$host" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ && "$host" != localhost && "$host" != 127.0.0.1 && "$host" != '*' ]] || {
        echo 'CHAT_ALLOWED_HOSTS needs a concrete first Production host for the internal probe.' >&2; return 2;
    }
}

compose_for() {
    local directory="$1"
    compose=(docker compose --project-name translacat-chat --project-directory "$directory"
        --env-file "$directory/.env"
        -f "$directory/compose.yaml"
        -f "$directory/compose.internal-auth.yaml"
        -f "$directory/compose.production.yaml")
}

probe_once() {
    local port="$1" host="$2" path="$3" status
    # 전송 종료 코드와 HTTP 코드를 각각 확인한다. redirect는 따라가지 않는다.
    status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
        --max-time 5 --max-redirs 0 --header "Host: $host" \
        "http://127.0.0.1:$port$path")" || return 1
    [[ "$status" == 200 ]]
}

wait_ready() {
    local directory="$1" attempts="$2" port host attempt
    port="$(setting "$directory" CHAT_API_PORT)"
    host="$(setting "$directory" CHAT_ALLOWED_HOSTS)"
    host="${host%%;*}"
    for ((attempt = 1; attempt <= attempts; attempt++)); do
        if probe_once "$port" "$host" /api/health && probe_once "$port" "$host" /api/ready; then
            return 0
        fi
        sleep 5
    done
    return 1
}

deploy_uid="$(id -u)"
check_release_permissions "$release" "$deploy_uid" || {
    echo 'Protected CHAT release has incompatible Linux file ownership or permissions.' >&2; exit 2;
}
validate_release "$release"
compose_for "$release"
"${compose[@]}" config --quiet

# 이전 설정과 image, 새 Redis image를 모두 확인한 뒤 교체를 시작한다.
previous=''
if [[ -s "$releases/current" ]]; then
    previous_sha="$(cat "$releases/current")"
    [[ "$previous_sha" =~ ^[0-9a-f]{40}$ && -f "$releases/$previous_sha/.complete" && "$(cat "$releases/$previous_sha/.complete")" == "$previous_sha" ]] || {
        echo 'Previous protected CHAT release is invalid.' >&2; exit 2;
    }
    previous="$releases/$previous_sha"
    check_release_permissions "$previous" "$deploy_uid" || {
        echo 'Previous CHAT release cannot be used for safe rollback: file permissions differ.' >&2; exit 2;
    }
    validate_release "$previous"
    CHAT_IMAGE="translacat-chat:$previous_sha"
    export CHAT_IMAGE
    compose_for "$previous"
    "${compose[@]}" config --quiet
fi
CHAT_IMAGE="$image"
export CHAT_IMAGE
compose_for "$release"
previous_id="$("${compose[@]}" ps --all --quiet chat-api)"
if [[ -n "$previous_id" && -z "$previous" ]]; then
    echo 'An existing CHAT container has no compatible protected release snapshot.' >&2
    exit 2
fi
previous_image=''
if [[ -n "$previous_id" ]]; then
    previous_image="$(docker inspect --format '{{.Config.Image}}' "$previous_id")"
    docker image inspect "$previous_image" >/dev/null
fi
redis_image_for() {
    local pinned
    pinned="$(sed -nE 's/^    image: (redis:[^ ]+@sha256:[0-9a-f]{64})$/\1/p' "$1/compose.yaml")"
    [[ -n "$pinned" && "$pinned" != *$'\n'* ]] || {
        echo 'Pinned Redis image was not found in a protected release.' >&2; return 2;
    }
    printf '%s' "$pinned"
}
redis_image="$(redis_image_for "$release")"
docker pull "$redis_image" >/dev/null
docker image inspect "$redis_image" >/dev/null
if [[ -n "$previous" ]]; then
    previous_redis_image="$(redis_image_for "$previous")"
    if [[ "$previous_redis_image" != "$redis_image" ]]; then
        docker pull "$previous_redis_image" >/dev/null
    fi
    docker image inspect "$previous_redis_image" >/dev/null
fi

docker build --file deploy/chat/Dockerfile --tag "$image" \
    --label "org.opencontainers.image.revision=$sha" .
docker image inspect "$image" >/dev/null

# managed Options/DI 검사까지 끝낸 다음에만 기존 컨테이너에 손댄다.
"${compose[@]}" run --rm --no-deps chat-api --validate-configuration

replacement_attempted=false
rollback() {
    local code="$1" restored=true
    trap - ERR INT TERM
    set +e
    if [[ "$replacement_attempted" == true ]]; then
        if [[ -n "$previous_image" && -n "$previous" ]]; then
            echo "CHAT deployment failed; restoring release $previous_sha with its image and protected configuration." >&2
            export CHAT_IMAGE="$previous_image"
            compose_for "$previous"
            "${compose[@]}" up --detach --force-recreate --no-build --pull never chat-redis >&2 || restored=false
            if [[ "$restored" == true ]]; then
                "${compose[@]}" up --detach --force-recreate --no-build --pull never --no-deps chat-api >&2 || restored=false
            fi
            if [[ "$restored" == true ]]; then
                wait_ready "$previous" 12 || restored=false
            fi
            if [[ "$restored" == true ]]; then
                cp -- "$previous/.env" "$root/deploy/chat/.env.next" && \
                    mv -- "$root/deploy/chat/.env.next" "$root/deploy/chat/.env" || restored=false
            fi
            if [[ "$restored" != true ]]; then
                echo "CRITICAL: CHAT rollback failed or is unhealthy. Inspect release $previous_sha, image $previous_image and project translacat-chat; restore with that release's three Compose files and .env. No DB rollback was attempted." >&2
                exit 70
            fi
            echo "Previous CHAT release $previous_sha is healthy and ready again." >&2
        else
            compose_for "$release"
            if ! "${compose[@]}" stop chat-api >&2; then
                echo "CRITICAL: first CHAT release failed and its API could not be stopped. Inspect release $sha and project translacat-chat manually. No DB rollback was attempted." >&2
                exit 70
            fi
            echo 'First CHAT release failed; no previous healthy release exists. Inspect this release and its container. No DB rollback was attempted.' >&2
        fi
    fi
    exit "$code"
}
trap 'rollback $?' ERR
trap 'rollback 130' INT
trap 'rollback 143' TERM

# Redis는 고정 digest를 미리 확보한 뒤 private network에서 시작한다.
replacement_attempted=true
"${compose[@]}" up --detach --force-recreate --no-build --pull never chat-redis
"${compose[@]}" up --detach --force-recreate --no-build --pull never --no-deps chat-api
running_id="$("${compose[@]}" ps -q chat-api)"
[[ -n "$running_id" ]]
[[ "$(docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$running_id")" == "$sha" ]]
wait_ready "$release" 60 || { echo 'CHAT health/readiness did not reach HTTP 200 within five minutes.' >&2; false; }

# 성공한 조합만 현재 릴리스로 승격한다. DB schema/data는 변경하지 않는다.
cp -- "$release/.env" "$root/deploy/chat/.env.next"
chmod 600 "$root/deploy/chat/.env.next"
mv -- "$root/deploy/chat/.env.next" "$root/deploy/chat/.env"
printf '%s\n' "$sha" > "$releases/current.next"
mv -- "$releases/current.next" "$releases/current"
echo "CHAT release $sha is healthy and ready."
