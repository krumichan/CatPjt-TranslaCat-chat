const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const { test } = require('node:test');

// FE의 현재 파일을 읽어 메모리에서만 실행한다. FE 소스/build output/cache를 쓰지 않는다.
const root = path.resolve(__dirname, '..', '..');
const frontend = path.resolve(root, '..', 'CatPjt-TranslaCat-fe');
const typescript = require(path.join(frontend, 'node_modules/typescript/lib/typescript.js'));
const source = path.join(frontend, 'src/utils/chat/chatWebSocketParser.ts');
const bytes = fs.readFileSync(source);
const sourceHash = crypto.createHash('sha256').update(bytes).digest('hex');
const output = typescript.transpileModule(bytes.toString('utf8'), {
    compilerOptions: { target: typescript.ScriptTarget.ES2022, module: typescript.ModuleKind.CommonJS },
}).outputText;
const moduleExports = {};
vm.runInNewContext(output, {
    exports: moduleExports,
    require: () => { throw new Error('Unexpected runtime dependency in isolated FE parser.'); },
}, { filename: source });
const parser = moduleExports;
const at = '2026-09-26T12:00:00.123456Z';

test('현재 FE source hash가 실행 중 유지된다', () => {
    // 준비 / 실행 / 검증
    const current = crypto.createHash('sha256').update(fs.readFileSync(source)).digest('hex');
    assert.equal(current, sourceHash);
    console.log(`FE parser SHA256=${sourceHash}; TypeScript=${typescript.version}; synthetic fixtures only`);
});

test('자기 읽음: 문자열과 명시적 null을 보존하고 누락·잘못된 타입은 거절한다', () => {
    // 준비
    const event = { eventType: 'chat.read.updated', chatRoomId: 71, userId: 81,
        lastReadMessageId: 101, lastReadAt: at, unreadCount: 2, occurredAt: at };

    // 실행 / 검증 — 인계 후 null 수용을 검증하되 다른 필드 검증은 유지한다.
    assert.ok(parser.extractChatReadUpdatedEvent(event));
    const nullable = parser.extractChatReadUpdatedEvent({ ...event, lastReadAt: null });
    assert.ok(nullable);
    assert.equal(nullable.lastReadAt, null);
    for (const invalid of [undefined, 0, false, {}]) {
        assert.equal(parser.extractChatReadUpdatedEvent({ ...event, lastReadAt: invalid }), null);
    }
    assert.equal(parser.extractChatReadUpdatedEvent({ ...event, unreadCount: '2' }), null);
});

test('방 읽음: OPEN member ID와 nullable 이전 cursor를 수용한다', () => {
    // 준비
    const event = { eventType: 'chat.member.read.updated', chatRoomId: 71, readerUserId: null,
        readerOpenChatMemberId: 91, previousLastReadMessageId: null, lastReadMessageId: 101, readAt: at, occurredAt: at };

    // 실행 / 검증
    assert.ok(parser.extractChatMemberReadUpdatedEvent(event));
    assert.equal(parser.extractChatMemberReadUpdatedEvent({ ...event, readerOpenChatMemberId: null }), null);
});

test('멤버·역할·종료·차단 이벤트 wire를 수용한다', () => {
    // 준비
    const cases = [
        ['extractChatRoomMembersChangedEvent', { eventType: 'chat.members.changed', roomId: 71, occurredAt: at }],
        ['extractOpenChatMemberRoleUpdatedEvent', { eventType: 'chat.member.role.updated', roomId: 71,
            targetOpenChatMemberId: 91, role: 'ADMIN', occurredAt: at }],
        ['extractOpenChatRoomClosedEvent', { eventType: 'chat.room.closed', roomId: 71, closedAt: at, occurredAt: at }],
        ['extractOpenChatMemberBannedEvent', { eventType: 'chat.member.banned', roomId: 71, targetOpenChatMemberId: 91,
            reason: '합성 차단', bannedAt: at, occurredAt: at }],
    ];

    // 실행 / 검증
    for (const [method, value] of cases) assert.ok(parser[method](value), method);
});

