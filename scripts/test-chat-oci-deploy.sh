#!/usr/bin/env bash
set -Eeuo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
evidence="$root/TestResults/OciDeployScript/$(date -u +%Y%m%dT%H%M%S)-$$"
new_sha='1111111111111111111111111111111111111111'
old_sha='2222222222222222222222222222222222222222'
mkdir -p "$evidence"

make_release() {
    local fixture="$1" release_sha="$2" host="$3" directory name
    directory="$fixture/deploy/chat/.releases/$release_sha"
    mkdir -p "$directory/secrets"
    cp "$root/deploy/chat"/{compose.yaml,compose.internal-auth.yaml,compose.production.yaml,redis.conf,start-chat-redis.sh,check-chat-redis.sh} "$directory/"
    sed -e "s/^CHAT_ALLOWED_HOSTS=.*/CHAT_ALLOWED_HOSTS=$host/" \
        -e 's/^CHAT_BROWSER_ORIGIN=.*/CHAT_BROWSER_ORIGIN=https:\/\/chat.invalid/' \
        -e 's/^CHAT_SOURCE_TIME_ZONE=.*/CHAT_SOURCE_TIME_ZONE=Asia\/Tokyo/' \
        -e 's/^CHAT_IDENTITY_BASE_URL=.*/CHAT_IDENTITY_BASE_URL=https:\/\/be.invalid\//' \
        -e 's/^CHAT_CORE_BASE_URL=.*/CHAT_CORE_BASE_URL=https:\/\/be.invalid\//' \
        -e 's/^CHAT_AI_BASE_URL=.*/CHAT_AI_BASE_URL=https:\/\/ai.invalid\//' \
        "$root/deploy/chat/.env.example" > "$directory/.env"
    for name in CHAT_REDIS_PASSWORD CHAT_DATABASE_CONNECTION CHAT_JWT_SIGNING_KEY \
        CHAT_SERVICE_INGRESS_KEY CHAT_IDENTITY_KEY CHAT_AI_API_KEY; do
        local file
        case "$name" in
            CHAT_REDIS_PASSWORD) file=chat_redis_password ;;
            CHAT_DATABASE_CONNECTION) file=chat_database_connection ;;
            CHAT_JWT_SIGNING_KEY) file=chat_jwt_signing_key ;;
            CHAT_SERVICE_INGRESS_KEY) file=chat_ingress_signing_key ;;
            CHAT_IDENTITY_KEY) file=chat_identity_signing_key ;;
            CHAT_AI_API_KEY) file=chat_ai_api_key ;;
        esac
        printf 'synthetic-only\n' > "$directory/secrets/$file"
        sed -i "s|^${name}_FILE=.*|${name}_FILE=$directory/secrets/$file|" "$directory/.env"
    done
    cp "$directory/secrets/chat_redis_password" "$directory/secrets/chat_redis_password_api"
    printf '%s\n' "$release_sha" > "$directory/.complete"
}

make_fakes() {
    local fixture="$1"
    mkdir -p "$fixture/bin"
    cat > "$fixture/bin/git" <<'FAKE'
#!/usr/bin/env bash
printf '%s\n' "$FAKE_SHA"
FAKE
    cat > "$fixture/bin/docker" <<'FAKE'
#!/usr/bin/env bash
printf 'docker %s image=%s\n' "$*" "${CHAT_IMAGE:-unset}" >> "$FAKE_CALLS"
if [[ "$1" == pull && "$FAKE_CASE" == redis_missing ]]; then exit 1; fi
if [[ "$1" == inspect ]]; then
    if [[ "$*" == *'.Config.Image'* ]]; then echo "translacat-chat:$FAKE_OLD_SHA"
    else echo "$FAKE_SHA"; fi
fi
if [[ " $* " == *' config --quiet '* && "$FAKE_CASE" == config ]]; then exit 1; fi
if [[ " $* " == *' ps --all --quiet '* && "$FAKE_PREVIOUS" == 1 ]]; then echo 'old-chat-container'; fi
if [[ " $* " == *' ps -q '* ]]; then echo 'running-chat-container'; fi
exit 0
FAKE
    cat > "$fixture/bin/curl" <<'FAKE'
#!/usr/bin/env bash
printf 'curl %s\n' "$*" >> "$FAKE_CALLS"
if [[ "$*" == *'Host: chat.old.invalid'* ]]; then
    printf '%s' "$FAKE_ROLLBACK_STATUS"
else
    if [[ "$FAKE_CASE" == ready_204 && "$*" == *'/api/ready'* ]]; then
        printf '204'
    elif [[ "$FAKE_CASE" == ready_307 && "$*" == *'/api/ready'* ]]; then
        printf '307'
    else
        printf '%s' "$FAKE_HTTP_STATUS"
        [[ "$FAKE_HTTP_STATUS" != timeout ]] || exit 28
    fi
fi
FAKE
    cat > "$fixture/bin/sleep" <<'FAKE'
#!/usr/bin/env bash
exit 0
FAKE
    cat > "$fixture/bin/id" <<'FAKE'
#!/usr/bin/env bash
echo '20000'
FAKE
    cat > "$fixture/bin/stat" <<'FAKE'
#!/usr/bin/env bash
case "$2" in
    '%a') echo '711' ;;
    '%u:%g:%a')
        if [[ "$3" == */secrets/chat_redis_password ]]; then echo '20000:20002:640'
        else echo '20000:20001:640'; fi ;;
    '%u:%a')
        if [[ "$3" == */.env ]]; then echo '20000:600'
        else echo '20000:644'; fi ;;
    *) exit 1 ;;
