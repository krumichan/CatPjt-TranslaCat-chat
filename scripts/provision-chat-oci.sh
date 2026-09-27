#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

# GitHub Secret은 Base64 전송 자료다. 디코딩한 내용을 source/eval하거나 Docker build에 넣지 않는다.
sha="${1:?Pass the exact release commit SHA}"
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid release SHA.' >&2; exit 2; }
[[ "$(git rev-parse HEAD)" == "$sha" ]] || { echo 'Release checkout does not match SHA.' >&2; exit 2; }

root="$(pwd -P)"
releases="$root/deploy/chat/.releases"
api_secret_gid=20001
redis_secret_gid=20002

# 배포 계정은 두 전용 그룹에 속해야 파일 소유자로서 해당 그룹을 설정할 수 있다.
account_groups=" $(id -G) "
[[ "$account_groups" == *" $api_secret_gid "* && "$account_groups" == *" $redis_secret_gid "* ]] || {
    echo 'Deploy account needs CHAT API and Redis secret reader groups (20001, 20002).' >&2
    exit 2
}

mkdir -p -m 711 "$releases"
chmod 711 "$releases"
stage="$(mktemp -d "$releases/.incoming.$sha.XXXXXXXX")"
final="$releases/$sha"
cleanup_stage() {
    local exit_code="$?"
    trap - EXIT
    set +e
    if [[ -n "${stage:-}" && -d "$stage" ]]; then
        local resolved
        resolved="$(cd "$stage" && pwd -P)"
        if [[ "$resolved" == "$releases"/.incoming."$sha".* ]]; then
            for attempt in 1 2 3; do
                rm -r -- "$resolved" 2>/dev/null && break
                sleep 0.1
            done
            if [[ -d "$resolved" ]]; then
                echo 'Protected staging cleanup needs manual inspection.' >&2
            fi
        fi
    fi
    exit "$exit_code"
}
trap cleanup_stage EXIT
mkdir -m 700 "$stage/secrets"

decode_secret() {
    local variable="$1" target="$2" limit="$3" encoded
    encoded="${!variable:-}"
    [[ -n "$encoded" ]] || {
        echo "GitHub Secret is missing: $variable" >&2; exit 2;
    }
    [[ "${#encoded}" -le "$limit" && "$encoded" =~ ^[A-Za-z0-9+/]+={0,2}$ ]] || {
        echo "GitHub Secret encoding is invalid: $variable" >&2; exit 2;
    }
    printf '%s' "$encoded" | base64 --decode > "$target" || {
        echo "GitHub Secret decode failed: $variable" >&2; exit 2;
    }
    [[ -s "$target" ]] || { echo "GitHub Secret is empty: $variable" >&2; exit 2; }
    chmod 600 "$target"
}

decode_secret CHAT_PRODUCTION_ENV_B64 "$stage/source.env" 65536
decode_secret CHAT_REDIS_PASSWORD_B64 "$stage/secrets/chat_redis_password" 8192
decode_secret CHAT_DATABASE_CONNECTION_B64 "$stage/secrets/chat_database_connection" 16384
decode_secret CHAT_JWT_SIGNING_KEY_B64 "$stage/secrets/chat_jwt_signing_key" 8192
decode_secret CHAT_SERVICE_INGRESS_KEY_B64 "$stage/secrets/chat_ingress_signing_key" 8192
decode_secret CHAT_IDENTITY_KEY_B64 "$stage/secrets/chat_identity_signing_key" 8192
decode_secret CHAT_AI_API_KEY_B64 "$stage/secrets/chat_ai_api_key" 8192

# 같은 원본의 별도 사본으로 API와 Redis의 읽기 그룹을 분리한다.
cp -- "$stage/secrets/chat_redis_password" "$stage/secrets/chat_redis_password_api"
chgrp "$redis_secret_gid" "$stage/secrets/chat_redis_password"
chmod 640 "$stage/secrets/chat_redis_password"
for file in chat_redis_password_api chat_database_connection chat_jwt_signing_key \
    chat_ingress_signing_key chat_identity_signing_key chat_ai_api_key; do
    chgrp "$api_secret_gid" "$stage/secrets/$file"
    chmod 640 "$stage/secrets/$file"
