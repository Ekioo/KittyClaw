window.kittyclawAttachments = {
    async reveal(path) {
        if (!/^\/api\/projects\/[a-zA-Z0-9_-]+\/tickets\/\d+\/attachments\/[a-f0-9]{32}\/reveal$/.test(path)) return false;
        const response = await fetch(path, { method: "POST", credentials: "same-origin", redirect: "error" });
        return response.ok;
    }
};
