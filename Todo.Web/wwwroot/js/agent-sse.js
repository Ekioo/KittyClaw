let _es = null;
let _ref = null;

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