test('OPEN 프로필과 Presence에는 일반 계정 식별자를 추가할 필요가 없다', () => {
    // 준비
    const profile = { eventType: 'chat.open-profile.updated', roomId: 71, openChatMemberId: 91,
        memberCode: 'OC-ABCDE', nickname: '합성 프로필', profileImageUrl: null, role: 'MEMBER', occurredAt: at };
    const presence = { eventType: 'chat.presence.changed', roomId: 71, roomType: 'OPEN', memberRef: '91', online: true, occurredAt: at };

    // 실행 / 검증
    assert.ok(parser.extractOpenChatProfileUpdatedEvent(profile));
    assert.ok(parser.extractChatPresenceChangedEvent(presence));
});

test('AI/SYSTEM 메시지의 nullable 발신자와 번역 목록을 수용한다', () => {
    // 준비
    const message = { id: 101, chatRoomId: 71, senderUserId: null, senderAiMemberId: 91,
        senderName: '합성 AI', senderEmail: null, senderType: 'AI', messageType: 'TEXT', content: '합성 내용',
        status: 'SENT', unreadMemberCount: 1, translations: [], createdAt: at, updatedAt: at, sender: null };

    // 실행 / 검증
    assert.ok(parser.extractChatMessageFromEvent({ eventType: 'chat.message.created', ...message }));
    assert.ok(parser.extractChatMessageFromEvent({ eventType: 'chat.message.created', ...message,
        senderAiMemberId: null, senderType: 'SYSTEM', messageType: 'SYSTEM', unreadMemberCount: null }));
});

test('번역 성공과 실패에서 FE는 occurredAt을 completedAt fallback으로 사용한다', () => {
    // 준비
    const common = { messageId: 101, translationId: 201, languageCode: 'ja', occurredAt: at };

    // 실행
    const success = parser.extractTranslationResultFromEvent({ ...common, eventType: 'chat.translation.completed',
        translatedContent: '合成', status: 'COMPLETED' }, 'COMPLETED');
    const failure = parser.extractTranslationResultFromEvent({ ...common, eventType: 'chat.translation.failed',
        translatedContent: null, status: 'FAILED', failureReason: '합성 실패' }, 'FAILED');

    // 검증 — FAILED DB completedAt=null과 현재 FE의 event 소비 값 차이를 숨기지 않는다.
    assert.equal(success.translation.completedAt, at);
    assert.equal(failure.translation.completedAt, at);
    assert.equal(failure.translation.translatedContent, null);
});

test('활동 알림의 payload object와 nullable readAt을 수용한다', () => {
    // 준비
    const notification = { id: 301, notificationType: 'OPEN_CHAT_ROLE_CHANGED', roomId: 71,
        payload: { roomName: '합성 방', newRole: 'OWNER' }, isRead: false, readAt: null, createdAt: at };

    // 실행 / 검증
    assert.ok(parser.extractChatNotificationCreatedItem({ eventType: 'chat.notification.created', notification, occurredAt: at }));
});

test('JSON 숫자 Long은 FE에서 정밀도가 유실될 수 있으며 parser가 막지 않는다', () => {
    // 준비 — 서버는 정수 토큰을 그대로 내지만 JavaScript Number 표현 범위가 다르다.
    const event = JSON.parse('{"eventType":"chat.read.updated","chatRoomId":71,"userId":81,"lastReadMessageId":9223372036854775807,"lastReadAt":"2026-09-26T12:00:00Z","unreadCount":0,"occurredAt":"2026-09-26T12:00:00Z"}');

    // 실행 / 검증 — 기존 공개 숫자 계약의 한계이며 문자열 ID로 무단 변경하지 않는다.
    assert.ok(parser.extractChatReadUpdatedEvent(event));
    assert.equal(Number.isSafeInteger(event.lastReadMessageId), false);
    assert.notEqual(BigInt(event.lastReadMessageId), 9223372036854775807n);
});
