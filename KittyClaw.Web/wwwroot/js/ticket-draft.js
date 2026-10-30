// Save directly from DOM input events, including while the Blazor connection is down.
window.ticketDraft = {
    loadConversation(key) {
        try { return JSON.parse(sessionStorage.getItem(key)); }
        catch { return null; }
    },
    saveConversation(key, draft) {
        sessionStorage.setItem(key, JSON.stringify(draft));
    },
    attach(popup, key) {
        const title = popup.querySelector('.create-input');
        const description = popup.querySelector('.create-textarea');
        let draft = null;
        try {
            const saved = JSON.parse(sessionStorage.getItem(key));
            if (Array.isArray(saved) && saved.length === 2 && saved.every(x => typeof x === 'string')) {
                draft = saved;
                title.value = saved[0];
                description.value = saved[1];
            }
        } catch { /* Storage may be unavailable. The form still works. */ }
        const save = () => {
            try { sessionStorage.setItem(key, JSON.stringify([title.value, description.value])); }
            catch { /* Keep editing if storage is unavailable. */ }
        };
        title.addEventListener('input', save);
        description.addEventListener('input', save);
        return draft;
    },
    clear(key) {
        sessionStorage.removeItem(key);
    }
};
