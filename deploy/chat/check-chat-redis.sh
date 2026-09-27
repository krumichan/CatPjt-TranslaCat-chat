#!/bin/sh
set -eu

# command line 인자로 비밀번호를 넘기지 않고 인증된 PING 결과만 판정한다.
REDISCLI_AUTH=$(cat /run/secrets/chat_redis_password)
export REDISCLI_AUTH
result=$(redis-cli --user chat --no-auth-warning --raw PING)
test "$result" = PONG
