'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { createRequire } = require('node:module');

// 기존 FE 의존성을 사용하는 명시적 로컬 검증 도구다. 운영 인증·저장소 대역은 넣지 않는다.
const feRequire = createRequire(path.resolve(__dirname, '../../../CatPjt-TranslaCat-fe/package.json'));
const { Client } = feRequire('@stomp/stompjs');
const WebSocket = feRequire('next/dist/compiled/ws');
class ProbeFailure extends Error {}

function safeJson(text, stage) {
    try { return JSON.parse(text); }
    catch { throw new ProbeFailure(stage + ': invalid JSON.'); }
}

let accounts;
const origin = process.env.CHAT_FRESH_START_BE_ORIGIN;
const second = process.env.CHAT_FRESH_START_SECOND_ORIGIN;
const resultFile = process.env.CHAT_FRESH_START_RESULT_FILE;
const checks = [];
const sessions = [];
const run = crypto.randomUUID();
let transportFailure;

try {
    const verification = fs.realpathSync(path.resolve(__dirname, '../../../.codex-workspace/verification/shared'));
    const accountFile = fs.realpathSync(process.env.CHAT_FRESH_START_ACCOUNTS_FILE);
    for (const candidate of [accountFile, fs.realpathSync(path.dirname(resultFile))]) {
        const relative = path.relative(verification, candidate);
        if (relative.startsWith('..') || path.isAbsolute(relative)) throw new Error();
    }
    if (!path.isAbsolute(resultFile) || fs.existsSync(resultFile)) throw new Error();
    accounts = safeJson(fs.readFileSync(accountFile, 'utf8'), 'Account fixture').accounts;
    if (!Array.isArray(accounts) || accounts.length !== 3) throw new Error();
    for (const value of [origin, second]) {
        const url = new URL(value);
        if (url.protocol !== 'http:' || !['127.0.0.1', 'localhost'].includes(url.hostname)
            || url.pathname !== '/' || url.username || url.password || url.search || url.hash) throw new Error();
    }
    if (new URL(origin).origin === new URL(second).origin) throw new Error();
} catch {
    console.error('Local verification requires valid protected fixtures, a new central result path and two distinct loopback origins.');
    process.exit(1);
}
const [a, b, c] = ['A', 'B', 'C'].map(key => accounts.find(account => account.key === key));

function check(name, condition) {
    checks.push({ name, passed: Boolean(condition) });
    if (!condition) throw new ProbeFailure('Check failed: ' + name);
}

async function api(account, method, route, body, expected = 200, extraHeaders = {}, target = origin) {
    const response = await fetch(target + route, {
        method,
        headers: {
            ...(account ? { Authorization: 'Bearer ' + account.accessToken } : {}),
            ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
            ...extraHeaders,
        },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: AbortSignal.timeout(15000),
        redirect: 'error',
    });
    check(method + ' ' + route + ' status ' + expected, response.status === expected);
    const text = await response.text();
    return text ? safeJson(text, 'HTTP response').body : undefined;
}

function serviceToken(account, scope) {
    const key = Buffer.from(fs.readFileSync(process.env.CHAT_FRESH_START_INGRESS_KEY_FILE, 'utf8').trim(), 'base64');
    const now = Math.floor(Date.now() / 1000);
    const header = Buffer.from(JSON.stringify({ alg: 'HS256', typ: 'JWT' })).toString('base64url');
    const payload = Buffer.from(JSON.stringify({
        iss: 'translacat-be', aud: 'translacat-chat', service: 'translacat-be',
        sub: String(account.userId), tokenUse: 'chat-ingress', environment: 'Development',
        scopes: [scope], iat: now, exp: now + 60,
    })).toString('base64url');
    const data = header + '.' + payload;
    return data + '.' + crypto.createHmac('sha256', key).update(data).digest('base64url');
}

async function until(name, predicate, milliseconds = 10000) {
    const end = Date.now() + milliseconds;
    while (Date.now() < end) {
        if (transportFailure) throw transportFailure;
        if (await predicate()) {
            check(name, true);
            return;
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    check(name, false);
}

async function connect(account, directSecond = false) {
    const target = directSecond ? second : origin;
    const headers = directSecond
        ? { 'X-Chat-Service-Authorization': 'Bearer ' + serviceToken(account, 'chat:realtime') }
        : {};
    const client = new Client({
        webSocketFactory: () => new WebSocket(target.replace('http:', 'ws:') + '/ws/chat', { headers }),
        connectHeaders: { Authorization: 'Bearer ' + account.accessToken },
        reconnectDelay: 0, heartbeatIncoming: 0, heartbeatOutgoing: 0, debug: () => {},
    });
    sessions.push(client);
    await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new ProbeFailure('Local STOMP connect timed out.')), 15000);
        client.onConnect = () => { clearTimeout(timer); resolve(); };
        client.onStompError = () => { clearTimeout(timer); reject(new ProbeFailure('Local STOMP authentication failed.')); };
        client.onWebSocketError = () => { clearTimeout(timer); reject(new ProbeFailure('Local WebSocket connection failed.')); };
        client.activate();
    });
    check(directSecond ? 'second CHAT authenticated CONNECT' : 'BE proxy authenticated CONNECT', true);
    return client;
}

