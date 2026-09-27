const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const frontend = path.resolve(__dirname, '..', '..', '..', 'CatPjt-TranslaCat-fe');
const { Client } = require(path.join(frontend, 'node_modules/@stomp/stompjs'));
const typescript = require(path.join(frontend, 'node_modules/typescript'));
const source = fs.readFileSync(path.join(frontend, 'src/utils/chat/chatWebSocketParser.ts'), 'utf8');
const parser = {};
vm.runInNewContext(typescript.transpileModule(source, {
    compilerOptions: { target: typescript.ScriptTarget.ES2022, module: typescript.ModuleKind.CommonJS },
}).outputText, { exports: parser });

function deferred() {
    let resolve, reject;
    const promise = new Promise((accept, fail) => { resolve = accept; reject = fail; });
    return { promise, resolve, reject };
}

async function connect(address, token) {
    const connected = deferred();
    const client = new Client({
        brokerURL: address.replace('http:', 'ws:').replace(/\/$/, '') + '/ws/chat',
        connectHeaders: { Authorization: `Bearer ${token}` },
        // 실제 FE 설정과 같은 heartbeat 제안값을 사용한다. 이 시험에서는 자동 재연결을 끈다.
        heartbeatIncoming: 10000,
        heartbeatOutgoing: 10000,
        reconnectDelay: 0,
        onConnect: frame => connected.resolve(frame),
        onStompError: () => connected.reject(new Error('STOMP connection rejected')),
        onWebSocketError: () => connected.reject(new Error('WebSocket connection failed')),
    });
    client.activate();
    const frame = await connected.promise;
    assert.equal(frame.headers.version, '1.2');
    assert.equal(frame.headers['heart-beat'], '0,0');
    return client;
}

async function subscribe(client, destination, callback) {
    const receipt = deferred();
    const id = crypto.randomUUID();
    client.watchForReceipt(id, () => receipt.resolve());
    client.subscribe(destination, callback, { receipt: id });
    await receipt.promise;
}

async function main() {
    // 합성 토큰은 command line/로그에 넣지 않고 테스트 부모의 stdin으로만 받는다.
    let input = '';
    for await (const bytes of process.stdin) input += bytes;
    const fixture = JSON.parse(input);
    const clients = [];
    const timeout = setTimeout(() => { console.error('FE_STOMP_TIMEOUT'); process.exit(1); }, 15000);
    try {
        const writer = await connect(fixture.writerAddress, fixture.writerToken);
        clients.push(writer);
        const reader = await connect(fixture.readerAddress, fixture.readerToken);
        clients.push(reader);
        const message = deferred();
        const read = deferred();
        await subscribe(reader, `/topic/chat/rooms/${fixture.roomId}`, frame => {
            const event = JSON.parse(frame.body);
            if (parser.getChatWebSocketEventType(event) !== 'chat.message.created') return;
            const value = parser.extractChatMessageFromEvent(event);
            if (value && value.content === '실제 FE STOMP 합성') message.resolve(value);
        });
        await subscribe(reader, '/user/queue/chat/read', frame => {
            const value = parser.extractChatReadUpdatedEvent(JSON.parse(frame.body));
            if (value) read.resolve(value);
        });

        // 실제 FE 패키지의 SEND → CHAT EF commit → Redis → 다른 app의 MESSAGE를 parser로 소비한다.
        writer.publish({ destination: `/app/chat/rooms/${fixture.roomId}/messages`, body: JSON.stringify({ content: '실제 FE STOMP 합성' }) });
        const created = await message.promise;
        assert.equal(created.chatRoomId, fixture.roomId);
        assert.equal(created.senderUserId, fixture.writerUserId);
        const response = await fetch(fixture.readerAddress + `api/v1/chat/rooms/${fixture.roomId}/read`, {
            method: 'PATCH', headers: { Authorization: `Bearer ${fixture.readerToken}`, 'Content-Type': 'application/json' },
            body: JSON.stringify({ lastReadMessageId: created.id }),
        });
        assert.equal(response.status, 200);
        const own = await read.promise;
        assert.equal(own.lastReadMessageId, created.id);
        assert.equal(own.unreadCount, 0);
        console.log(`FE_STOMP_PASS messageId=${created.id} protocol=1.2 client=7.3.0`);
    } finally {
        await Promise.all(clients.map(client => client.deactivate()));
        clearTimeout(timeout);
    }
}

main().catch(error => {
    // 토큰/원본 프레임을 출력하지 않는다. 상세 데이터 검증은 부모 테스트의 DB assertion에서 수행한다.
    console.error(`FE_STOMP_FAILURE ${error.name}`);
    process.exitCode = 1;
});
