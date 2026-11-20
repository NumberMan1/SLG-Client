mergeInto(LibraryManager.library, {
    $SlgSockets: { nextId: 1, entries: {} },
    SlgSocketOpen__deps: ['$SlgSockets'],
    SlgSocketOpen: function(url) {
        var id = SlgSockets.nextId++;
        var entry = { socket: new WebSocket(UTF8ToString(url)), messages: [] };
        SlgSockets.entries[id] = entry;
        entry.socket.binaryType = 'arraybuffer';
        entry.socket.onmessage = function(event) {
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
            entry.socket.send(HEAPU8.slice(data, data + length));
            return 1;
        } catch (_) { return 0; }
    },
    SlgSocketReceive__deps: ['$SlgSockets'],
    SlgSocketReceive: function(id, data, capacity) {
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
