// The browser side of the real-time WebSocket connection to SocketWeb.
//
// Note on casing: messages sent/received here (SocketMessage) use
// PascalCase property names (Type, SessionId, RequestId, ...) because
// SocketWeb serializes them with System.Text.Json's default settings.
// This is different from the REST API in api.js, whose JSON uses
// camelCase (ASP.NET Core's default for minimal API responses). Both are
// intentional - just remember which one you're looking at.
const SocketClient = (() => {
    // Fixed URL matching the fixed port SocketWeb runs on (see
    // SocketWeb/Program.cs) - same simplification the original demo used.
    const SERVER_URL = "ws://localhost:5080/ws";

    const INITIAL_RECONNECT_DELAY_MS = 1000;
    const MAX_RECONNECT_DELAY_MS = 30000;

    let socket = null;
    let reconnectDelayMs = INITIAL_RECONNECT_DELAY_MS;
    let reconnectTimer = null;

    const listeners = { open: [], close: [], message: [] };

    function on(event, callback) {
        listeners[event].push(callback);
    }

    function connect() {
        clearTimeout(reconnectTimer);
        socket = new WebSocket(SERVER_URL);

        socket.onopen = () => {
            reconnectDelayMs = INITIAL_RECONNECT_DELAY_MS; // reset backoff after a successful connect
            sendClientHello();
            listeners.open.forEach((callback) => callback());
        };

        socket.onmessage = (event) => {
            let message;
            try {
                message = JSON.parse(event.data);
            } catch {
                return; // ignore anything that isn't valid JSON
            }
            listeners.message.forEach((callback) => callback(message));
        };

        socket.onclose = () => {
            listeners.close.forEach((callback) => callback());
            scheduleReconnect();
        };

        socket.onerror = () => {
            socket.close();
        };
    }

    function scheduleReconnect() {
        clearTimeout(reconnectTimer);
        reconnectTimer = setTimeout(() => {
            connect();
        }, reconnectDelayMs);
        reconnectDelayMs = Math.min(reconnectDelayMs * 2, MAX_RECONNECT_DELAY_MS);
    }

    function sendClientHello() {
        send({ Type: "ClientHello", Role: "Browser" });
    }

    function send(message) {
        if (!socket || socket.readyState !== WebSocket.OPEN) {
            return false;
        }
        socket.send(JSON.stringify(message));
        return true;
    }

    function isConnected() {
        return !!socket && socket.readyState === WebSocket.OPEN;
    }

    return { connect, send, on, isConnected };
})();
