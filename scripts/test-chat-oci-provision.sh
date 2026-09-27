#!/usr/bin/env bash
set -Eeuo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
evidence="$root/TestResults/OciProvisionScript/$(date -u +%Y%m%dT%H%M%S)-$$"
sha='1111111111111111111111111111111111111111'
mkdir -p "$evidence"

make_fixture() {
    local case_name="$1" fixture="$evidence/$1"
    mkdir -p "$fixture/deploy/chat" "$fixture/scripts" "$fixture/bin"
    cp "$root/scripts/provision-chat-oci.sh" "$fixture/scripts/"
    cp "$root/deploy/chat"/{.env.example,compose.yaml,compose.internal-auth.yaml,compose.production.yaml,redis.conf,start-chat-redis.sh,check-chat-redis.sh} "$fixture/deploy/chat/"
    sed -e 's/^CHAT_ALLOWED_HOSTS=.*/CHAT_ALLOWED_HOSTS=chat.invalid/' \
        -e 's/^CHAT_BROWSER_ORIGIN=.*/CHAT_BROWSER_ORIGIN=https:\/\/chat.invalid/' \
        -e 's/^CHAT_SOURCE_TIME_ZONE=.*/CHAT_SOURCE_TIME_ZONE=Asia\/Tokyo/' \
        -e 's/^CHAT_IDENTITY_BASE_URL=.*/CHAT_IDENTITY_BASE_URL=https:\/\/be.invalid\//' \
        -e 's/^CHAT_CORE_BASE_URL=.*/CHAT_CORE_BASE_URL=https:\/\/be.invalid\//' \
        -e 's/^CHAT_AI_BASE_URL=.*/CHAT_AI_BASE_URL=https:\/\/ai.invalid\//' \
        "$fixture/deploy/chat/.env.example" > "$fixture/source.env"
    cat > "$fixture/bin/git" <<'FAKE'
#!/usr/bin/env bash
printf '%s\n' "$FAKE_SHA"
FAKE
    cat > "$fixture/bin/id" <<'FAKE'
#!/usr/bin/env bash
if [[ "$1" == -G ]]; then echo '20000 20001 20002'; else echo '20000'; fi
FAKE
    cat > "$fixture/bin/chgrp" <<'FAKE'
#!/usr/bin/env bash
# Windows Git Bash는 실제 POSIX 소유권을 설정할 수 없다. Linux 검증은 별도 실제 Docker 테스트에서 수행한다.
exit 0
FAKE
    cat > "$fixture/bin/mkdir" <<'FAKE'
#!/usr/bin/env bash
args=()
while (($#)); do
    if [[ "$1" == -m ]]; then shift 2; continue; fi
    args+=("$1")
    shift
done
/usr/bin/mkdir "${args[@]}"
FAKE
    cat > "$fixture/bin/chmod" <<'FAKE'
#!/usr/bin/env bash
exit 0
FAKE
    chmod +x "$fixture"/bin/*
    printf '%s' "$fixture"
}

set_healthy_inputs() {
    local fixture="$1"
    export FAKE_SHA="$sha" PATH="$fixture/bin:$PATH"
    export CHAT_PRODUCTION_ENV_B64="$(base64 -w 0 < "$fixture/source.env")"
    export CHAT_REDIS_PASSWORD_B64="$(printf synthetic-redis | base64 -w 0)"
    export CHAT_DATABASE_CONNECTION_B64="$(printf synthetic-db | base64 -w 0)"
    export CHAT_JWT_SIGNING_KEY_B64="$(printf synthetic-jwt | base64 -w 0)"
    export CHAT_SERVICE_INGRESS_KEY_B64="$(printf synthetic-ingress | base64 -w 0)"
    export CHAT_IDENTITY_KEY_B64="$(printf synthetic-identity | base64 -w 0)"
    export CHAT_AI_API_KEY_B64="$(printf synthetic-ai | base64 -w 0)"
}

expect_rejection() {
    local fixture="$1" reason="$2" output="$3" result=0
    (cd "$fixture" && bash scripts/provision-chat-oci.sh "$sha") > "$output" 2>&1 || result=$?
    [[ "$result" == 2 ]] || {
        echo "FAIL: expected exit 2, received $result" >&2; exit 1;
    }
    grep -Fxq "$reason" "$output" || {
        echo "FAIL: expected specific rejection reason: $reason" >&2; exit 1;
    }
    if [[ "$(uname -s)" != MINGW* ]]; then
        [[ "$(find "$fixture/deploy/chat/.releases" -maxdepth 1 -name '.incoming.*' | wc -l)" == 0 ]]
    fi
}

# 성공과 같은 SHA 재실행은 완전한 정상 입력으로 시작한다.
fixture="$(make_fixture success)"
set_healthy_inputs "$fixture"
(cd "$fixture" && bash scripts/provision-chat-oci.sh "$sha" && bash scripts/provision-chat-oci.sh "$sha") > "$fixture/result.log" 2>&1
release="$fixture/deploy/chat/.releases/$sha"
[[ -f "$release/.complete" && -s "$release/.env" ]]
cmp -s "$release/secrets/chat_redis_password" "$release/secrets/chat_redis_password_api"

# 각 거절 시나리오는 새 정상 fixture와 일곱 필수 입력을 다시 채운 뒤 딱 하나만 바꾼다.
fixture="$(make_fixture mismatch)"
set_healthy_inputs "$fixture"
(cd "$fixture" && bash scripts/provision-chat-oci.sh "$sha") > "$fixture/setup.log" 2>&1
original_digest="$(sha256sum "$fixture/deploy/chat/.releases/$sha/secrets/chat_ai_api_key")"
export CHAT_AI_API_KEY_B64="$(printf changed-synthetic-ai | base64 -w 0)"
expect_rejection "$fixture" 'The same SHA already has different protected settings.' "$fixture/result.log"
[[ "$(sha256sum "$fixture/deploy/chat/.releases/$sha/secrets/chat_ai_api_key")" == "$original_digest" ]]

fixture="$(make_fixture missing)"
set_healthy_inputs "$fixture"
unset CHAT_AI_API_KEY_B64
expect_rejection "$fixture" 'GitHub Secret is missing: CHAT_AI_API_KEY_B64' "$fixture/result.log"

fixture="$(make_fixture unknown)"
set_healthy_inputs "$fixture"
printf 'UNKNOWN_SETTING=synthetic\n' >> "$fixture/source.env"
export CHAT_PRODUCTION_ENV_B64="$(base64 -w 0 < "$fixture/source.env")"
expect_rejection "$fixture" 'Production env has an unknown name: UNKNOWN_SETTING' "$fixture/result.log"

fixture="$(make_fixture shell_expression)"
set_healthy_inputs "$fixture"
sed -i 's/^CHAT_ALLOWED_HOSTS=.*/CHAT_ALLOWED_HOSTS=$(touch forbidden-marker)/' "$fixture/source.env"
export CHAT_PRODUCTION_ENV_B64="$(base64 -w 0 < "$fixture/source.env")"
expect_rejection "$fixture" 'Production env must contain literal values: CHAT_ALLOWED_HOSTS' "$fixture/result.log"
[[ ! -e "$fixture/forbidden-marker" ]]

fixture="$(make_fixture duplicate)"
set_healthy_inputs "$fixture"
printf 'CHAT_ALLOWED_HOSTS=second.invalid\n' >> "$fixture/source.env"
export CHAT_PRODUCTION_ENV_B64="$(base64 -w 0 < "$fixture/source.env")"
expect_rejection "$fixture" 'Production env has a duplicate name: CHAT_ALLOWED_HOSTS' "$fixture/result.log"

printf 'PASS provision repeat + five isolated rejections; Windows ownership is stubbed: %s\n' "$evidence"
