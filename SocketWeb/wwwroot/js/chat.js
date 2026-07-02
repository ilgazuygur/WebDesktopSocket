// Ties api.js (REST CRUD) and ws.js (real-time AI messages) together:
// renders the sidebar and message list, and drives the composer.
//
// Casing reminder (see ws.js for the long version): objects from Api.*
// use camelCase properties (id, title, role, content, ...); objects from
// SocketClient's "message" event use PascalCase (Type, SessionId,
// Content, ...). Both appear in this file - read the property name to
// tell which one you're looking at.

const sidebarEl = document.getElementById("sidebar");
const sidebarBackdropEl = document.getElementById("sidebarBackdrop");
const sidebarToggleEl = document.getElementById("sidebarToggle");
const newChatBtnEl = document.getElementById("newChatBtn");
const sessionListEl = document.getElementById("sessionList");

const sessionTitleHeadingEl = document.getElementById("sessionTitleHeading");
const wsStatusPillEl = document.getElementById("wsStatusPill");
const wsStatusLabelEl = document.getElementById("wsStatusLabel");
const desktopStatusPillEl = document.getElementById("desktopStatusPill");
const desktopStatusLabelEl = document.getElementById("desktopStatusLabel");

const chatAreaEl = document.getElementById("chatArea");
const emptyStateEl = document.getElementById("emptyState");
const messageListEl = document.getElementById("messageList");

const requestErrorEl = document.getElementById("requestError");
const messageInputEl = document.getElementById("messageInput");
const sendBtnEl = document.getElementById("sendBtn");
const thinkingIndicatorEl = document.getElementById("thinkingIndicator");

const state = {
    sessions: [], // camelCase session summaries from the REST API
    selectedSessionId: null,
    pendingRequestId: null
};

// ---------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------

async function refreshSessionList() {
    try {
        state.sessions = await Api.listSessions();
    } catch {
        return; // non-fatal - sidebar just stays stale until the next refresh
    }

    renderSessionList();

    if (state.selectedSessionId) {
        const current = state.sessions.find((s) => s.id === state.selectedSessionId);
        if (current) {
            sessionTitleHeadingEl.textContent = displayTitle(current.title);
        }
    }
}

function renderSessionList() {
    sessionListEl.replaceChildren();

    if (state.sessions.length === 0) {
        const empty = document.createElement("div");
        empty.className = "session-list-empty";
        empty.textContent = "No chats yet";
        sessionListEl.appendChild(empty);
        return;
    }

    state.sessions.forEach((session) => sessionListEl.appendChild(buildSessionItem(session)));
}

function buildSessionItem(session) {
    const item = document.createElement("div");
    item.className = "session-item" + (session.id === state.selectedSessionId ? " active" : "");

    const title = document.createElement("span");
    title.className = "session-item-title";
    title.textContent = displayTitle(session.title);
    item.appendChild(title);

    const actions = document.createElement("span");
    actions.className = "session-item-actions";

    const renameBtn = buildIconButton(RENAME_ICON_SVG, "Rename chat");
    renameBtn.addEventListener("click", (event) => {
        event.stopPropagation();
        startRenaming(title, session);
    });
    actions.appendChild(renameBtn);

    const deleteBtn = buildIconButton(DELETE_ICON_SVG, "Delete chat");
    deleteBtn.addEventListener("click", (event) => {
        event.stopPropagation();
        deleteSession(session);
    });
    actions.appendChild(deleteBtn);

    item.appendChild(actions);
    item.addEventListener("click", () => selectSession(session.id));

    return item;
}

// Static, developer-authored icon markup only (never user/AI content) -
// safe to set via innerHTML, unlike message text or session titles,
// which always go through textContent elsewhere in this file.
const RENAME_ICON_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 20h9"/><path d="M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>';
const DELETE_ICON_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 6h18"/><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>';

function buildIconButton(svgMarkup, label) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "icon-btn";
    button.setAttribute("aria-label", label);
    button.innerHTML = svgMarkup;
    return button;
}

function displayTitle(title) {
    return title && title.length > 0 ? title : "New chat";
}

function startRenaming(titleEl, session) {
    const input = document.createElement("input");
    input.type = "text";
    input.className = "session-rename-input";
    input.value = session.title || "";
    titleEl.replaceWith(input);
    input.focus();
    input.select();

    let finished = false;
    const finish = async (commit) => {
        if (finished) return;
        finished = true;
        const newTitle = input.value.trim();
        input.replaceWith(titleEl);

        if (!commit || newTitle.length === 0 || newTitle === session.title) {
            return;
        }

        try {
            await Api.renameSession(session.id, newTitle);
            session.title = newTitle;
            titleEl.textContent = newTitle;
            if (session.id === state.selectedSessionId) {
                sessionTitleHeadingEl.textContent = newTitle;
            }
        } catch {
            showRequestError("Could not rename this chat. Please try again.");
        }
    };

    input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
            event.preventDefault();
            finish(true);
        } else if (event.key === "Escape") {
            event.preventDefault();
            finish(false);
        }
    });
    input.addEventListener("blur", () => finish(true));
}