async function subscribe(client, destination, events) {
    const receipt = crypto.randomUUID();
    await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new ProbeFailure('Local subscription receipt timed out.')), 10000);
        client.watchForReceipt(receipt, () => { clearTimeout(timer); resolve(); });
        client.subscribe(destination, message => {
            try { events.push(safeJson(message.body, 'STOMP event')); }
            catch { transportFailure = new ProbeFailure('Malformed STOMP event.'); }
        }, { receipt });
    });
}

async function disconnect(client) {
    // 종료가 멈춰도 검증 결과는 기록한다. 이번 시험이 만든 socket만 강제로 닫는다.
    let timer;
    const completed = await Promise.race([
        client.deactivate().then(() => true).catch(() => false),
        new Promise(resolve => { timer = setTimeout(() => resolve(false), 3000); }),
    ]);
    clearTimeout(timer);
    if (!completed) {
        await Promise.race([
            client.deactivate({ force: true }).catch(() => {}),
            new Promise(resolve => setTimeout(resolve, 1000)),
        ]);
    }
}

async function main() {
    // 준비: 실제 계정의 공개 경로와 서버 전용 인증 경계를 먼저 검사한다.
    await api(null, 'GET', '/api/v1/chat/rooms', undefined, 401);
    await api({ accessToken: 'invalid' }, 'GET', '/api/v1/chat/rooms', undefined, 401);
    await api(a, 'GET', '/api/v1/chat/rooms', undefined, 401, { 'X-Chat-Service-Authorization': 'Bearer invalid' });
    await api(a, 'GET', '/api/v1/chat/rooms', undefined, 401, { 'X-Chat-Service-Authorization': 'Bearer invalid' }, second);
    await api(a, 'GET', '/api/v1/chat/rooms', undefined, 200,
        { 'X-Chat-Service-Authorization': 'Bearer ' + serviceToken(a, 'chat:http') }, second);

    // 실행: DIRECT 재사용과 별도 GROUP 업무·읽음 정책을 실제 DB에서 확인한다.
    const direct = await api(a, 'POST', '/api/v1/chat/rooms', { roomType: 'DIRECT', memberUserIds: [b.userId] }, 201);
    const reused = await api(a, 'POST', '/api/v1/chat/rooms', { roomType: 'DIRECT', memberUserIds: [b.userId] }, 201);
    check('DIRECT is reused', reused.id === direct.id);
    const group = await api(a, 'POST', '/api/v1/chat/rooms', {
        roomType: 'GROUP', name: 'Local verification ' + run, memberUserIds: [b.userId],
    }, 201);
    const roomId = group.id;
    check('GROUP membership count', group.memberCount === 2);
    await api(c, 'GET', '/api/v1/chat/rooms/' + roomId, undefined, 400);
    const messages = [];
    for (let index = 0; index < 3; index++) {
        messages.push(await api(a, 'POST', '/api/v1/chat/rooms/' + roomId + '/messages', { content: 'Synthetic local ' + run + ' ' + index }));
    }
    const firstRead = await api(b, 'PATCH', '/api/v1/chat/rooms/' + roomId + '/read', { lastReadMessageId: messages[1].id });
    check('unread count comes from remaining message', firstRead.unreadCount === 1);
    const sameRead = await api(b, 'PATCH', '/api/v1/chat/rooms/' + roomId + '/read', { lastReadMessageId: messages[1].id });
    const olderRead = await api(b, 'PATCH', '/api/v1/chat/rooms/' + roomId + '/read', { lastReadMessageId: messages[0].id });
    check('same and older cursor preserve lastReadAt', firstRead.lastReadAt === sameRead.lastReadAt && firstRead.lastReadAt === olderRead.lastReadAt);
    check('older cursor cannot regress', olderRead.lastReadMessageId === messages[1].id && olderRead.unreadCount === 1);
    await api(c, 'PATCH', '/api/v1/chat/rooms/' + roomId + '/read', { lastReadMessageId: messages[0].id }, 400);
    const foreign = await api(a, 'POST', '/api/v1/chat/rooms/' + direct.id + '/messages', { content: 'Synthetic foreign-room cursor ' + run });
    await api(b, 'PATCH', '/api/v1/chat/rooms/' + roomId + '/read', { lastReadMessageId: foreign.id }, 400);

    // 실행: OPEN의 권한·관리·재참여를 실제 계정으로 검증하고 만든 방은 정상 close한다.
    const open = await api(a, 'POST', '/api/v1/chat/open-rooms', {
        name: 'Local moderation ' + run, description: 'Synthetic local verification',
        visibility: 'PUBLIC', maxMemberCount: 10, ownerProfile: { nickname: 'Owner' },
    }, 201);
    const openRoute = '/api/v1/chat/open-rooms/' + open.id;
    const joinedB = await api(b, 'POST', openRoute + '/join', { profile: { nickname: 'Admin' } });
    const joinedC = await api(c, 'POST', openRoute + '/join', { profile: { nickname: 'Member' } });
    const bOpenId = joinedB.myOpenProfile.openChatMemberId;
    const cOpenId = joinedC.myOpenProfile.openChatMemberId;
    await api(c, 'POST', openRoute + '/admins/' + bOpenId, undefined, 400);
    const promoted = await api(a, 'POST', openRoute + '/admins/' + bOpenId);
    check('OPEN owner promotes admin', promoted.role === 'ADMIN');
    const banned = await api(b, 'POST', openRoute + '/bans', {
        targetOpenChatMemberId: cOpenId, reason: 'Synthetic moderation verification',
    });
    check('OPEN admin bans ordinary member', banned.active === true && banned.targetOpenChatMemberId === cOpenId);
    await api(c, 'POST', openRoute + '/join', { profile: { nickname: 'Member' } }, 400);
    const released = await api(b, 'PATCH', openRoute + '/bans/' + banned.banId + '/release');
    check('OPEN ban release preserves audit time', released.active === false && released.releasedAt !== null);
    const rejoinedC = await api(c, 'POST', openRoute + '/join', { profile: { nickname: 'Member' } });
    check('OPEN rejoin reuses member identity', rejoinedC.myOpenProfile.openChatMemberId === cOpenId);
    const transferred = await api(a, 'POST', openRoute + '/owner-transfer', { targetOpenChatMemberId: bOpenId });
    check('OPEN owner transfer selects target member', transferred.ownerProfile.openChatMemberId === bOpenId);
    await api(a, 'POST', openRoute + '/close', undefined, 400);
    const closed = await api(b, 'POST', openRoute + '/close');
    check('OPEN new owner closes room', closed.status === 'CLOSED');

    // 실행: 두 CHAT 인스턴스와 동일 사용자의 두 session을 실제 Redis로 연결한다.
    const aSession = await connect(a);
    const bSession1 = await connect(b);
    const bSession2 = await connect(b, true);
    const aEvents = [];
    const bEvents = [];
    await subscribe(aSession, '/topic/chat/rooms/' + roomId, aEvents);
    await subscribe(bSession2, '/topic/chat/rooms/' + roomId, bEvents);
    const marker = 'Cross instance ' + run;
    aSession.publish({ destination: '/app/chat/rooms/' + roomId + '/messages', body: JSON.stringify({ content: marker }) });
    await until('second CHAT receives committed message from first CHAT', () => bEvents.some(event => event.message?.content === marker));

    await disconnect(bSession1);
    const remaining = await api(a, 'GET', '/api/v1/chat/rooms/' + roomId + '/members');
    const bMember = remaining.members.find(member => member.userId === b.userId);
    check('one disconnected session leaves other session online', bMember?.online === true);
    await disconnect(bSession2);
    const reconnect = await connect(b, true);
    await subscribe(reconnect, '/topic/chat/rooms/' + roomId, bEvents);
    const reconnected = await api(a, 'GET', '/api/v1/chat/rooms/' + roomId + '/members');
    check('reconnect during grace is online', reconnected.members.find(member => member.userId === b.userId)?.online === true);
    await disconnect(reconnect);
    await until('offline query after final session disconnect', async () => {
        const response = await fetch(origin + '/api/v1/chat/rooms/' + roomId + '/members', {
            headers: { Authorization: 'Bearer ' + a.accessToken },
            signal: AbortSignal.timeout(3000), redirect: 'error',
        });
        if (response.status !== 200) throw new ProbeFailure('Presence polling request failed.');
        const body = safeJson(await response.text(), 'Presence response').body;
        return body.members.find(member => member.userId === b.userId)?.online === false;
    });
    await until('offline event after configured grace', () => aEvents.some(event => event.eventType === 'chat.presence.changed'
        && event.roomId === roomId && event.memberRef === String(bMember.id) && event.online === false), 45000);

    // 검증: 저장된 응답을 다시 조회하며 room/message 원문은 결과 파일에 남기지 않는다.
    const page = await api(a, 'GET', '/api/v1/chat/rooms/' + roomId + '/messages');
    check('cross instance message persisted', page.messages.some(message => message.content === marker));
    return { roomId, directRoomId: direct.id, messagesCreated: 5 };
}

(async () => {
    let outcome;
    try {
        outcome = { status: 'PASS', ...(await main()) };
    } catch (error) {
        // 실패 원문은 토큰·payload 없이 우리가 만든 단계 이름만 기록한다.
        outcome = { status: 'FAIL', reason: error instanceof ProbeFailure ? error.message : 'Unexpected local transport or fixture failure; no raw diagnostic recorded.' };
        process.exitCode = 1;
    } finally {
        for (const client of sessions) await disconnect(client);
        fs.writeFileSync(resultFile, JSON.stringify({ ...outcome, checks, actualBrowser: false,
            configuredScope: 'Existing BE account DB and two CHAT instances sharing MySQL/Redis; each check records actual success' }, null, 2));
        console.log('Local runtime checks: ' + checks.filter(item => item.passed).length + '/' + checks.length + '; ' + outcome.status);
    }
})();
