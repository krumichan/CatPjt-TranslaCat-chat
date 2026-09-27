#!/bin/sh
set -eu
umask 077

# secret 부재/잘못된 scope에서는 인증 없는 Redis를 시작하지 않는다.
namespace=${CHAT_REDIS_NAMESPACE:?CHAT_REDIS_NAMESPACE is required}
case "$namespace" in
    translacat:chat:?*) ;;
    *) echo 'CHAT Redis namespace is invalid.' >&2; exit 1 ;;
esac
case "$namespace" in
    *[!a-zA-Z0-9:_-]*) echo 'CHAT Redis namespace contains unsupported characters.' >&2; exit 1 ;;
esac

password=$(cat /run/secrets/chat_redis_password)
case "$password" in
    *[!0-9a-fA-F]*|'') echo 'CHAT Redis secret must contain 64 hexadecimal characters.' >&2; exit 1 ;;
esac
if [ "${#password}" -ne 64 ]; then
    echo 'CHAT Redis secret must contain 64 hexadecimal characters.' >&2
    exit 1
fi

# ACL에는 SHA-256만 기록하며 default 사용자를 차단한다. 공용 key/channel 권한은 주지 않는다.
password_hash=$(printf '%s' "$password" | sha256sum | cut -d ' ' -f 1)
commands='+ping +echo +hello +quit +select +info +client|setname +client|setinfo +get +exists +del +psetex +pexpire +pttl +zadd +zrem +zremrangebyscore +zcard +eval +evalsha +script|load +script|exists +publish +subscribe +unsubscribe'
printf 'user default reset off\nuser chat reset on #%s ~%s:* &%s:* -@all %s\n' \
    "$password_hash" "$namespace" "$namespace" "$commands" > /run/chat/users.acl
unset password password_hash commands

exec redis-server /chat-config/redis.conf
