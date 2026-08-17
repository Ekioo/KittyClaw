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
    if (!el || el.__pasteHandlerInstalled) return;
    el.__pasteHandlerInstalled = true;

    var ALLOWED = { 'image/jpeg': 1, 'image/png': 1, 'image/gif': 1, 'image/webp': 1 };
    var MAX_BYTES = 5 * 1024 * 1024; // 5 MB per image
    var MAX_IMAGES = 5;

    el.addEventListener('paste', function (e) {
        var cd = e.clipboardData;
        if (!cd || !cd.items) return;
        var imageItems = [];
        for (var i = 0; i < cd.items.length; i++) {
            var it = cd.items[i];
            if (it.kind === 'file' && it.type && it.type.indexOf('image/') === 0) imageItems.push(it);
        }
        if (imageItems.length === 0) return; // let plain-text paste work normally

        var pastedText = cd.getData ? cd.getData('text/plain') : '';
        if (!pastedText) e.preventDefault();

        if (imageItems.length > MAX_IMAGES) {
            dotnetRef.invokeMethodAsync('OnImagePasteError', 'too_many');
            return;
        }

        dotnetRef.invokeMethodAsync('OnImagePasteStarted', imageItems.length);
        (async function () {
            for (var index = 0; index < imageItems.length; index++) {
                var file = imageItems[index].getAsFile();
                if (!file) continue;
                if (!ALLOWED[file.type]) {
                    await dotnetRef.invokeMethodAsync('OnImagePasteError', 'unsupported_type');
                    continue;
                }
                if (file.size > MAX_BYTES) {
                    await dotnetRef.invokeMethodAsync('OnImagePasteError', 'too_large');
                    continue;
                }
                try {
                    var dataUrl = await new Promise(function (resolve, reject) {
                        var reader = new FileReader();
                        reader.onload = function () { resolve(reader.result); };
                        reader.onerror = reject;
                        reader.readAsDataURL(file);
                    });
                    await dotnetRef.invokeMethodAsync('OnImagePasted', {
                        dataUrl: dataUrl,
                        mime: file.type,
                        name: file.name || 'pasted-image',
                        sizeBytes: file.size
                    });
                } catch (_) {
                    await dotnetRef.invokeMethodAsync('OnImagePasteError', 'read_failed');
                }
            }
        })().finally(function () {
            dotnetRef.invokeMethodAsync('OnImagePasteCompleted');
        });
    });
};
