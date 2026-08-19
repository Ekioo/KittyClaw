window.chatDrawerScrollToBottom = function (el) {
    if (el) el.scrollTop = el.scrollHeight;
};

// Block the default newline insertion when pressing Enter (without Shift) so the
// browser doesn't append "\n" after our Send() handler clears the textarea — that
// would re-fire oninput and restore the just-cleared text.
window.chatDrawerInstallEnterGuard = function (el) {
    if (!el || el.__enterGuardInstalled) return;
    el.__enterGuardInstalled = true;
    el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey) e.preventDefault();
    });
};

// Image paste support (#115). Watches the chat textarea for `paste` events carrying
// image clipboard items, validates them client-side, and bridges accepted images back
// to the Blazor component via JSInvokable callbacks. Plain-text pastes pass through
// unchanged. For mixed clipboard content, the browser inserts the text while this
// handler independently reads the images.
window.chatDrawerInstallPasteHandler = function (el, dotnetRef) {
    if (!el) return;
    // Blazor can retain the textarea DOM node while recreating the component/circuit.
    // Always refresh the bridge reference even when the DOM listener already exists.
    el.__pasteDotnetRef = dotnetRef;
    if (el.__pasteHandlerInstalled) return;
    el.__pasteHandlerInstalled = true;

    var ALLOWED = { 'image/jpeg': 1, 'image/png': 1, 'image/gif': 1, 'image/webp': 1 };
    var MAX_BYTES = 5 * 1024 * 1024; // 5 MB per image
    var MAX_IMAGES = 5;

    el.addEventListener('paste', function (e) {
        var cd = e.clipboardData;
        if (!cd || !cd.items) return;
        // Snapshot the File objects synchronously: browsers neuter DataTransferItems as
        // soon as the paste handler returns, so getAsFile() after any await returns null
        // and the images would silently vanish (real pastes only — synthetic QA events
        // keep their items alive, which is how this bug slipped past scenario tests).
        var imageItemCount = 0;
        var files = [];
        for (var i = 0; i < cd.items.length; i++) {
            var it = cd.items[i];
            if (it.kind === 'file' && it.type && it.type.indexOf('image/') === 0) {
                imageItemCount++;
                var f = it.getAsFile();
                if (f) files.push(f);
            }
        }
        if (imageItemCount === 0) return; // let plain-text paste work normally

        var pastedText = cd.getData ? cd.getData('text/plain') : '';
        if (!pastedText) e.preventDefault();

        var bridge = el.__pasteDotnetRef;
        if (!bridge) return;
        if (files.length === 0) {
            bridge.invokeMethodAsync('OnImagePasteError', 'read_failed');
            return;
        }
        if (files.length > MAX_IMAGES) {
            bridge.invokeMethodAsync('OnImagePasteError', 'too_many');
            return;
        }

        (async function () {
            await bridge.invokeMethodAsync('OnImagePasteStarted', files.length);
            try {
                for (var index = 0; index < files.length; index++) {
                    var file = files[index];
                    if (!ALLOWED[file.type]) {
                        await bridge.invokeMethodAsync('OnImagePasteError', 'unsupported_type');
                        continue;
                    }
                    if (file.size > MAX_BYTES) {
                        await bridge.invokeMethodAsync('OnImagePasteError', 'too_large');
                        continue;
                    }
                    try {
                        var dataUrl = await new Promise(function (resolve, reject) {
                            var reader = new FileReader();
                            reader.onload = function () { resolve(reader.result); };
                            reader.onerror = reject;
                            reader.readAsDataURL(file);
                        });
                        await bridge.invokeMethodAsync('OnImagePasted', {
                            dataUrl: dataUrl,
                            mime: file.type,
                            name: file.name || 'pasted-image',
                            sizeBytes: file.size
                        });
                    } catch (_) {
                        await bridge.invokeMethodAsync('OnImagePasteError', 'read_failed');
                    }
                }
            } finally {
                await bridge.invokeMethodAsync('OnImagePasteCompleted');
            }
        })().catch(function (error) {
            console.warn('KittyClaw image paste failed', error);
        });
    });
};