esac
FAKE
    chmod +x "$fixture"/bin/*
}

run_case() {
    local scenario="$1" previous="$2" status_code="$3" rollback_code="$4" expected_exit="$5"
    local fixture="$evidence/$scenario" result=0
    mkdir -p "$fixture/deploy/chat/.releases"
    cp "$root/scripts/deploy-chat-oci.sh" "$fixture/deploy.sh"
    make_release "$fixture" "$new_sha" chat.new.invalid
    if [[ "$previous" == 1 ]]; then
        make_release "$fixture" "$old_sha" chat.old.invalid
        printf '%s\n' "$old_sha" > "$fixture/deploy/chat/.releases/current"
    fi
    make_fakes "$fixture"

    # 실행 — Docker, curl, Git을 PATH 안의 실행별 대역으로 제한한다.
    (
        cd "$fixture"
        export FAKE_SHA="$new_sha" FAKE_OLD_SHA="$old_sha" FAKE_CASE="$scenario"
        export FAKE_HTTP_STATUS="$status_code" FAKE_ROLLBACK_STATUS="$rollback_code"
        export FAKE_PREVIOUS="$previous" FAKE_CALLS="$fixture/calls.log"
        export PATH="$fixture/bin:$PATH"
        bash ./deploy.sh "$new_sha"
    ) > "$fixture/output.log" 2>&1 || result=$?

    # 검증 — HTTP 200 양쪽 성공과 이전 릴리스 설정 사용을 함께 확인한다.
    [[ "$result" == "$expected_exit" ]] || {
        echo "FAIL $scenario: exit=$result expected=$expected_exit" >&2
        tail -n 12 "$fixture/output.log" >&2
        exit 1
    }
    if [[ "$scenario" == success || "$scenario" == first_success ]]; then
        [[ "$(cat "$fixture/deploy/chat/.releases/current")" == "$new_sha" ]]
        grep -q 'Host: chat.new.invalid' "$fixture/calls.log"
        grep -q -- '--max-redirs 0' "$fixture/calls.log"
        ! grep -q -- '--location' "$fixture/calls.log"
    elif [[ "$scenario" == redis_missing || "$scenario" == config ]]; then
        ! grep -q ' up ' "$fixture/calls.log"
    elif [[ "$previous" == 1 ]]; then
        grep -q 'Host: chat.old.invalid' "$fixture/calls.log"
        grep -q "image=translacat-chat:$old_sha" "$fixture/calls.log"
        grep -q -- "--project-directory $fixture/deploy/chat/.releases/$old_sha" "$fixture/calls.log"
        [[ "$(cat "$fixture/deploy/chat/.releases/current")" == "$old_sha" ]]
    else
        grep -q ' stop chat-api ' "$fixture/calls.log"
        [[ ! -e "$fixture/deploy/chat/.releases/current" ]]
    fi
    printf 'PASS %s exit=%s\n' "$scenario" "$result"
}

run_case success 1 200 200 0
run_case first_success 0 200 200 0
for code in 204 301 302 307 400 401 503 timeout; do
    run_case "http_$code" 1 "$code" 200 1
done
run_case ready_204 1 200 200 1
run_case ready_307 1 200 200 1
run_case redis_missing 1 200 200 1
run_case config 1 200 200 1
run_case rollback_failure 1 503 503 70
run_case first_failure 0 503 200 1
printf 'Sixteen synthetic deploy/rollback cases passed. Evidence: %s\n' "$evidence"
