// This file handles the browser side of the WebSocket connection.
// It talks to the same /ws endpoint that the WPF desktop app connects to.

const statusEl = document.getElementById("status");
const statusTextEl = document.getElementById("statusText");
const logEl = document.getElementById("log");
const messageInput = document.getElementById("messageInput");
const sendBtn = document.getElementById("sendBtn");

// Fixed URL matching the fixed port SocketWeb runs on (see Program.cs).
const socket = new WebSocket("ws://localhost:5080/ws");

socket.onopen = () => setStatus(true);
socket.onclose = () => setStatus(false);
socket.onerror = () => setStatus(false);

function setStatus(isConnected) {
    statusEl.className = "status-pill " + (isConnected ? "connected" : "disconnected");
    statusTextEl.textContent = isConnected ? "Connected" : "Disconnected";
    sendBtn.disabled = !isConnected;
}

// Every message that arrives here came from the server's broadcast -
// it could be a message from this same browser tab, another tab, or
// the desktop app. We only ever add messages to the log here, so the
// same message never shows up twice.
socket.onmessage = (event) => {
    const chatMessage = JSON.parse(event.data);
    appendToLog(chatMessage);
};

// Builds one chat bubble and appends it to the log. Messages sent as
// "Web" are shown on the right in blue ("mine"); anything else (e.g.
// "Desktop") is shown on the left in gray ("theirs") - just like a
// typical chat app.
function appendToLog(chatMessage) {
    const isMine = chatMessage.Sender === "Web";
    const time = new Date(chatMessage.Timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

    const row = document.createElement("div");
    row.className = "bubble-row " + (isMine ? "mine" : "theirs");

    const senderLabel = document.createElement("div");
    senderLabel.className = "sender-label";
    senderLabel.textContent = chatMessage.Sender;

    const bubble = document.createElement("div");
    bubble.className = "bubble";
    bubble.textContent = chatMessage.Text;

    const timeEl = document.createElement("div");
    timeEl.className = "bubble-time";
    timeEl.textContent = time;

    row.appendChild(senderLabel);
    row.appendChild(bubble);
    row.appendChild(timeEl);

    logEl.appendChild(row);
    logEl.scrollTop = logEl.scrollHeight;
}

function sendMessage() {
    const text = messageInput.value.trim();
    if (text === "" || socket.readyState !== WebSocket.OPEN) {
        return;
    }

    const chatMessage = {
        Sender: "Web",
        Text: text,
        Timestamp: new Date().toISOString()
    };

    socket.send(JSON.stringify(chatMessage));
    messageInput.value = "";
    messageInput.focus();
}

sendBtn.addEventListener("click", sendMessage);

// Also allow pressing Enter in the input box to send.
messageInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
        sendMessage();
    }
});