async function deleteSession(session) {
    const confirmed = confirm(`Delete "${displayTitle(session.title)}"? This can't be undone.`);
    if (!confirmed) {
        return;
    }

    try {
        await Api.deleteSession(session.id);
    } catch {
        showRequestError("Could not delete this chat. Please try again.");
        return;
    }

    state.sessions = state.sessions.filter((s) => s.id !== session.id);
    renderSessionList();

    if (state.selectedSessionId === session.id) {
        await startNewChat();
    }
}

// ---------------------------------------------------------------------
// Selecting / starting a chat
// ---------------------------------------------------------------------

async function selectSession(sessionId) {
    state.selectedSessionId = sessionId;
    state.pendingRequestId = null;
    hideThinkingIndicator();
    hideRequestError();
    updateComposerState();
    renderSessionList();
    closeMobileSidebar();

    try {
        const session = await Api.getSession(sessionId);
        sessionTitleHeadingEl.textContent = displayTitle(session.title);
        renderMessages(session.messages);
    } catch {
        showRequestError("Could not load this chat.");
    }
}

async function startNewChat() {
    state.selectedSessionId = null;
    state.pendingRequestId = null;
    hideThinkingIndicator();
    hideRequestError();
    updateComposerState();
    sessionTitleHeadingEl.textContent = "New chat";
    renderMessages([]);
    renderSessionList();
    closeMobileSidebar();
    messageInputEl.focus();
}

// ---------------------------------------------------------------------
// Message list
// ---------------------------------------------------------------------

function renderMessages(messages) {
    messageListEl.replaceChildren();

    if (!messages || messages.length === 0) {
        emptyStateEl.hidden = false;
        messageListEl.hidden = true;
        return;
    }

    // messages come from the REST API - camelCase (role, content).
    messages.forEach((message) => appendMessageBubble(message.role, message.content));
}

function appendMessageBubble(role, content) {
    emptyStateEl.hidden = true;
    messageListEl.hidden = false;

    const row = document.createElement("div");
    row.className = "message-row " + (role === "user" ? "user" : "assistant");

    const bubble = document.createElement("div");
    bubble.className = "message-bubble";
    // textContent only - message content is never trusted as HTML. Line
    // breaks are preserved safely via CSS "white-space: pre-wrap" (see
    // site.css) rather than converting "\n" into "<br>" markup.
    bubble.textContent = content;

    row.appendChild(bubble);
    messageListEl.appendChild(row);
    chatAreaEl.scrollTop = chatAreaEl.scrollHeight;
}

// ---------------------------------------------------------------------
// Composer
// ---------------------------------------------------------------------

function autoGrowTextarea() {
    messageInputEl.style.height = "auto";
    messageInputEl.style.height = Math.min(messageInputEl.scrollHeight, 200) + "px";
}

function updateComposerState() {
    const hasText = messageInputEl.value.trim().length > 0;
    const busy = state.pendingRequestId !== null;
    messageInputEl.disabled = busy;
    sendBtnEl.disabled = busy || !hasText || !SocketClient.isConnected();
}

async function sendMessage() {
    const text = messageInputEl.value.trim();
    if (!text || state.pendingRequestId) {
        return;
    }

    if (!SocketClient.isConnected()) {
        showRequestError("Not connected to the server. Please wait for it to reconnect.");
        return;
    }

    let sessionId = state.selectedSessionId;

    // A brand new chat doesn't get a real session until the first
    // message is actually sent - this keeps empty "New chat" clicks from
    // cluttering the sidebar with sessions nobody ever used.
    if (!sessionId) {
        try {
            const created = await Api.createSession(null);
            sessionId = created.id;
            state.selectedSessionId = sessionId;
            state.sessions.unshift(created);
            renderSessionList();
        } catch {
            showRequestError("Could not start a new chat. Please try again.");
            return;
        }
    }

    const requestId = crypto.randomUUID();
    state.pendingRequestId = requestId;
    hideRequestError();

    // The server never echoes the user's own prompt back - it only
    // confirms receipt with a "thinking" status - so the bubble is added
    // here, optimistically, right away.
    appendMessageBubble("user", text);
    messageInputEl.value = "";
    autoGrowTextarea();
    updateComposerState();

    const sent = SocketClient.send({
        Type: "UserPrompt",
        SessionId: sessionId,
        RequestId: requestId,
        Content: text
    });

    if (!sent) {
        showRequestError("Not connected to the server. Please try again.");
        state.pendingRequestId = null;
        updateComposerState();
    }
}

