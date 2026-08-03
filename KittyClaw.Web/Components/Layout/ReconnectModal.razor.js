// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

// A server restart can invalidate a circuit between two user interactions. Blazor's
// default fatal-error banner otherwise appears only after the next click, which makes
// an innocent ticket look broken. If the new server is healthy, reload the stale page
// once automatically. A one-minute guard keeps genuine render bugs visible instead of
// creating a reload loop.
const fatalErrorUi = document.getElementById("blazor-error-ui");
const fatalReloadKey = "kittyclaw:last-fatal-circuit-reload";
if (fatalErrorUi) {
    const fatalObserver = new MutationObserver(recoverStaleCircuit);
    fatalObserver.observe(fatalErrorUi, { attributes: true, attributeFilter: ["style", "class"] });
}

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}

async function recoverStaleCircuit() {
    if (!fatalErrorUi || getComputedStyle(fatalErrorUi).display === "none") {
        return;
    }

    const lastReload = Number(sessionStorage.getItem(fatalReloadKey) || "0");
    if (Date.now() - lastReload < 60_000) {
        return;
    }

    try {
        const health = await fetch("/api/engine/health", { cache: "no-store" });
        if (health.ok) {
            sessionStorage.setItem(fatalReloadKey, String(Date.now()));
            location.reload();
        }
    } catch {
        // The server is still unavailable. Keep the actionable banner visible.
    }
}