done

# 환경 파일은 공개 템플릿의 정확한 32개 이름만 허용한다. 파일 경로 여섯 개는 서버가 결정한다.
[[ "$(wc -c < "$stage/source.env")" -le 65536 ]] || { echo 'Production env is too large.' >&2; exit 2; }
LC_ALL=C tr -d '\000' < "$stage/source.env" | cmp -s - "$stage/source.env" || {
    echo 'Production env contains a NUL byte.' >&2; exit 2;
}
declare -A expected=() seen=()
while IFS= read -r line || [[ -n "$line" ]]; do
    [[ "$line" =~ ^([A-Z][A-Z0-9_]*)= ]] || continue
    expected["${BASH_REMATCH[1]}"]=1
done < "$root/deploy/chat/.env.example"

: > "$stage/.env"
while IFS= read -r line || [[ -n "$line" ]]; do
    [[ -z "$line" || "$line" == \#* ]] && continue
    [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ && "$line" != *$'\r'* ]] || {
        echo 'Production env has an invalid line.' >&2; exit 2;
    }
    name="${BASH_REMATCH[1]}"
    value="${BASH_REMATCH[2]}"
    [[ -n "${expected[$name]:-}" ]] || {
        echo "Production env has an unknown name: $name" >&2; exit 2;
    }
    [[ -z "${seen[$name]:-}" ]] || {
        echo "Production env has a duplicate name: $name" >&2; exit 2;
    }
    [[ "$value" != *'$'* && "$value" != *'`'* && "$value" != *'"'* && "$value" != *"'"* ]] || {
        echo "Production env must contain literal values: $name" >&2; exit 2;
    }
    seen["$name"]=1

    case "$name" in
        CHAT_REDIS_PASSWORD_FILE) target=chat_redis_password ;;
        CHAT_DATABASE_CONNECTION_FILE) target=chat_database_connection ;;
        CHAT_JWT_SIGNING_KEY_FILE) target=chat_jwt_signing_key ;;
        CHAT_SERVICE_INGRESS_KEY_FILE) target=chat_ingress_signing_key ;;
        CHAT_IDENTITY_KEY_FILE) target=chat_identity_signing_key ;;
        CHAT_AI_API_KEY_FILE) target=chat_ai_api_key ;;
        *) target='' ;;
    esac
    if [[ -n "$target" ]]; then
        [[ -z "$value" ]] || { echo "GitHub env must leave file path blank: $name" >&2; exit 2; }
        value="$final/secrets/$target"
    fi
    printf '%s=%s\n' "$name" "$value" >> "$stage/.env"
done < "$stage/source.env"

[[ "${#seen[@]}" -eq "${#expected[@]}" ]] || {
    echo 'Production env is missing a required name.' >&2; exit 2;
}
chmod 600 "$stage/.env"
rm -- "$stage/source.env"

# Compose와 Redis 설정도 릴리스별로 보존해 이전 조합을 그대로 복구한다.
for file in compose.yaml compose.internal-auth.yaml compose.production.yaml \
    redis.conf start-chat-redis.sh check-chat-redis.sh; do
    cp -- "$root/deploy/chat/$file" "$stage/$file"
    chmod 644 "$stage/$file"
done
printf '%s\n' "$sha" > "$stage/.complete"
chmod 600 "$stage/.complete"

# 내용 확정 전에는 staging을 닫아 두고, 승격 직전에 경로 탐색만 허용한다.
chmod 711 "$stage/secrets" "$stage"

if [[ -e "$final" ]]; then
    same=true
    while IFS= read -r file; do
        cmp -s "$stage/$file" "$final/$file" || same=false
    done < <(cd "$stage" && find . -type f -print)
    [[ "$same" == true ]] || { echo 'The same SHA already has different protected settings.' >&2; exit 2; }

else
    mv -- "$stage" "$final"
fi

echo "CHAT protected release $sha staged. No service was changed."