// ---------------------------------------------------------------------
// WebSocket message handling
// ---------------------------------------------------------------------

function handleSocketMessage(message) {
    switch (message.Type) {
        case "HelloAck":
            setDesktopStatus(message.Content === "desktop-online");
            break;
        case "Status":
            handleStatusMessage(message);
            break;
        case "AiResponse":
            handleAiResponse(message);
            break;
        case "Error":
            handleServerError(message);
            break;
        default:
            break; // AiRequest/ClientHello are not expected here
    }
}

function handleStatusMessage(message) {
    if (message.Content === "desktop-online") {
        setDesktopStatus(true);
        return;
    }
    if (message.Content === "desktop-offline") {
        setDesktopStatus(false);
        return;
    }
    if (message.Content === "thinking" && message.RequestId === state.pendingRequestId) {
        showThinkingIndicator();
        // The user message (and, if this was a new chat, its
        // auto-generated title) is guaranteed saved by the time this
        // arrives - refresh the sidebar so the title/ordering catch up.
        refreshSessionList();
    }
}

function handleAiResponse(message) {
    if (message.RequestId !== state.pendingRequestId) {
        return; // stale/unrelated to what this tab is waiting on - already persisted server-side either way
    }

    state.pendingRequestId = null;
    hideThinkingIndicator();
    updateComposerState();

    if (message.SessionId === state.selectedSessionId) {
        appendMessageBubble("assistant", message.Content);
    }
}

function handleServerError(message) {
    if (message.RequestId && message.RequestId !== state.pendingRequestId) {
        return;
    }

    state.pendingRequestId = null;
    hideThinkingIndicator();
    updateComposerState();
    showRequestError(message.Error || "Something went wrong. Please try again.");
}

// ---------------------------------------------------------------------
// Status pills / error banner / thinking indicator
// ---------------------------------------------------------------------

function setWsStatus(connected) {
    wsStatusPillEl.className = "status-pill " + (connected ? "connected" : "disconnected");
    wsStatusLabelEl.textContent = connected ? "Connected" : "Disconnected";
    updateComposerState();
}

function setDesktopStatus(online) {
    desktopStatusPillEl.className = "status-pill " + (online ? "online" : "offline");
    desktopStatusLabelEl.textContent = online ? "AI: Online" : "AI: Offline";
}

function showRequestError(text) {
    requestErrorEl.textContent = text;
    requestErrorEl.hidden = false;
}

function hideRequestError() {
    requestErrorEl.hidden = true;
    requestErrorEl.textContent = "";
}

function showThinkingIndicator() {
    thinkingIndicatorEl.hidden = false;
}

function hideThinkingIndicator() {
    thinkingIndicatorEl.hidden = true;
}

// ---------------------------------------------------------------------
// Mobile sidebar
// ---------------------------------------------------------------------

function openMobileSidebar() {
    sidebarEl.classList.add("open");
    sidebarBackdropEl.classList.add("open");
}

function closeMobileSidebar() {
    sidebarEl.classList.remove("open");
    sidebarBackdropEl.classList.remove("open");
}

// ---------------------------------------------------------------------
// Wiring it all together
// ---------------------------------------------------------------------

SocketClient.on("open", async () => {
    setWsStatus(true);
    await refreshSessionList();

    // Reload (rather than append to) the currently-selected session's
    // messages on every (re)connect, including the very first one. This
    // is what prevents duplicate bubbles after a refresh or a dropped
    // connection - the message list is always replaced wholesale from
    // the database, never merged with whatever was already on screen.
    if (state.selectedSessionId) {
        try {
            const session = await Api.getSession(state.selectedSessionId);
            sessionTitleHeadingEl.textContent = displayTitle(session.title);
            renderMessages(session.messages);
        } catch {
            await startNewChat();
        }
    }
});

SocketClient.on("close", () => {
    setWsStatus(false);
    // The real desktop status can't be known while disconnected from the
    // server - show offline until a fresh HelloAck says otherwise.
    setDesktopStatus(false);
});

SocketClient.on("message", handleSocketMessage);

function init() {
    messageInputEl.addEventListener("input", () => {
        autoGrowTextarea();
        updateComposerState();
    });

    messageInputEl.addEventListener("keydown", (event) => {
        if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            sendMessage();
        }
        // Shift+Enter falls through to the textarea's default behavior
        // (insert a newline).
    });

    sendBtnEl.addEventListener("click", sendMessage);
    newChatBtnEl.addEventListener("click", startNewChat);

    sidebarToggleEl.addEventListener("click", () => {
        if (sidebarEl.classList.contains("open")) {
            closeMobileSidebar();
        } else {
            openMobileSidebar();
        }
    });
    sidebarBackdropEl.addEventListener("click", closeMobileSidebar);

    updateComposerState();
    SocketClient.connect();
}

init();
