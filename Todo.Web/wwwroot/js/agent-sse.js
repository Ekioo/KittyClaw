let _es = null;
let _ref = null;
let _autoScroll = true;
let _scrollEl = null;
let _observer = null;
let _scrollListening = false;

function _scrollToBottom() {
    if (_scrollEl) _scrollEl.scrollTop = _scrollEl.scrollHeight;
}

function _onScroll() {
    if (!_scrollEl) return;
    const el = _scrollEl;
    _autoScroll = el.scrollTop + el.clientHeight >= el.scrollHeight - 30;
}

export function initAutoScroll(element) {
    disposeAutoScroll();
    _scrollEl = element;
    _autoScroll = true;
    _scrollListening = false;

    // Use MutationObserver to auto-scroll whenever new children are added
    _observer = new MutationObserver(() => {
        if (_autoScroll) _scrollToBottom();
        // Attach scroll listener after first mutation (content rendered)
        if (!_scrollListening) {
            _scrollListening = true;
            element.addEventListener("scroll", _onScroll);
        }
    });
    _observer.observe(element, { childList: true, subtree: true });

    // Scroll for already-present content: immediate, next frame, and next task
    _scrollToBottom();
    requestAnimationFrame(_scrollToBottom);
    setTimeout(() => {
        _scrollToBottom();
        // Attach scroll listener if observer hasn't already
        if (!_scrollListening && _scrollEl === element) {
            _scrollListening = true;
            element.addEventListener("scroll", _onScroll);
        }
    }, 0);
}

export function scrollIfNeeded() {
    // Handled by MutationObserver now, but keep as fallback
    if (_autoScroll && _scrollEl) {
        requestAnimationFrame(_scrollToBottom);
    }
}

export function disposeAutoScroll() {
    if (_observer) { _observer.disconnect(); _observer = null; }
    if (_scrollEl && _scrollListening) _scrollEl.removeEventListener("scroll", _onScroll);
    _scrollEl = null;
    _autoScroll = true;
    _scrollListening = false;
}

export function start(dotnetRef, url) {
    stop();
    _ref = dotnetRef;
    _es = new EventSource(url);
    _es.onmessage = (ev) => {
        if (!_ref) return;
        try {
            const data = JSON.parse(ev.data);
            _ref.invokeMethodAsync("ReceiveSse", data.kind ?? "event", data.text ?? "");
        } catch {
            _ref.invokeMethodAsync("ReceiveSse", "raw", ev.data);
        }
    };
    _es.addEventListener("end", () => {
        if (_ref) _ref.invokeMethodAsync("StreamEnded");
        stop();
    });
    _es.onerror = () => {
        if (_ref) _ref.invokeMethodAsync("StreamEnded");
        stop();
    };
}

export function stop() {
    if (_es) { try { _es.close(); } catch {} _es = null; }
    _ref = null;
}
