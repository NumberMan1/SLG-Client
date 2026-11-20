// 将浏览器 WebSocket 桥接给 Unity，通过整数句柄管理连接和接收队列。
mergeInto(LibraryManager.library, {
    $SlgSockets: { nextId: 1, entries: {} },
    SlgSocketOpen__deps: ['$SlgSockets'],
    SlgSocketOpen: function(url) {
        var id = SlgSockets.nextId++;
        var entry = { socket: new WebSocket(UTF8ToString(url)), messages: [] };
        SlgSockets.entries[id] = entry;
        entry.socket.binaryType = 'arraybuffer';
        entry.socket.onmessage = function(event) {
            // 1034 字节为帧头加负载上限，队列长度限制浏览器端缓存占用。
            if (!(event.data instanceof ArrayBuffer) || event.data.byteLength > 1034 || entry.messages.length >= 16) {
                entry.socket.close(1009, 'Frame limit exceeded');
                return;
            }
            entry.messages.push(new Uint8Array(event.data));
        };
        entry.socket.onerror = function() { entry.socket.close(); };
        return id;
    },
    SlgSocketState__deps: ['$SlgSockets'],
    SlgSocketState: function(id) {
        var entry = SlgSockets.entries[id];
        return entry ? entry.socket.readyState : 3;
    },
    SlgSocketSend__deps: ['$SlgSockets'],
    SlgSocketSend: function(id, data, length) {
        var entry = SlgSockets.entries[id];
        if (!entry || entry.socket.readyState !== 1 || length > 1034) return 0;
        try {
            // 复制 WASM 内存中的帧，再交给浏览器异步发送。
            entry.socket.send(HEAPU8.slice(data, data + length));
            return 1;
        } catch (_) { return 0; }
    },
    SlgSocketReceive__deps: ['$SlgSockets'],
    SlgSocketReceive: function(id, data, capacity) {
        // 返回 0 表示暂无消息，负值表示无效帧，正值表示写入的字节数。
        var entry = SlgSockets.entries[id];
        if (!entry || entry.messages.length === 0) return 0;
        var message = entry.messages.shift();
        if (message.length > capacity || message.length === 0) return -1;
        HEAPU8.set(message, data);
        return message.length;
    },
    SlgSocketClose__deps: ['$SlgSockets'],
    SlgSocketClose: function(id) {
        var entry = SlgSockets.entries[id];
        if (!entry) return;
        entry.socket.onmessage = null;
        entry.socket.close();
        delete SlgSockets.entries[id];
    }
});
