window.chatDrawerScrollToBottom = function (el) {
    if (el) el.scrollTop = el.scrollHeight;
};

// Handle submission locally: ordinary keystrokes do not need a server keydown event.
// Send the current DOM value so Enter cannot submit an older server-side draft.
window.chatDrawerInstallEnterGuard = function (el, dotnetRef) {
    if (!el) return;
    el.__enterDotnetRef = dotnetRef;
    if (el.__enterGuardInstalled) return;
    el.__enterGuardInstalled = true;
    el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && e.keyCode !== 229) {
            e.preventDefault();
            if (!e.repeat && !el.disabled && el.__enterDotnetRef) {
                el.__enterDotnetRef.invokeMethodAsync('OnComposerEnter', el.value)
                    .catch(() => console.warn('Chat submission could not reach the server.'));
            }
        }
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

    el.__acceptImageFiles = async function (files) {
        var bridge = el.__pasteDotnetRef;
        if (!bridge || !files.length) return;
        if (files.length > MAX_IMAGES) {
            await bridge.invokeMethodAsync('OnImagePasteError', 'too_many');
            return;
        }
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
    };

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

        el.__acceptImageFiles(files).catch(function (error) {
            console.warn('KittyClaw image paste failed', error);
        });
    });
};

// File selection uses exactly the same validation and callbacks as clipboard images.
window.chatDrawerPickImages = function (el) {
    if (!el || !el.__acceptImageFiles) return;
    var picker = document.createElement('input');
    picker.type = 'file';
    picker.accept = 'image/png,image/jpeg,image/gif,image/webp';
    picker.multiple = true;
    picker.hidden = true;
    picker.addEventListener('change', function () {
        var files = Array.from(picker.files || []);
        picker.remove();
        el.__acceptImageFiles(files).catch(function (error) { console.warn('KittyClaw image selection failed', error); });
    }, { once: true });
    picker.addEventListener('cancel', function () { picker.remove(); }, { once: true });
    document.body.appendChild(picker);
    picker.click();
};
