// Thin wrapper around the /api/sessions REST endpoints (plain database
// CRUD). Real-time AI messages go over the WebSocket instead - see ws.js.
const Api = {
    async listSessions() {
        return fetchJson("/api/sessions");
    },

    async createSession(title) {
        return fetchJson("/api/sessions", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ title: title ?? null })
        });
    },

    // Returns { id, title, createdAt, updatedAt, messages: [...] }.
    async getSession(sessionId) {
        return fetchJson(`/api/sessions/${encodeURIComponent(sessionId)}`);
    },

    async renameSession(sessionId, title) {
        const response = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}`, {
            method: "PUT",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ title })
        });
        if (!response.ok) {
            throw new Error(`Rename failed with status ${response.status}`);
        }
    },

    async deleteSession(sessionId) {
        const response = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}`, { method: "DELETE" });
        if (!response.ok && response.status !== 404) {
            throw new Error(`Delete failed with status ${response.status}`);
        }
    }
};

async function fetchJson(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) {
        throw new Error(`Request to ${url} failed with status ${response.status}`);
    }
    // 204 No Content responses have no body to parse.
    if (response.status === 204) {
        return null;
    }
    return response.json();
}
