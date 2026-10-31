// Browser-final results usually correspond to a pause; fill missing sentence marks.
export function formatSpeechResults(results, language, finish = false, sentenceStart = true) {
    const french = language?.toLowerCase().startsWith('fr');
    const phrases = results.map(result => {
        let phrase = result.text.trim();
        if (french) {
            phrase = phrase
                .replace(/\bpoint d[’']interrogation\b/gi, '?')
                .replace(/\bpoint d[’']exclamation\b/gi, '!')
                .replace(/\bpoint[ -]virgule\b/gi, ';')
                .replace(/\bdeux[ -]points\b/gi, ':')
                .replace(/\bpoint final\b/gi, '.')
                .replace(/\bvirgule\b/gi, ',')
                .replace(/(?:à|a) la ligne/gi, '\n');
        }
        phrase = phrase.replace(/[ \t]+([.,…])/g, '$1');
        return phrase;
    });
    let text = phrases.map((phrase, index) => {
        const nextStartsWithPunctuation = /^[.,…;!?:]/.test(phrases[index + 1]?.trimStart() || '');
        if ((results[index].final || finish) && !nextStartsWithPunctuation && /[\p{L}\p{N})\]»”"']$/u.test(phrase)) phrase += '.';
        return phrase;
    }).join(' ').trim();
    text = text.replace(/[ \t]+([.,…])/g, '$1').replace(/[ \t]*\n[ \t]*/g, '\n');
    if (french) text = text.replace(/[ \t\u00a0\u202f]*([;!?])/g, '\u202f$1')
        .replace(/[ \t\u00a0\u202f]*:/g, '\u00a0:');
    text = text.replace(/([.!?][ \t\u00a0\u202f]+|\n)(\p{L})/gu, (_, space, letter) => space + letter.toLocaleUpperCase(language));
    if (sentenceStart) text = text.replace(/^(\p{L})/u, letter => letter.toLocaleUpperCase(language));
    return text;
}

let active = null;

export function attach(root) {
    const toggle = root.querySelector('.speech-toggle');
    const status = root.querySelector('.speech-status');
    const scope = root.closest('.conversation-input-area, .fullscreen-editor');
    const target = scope?.querySelector('textarea');
    const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    let blockedReason = '';
    let permission = null;
    const refresh = () => {
        const disabled = !!blockedReason || root.dataset.disabled === 'true';
        if (toggle.disabled !== disabled) toggle.disabled = disabled;
        const label = blockedReason || (recognition ? root.dataset.stop : root.dataset.start);
        toggle.title = root.title = label;
        toggle.setAttribute('aria-label', label);
        if (blockedReason) root.dataset.reason = blockedReason;
        else delete root.dataset.reason;
        root.tabIndex = blockedReason ? 0 : -1;
    };
    let recognition = null;
    let disposed = false;
    let updating = false;
    let start = 0;
    let end = 0;
    let ignoredThrough = -1;
    let lastResult = -1;
    let lastResults = [];

    const writeResults = (finish = false) => {
        if (!target?.isConnected || target.disabled || target.readOnly) return;
        const before = target.value.slice(0, start);
        const after = target.value.slice(end);
        const language = root.dataset.language || document.documentElement.lang;
        const words = formatSpeechResults(lastResults.slice(ignoredThrough + 1), language, finish,
            !before.trim() || /[.!?][\s"'»”)]*$|\n\s*$/u.test(before));
        if (!words) return;
        const french = language?.toLowerCase().startsWith('fr');
        let leading = before && !/\s$/.test(before) && !/^[.,…\])}’']/.test(words) && !/[\[({’']$/.test(before) ? ' ' : '';
        if (!french && /^[;!?:]/.test(words)) leading = '';
        let text = leading + words + (after && !/^\s|^[.,…;!?:\])}’']/.test(after) ? ' ' : '');
        if (target.maxLength >= 0) text = text.slice(0, Math.max(0, target.maxLength - before.length - after.length));
        target.setRangeText(text, start, end, 'end');
        end = start + text.length;
        updating = true;
        try {
            target.dispatchEvent(new Event('input', { bubbles: true }));
            target.dispatchEvent(new Event('change', { bubbles: true }));
        } finally { updating = false; }
    };
    const reset = () => {
        refresh();
        toggle.setAttribute('aria-pressed', 'false');
        if (active === cancel) active = null;
    };
    const cancel = () => {
        if (recognition) writeResults(true);
        const previous = recognition;
        recognition = null;
        if (previous) {
            previous.onresult = previous.onerror = previous.onend = null;
            previous.abort();
        }
        reset();
    };
    // Rebase on manual edits so later recognition never overwrites corrections.
    const onInput = () => {
        if (updating || !recognition) return;
        ignoredThrough = lastResult;
        start = end = target.selectionStart ?? target.value.length;
    };
    // Freeze the visible draft before any explicit send/save handler runs.
    const onClick = event => {
        if (event.target.closest('button') && !toggle.contains(event.target)) cancel();
    };
    const onKeyDown = event => {
        if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) cancel();
    };
    target?.addEventListener('input', onInput);
    target?.addEventListener('keydown', onKeyDown, true);
    scope?.addEventListener('click', onClick, true);
    const observer = new MutationObserver(() => {
        if (!root.isConnected || target?.disabled || toggle.disabled) cancel();
    });
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['disabled'] });
    const onVisibility = () => { if (document.hidden) cancel(); };
    document.addEventListener('visibilitychange', onVisibility);
    const block = reason => {
        blockedReason = reason;
        status.textContent = "";
        cancel();
        refresh();
    };
    const policy = document.permissionsPolicy || document.featurePolicy;
    const unavailable = !Recognition ? root.dataset.unavailable
        : !window.isSecureContext ? root.dataset.insecure
        : policy?.allowsFeature && !policy.allowsFeature('microphone') ? root.dataset.denied : '';
    if (unavailable) block(unavailable);
    else {
        refresh();
        // Querying permission never prompts; unsupported permission descriptors are ignored.
        window.navigator?.permissions?.query({ name: 'microphone' }).then(result => {
            if (disposed) return;
            permission = result;
            const changed = () => {
                if (disposed) return;
                if (permission.state === 'denied') block(root.dataset.denied);
                else if (blockedReason === root.dataset.denied) { blockedReason = ''; refresh(); }
            };
            permission.onchange = changed;
            changed();
        }).catch(() => {});
    }
    toggle.onclick = () => {
        if (recognition) { recognition.stop(); return; }
        if (blockedReason || toggle.disabled || !target || target.disabled || target.readOnly) return;
        active?.();
        active = cancel;
        start = target.selectionStart ?? target.value.length;
        end = target.selectionEnd ?? start;
        ignoredThrough = lastResult = -1;
        lastResults = [];
        let session;
        try { session = new Recognition(); }
        catch { block(root.dataset.unavailable); return; }
        recognition = session;
        session.lang = root.dataset.language || document.documentElement.lang;
        session.continuous = true;
        session.interimResults = true;
        session.onresult = event => {
            if (disposed || recognition !== session || !target.isConnected || target.disabled) return;
            lastResult = event.results.length - 1;
            lastResults = Array.from(event.results, result => ({ text: result[0].transcript, final: result.isFinal }));
            writeResults();
        };
        session.onerror = event => {
            if (disposed || recognition !== session) return;
            const reason = {
                'not-allowed': root.dataset.denied,
                'service-not-allowed': root.dataset.serviceDenied,
                'audio-capture': root.dataset.noMicrophone,
                'language-not-supported': root.dataset.languageUnsupported
            }[event.error];
            if (reason) block(reason);
            else status.textContent = root.dataset.error;
        };
        session.onend = () => {
            if (recognition !== session) return;
            writeResults(true);
            recognition = null;
            reset();
        };
        toggle.title = root.dataset.stop;
        toggle.setAttribute('aria-label', root.dataset.stop);
        toggle.setAttribute('aria-pressed', 'true');
        status.textContent = '';
        target.focus();
        try { session.start(); }
        catch (error) {
            if (error.name === 'NotAllowedError' || error.name === 'SecurityError') block(root.dataset.denied);
            else if (error.name === 'NotSupportedError') block(root.dataset.unavailable);
            else { cancel(); status.textContent = root.dataset.error; }
        }
    };
    return { refresh, dispose() {
        disposed = true;
        if (permission) permission.onchange = null;
        observer.disconnect();
        document.removeEventListener('visibilitychange', onVisibility);
        target?.removeEventListener('input', onInput);
        target?.removeEventListener('keydown', onKeyDown, true);
        scope?.removeEventListener('click', onClick, true);
        cancel();
        toggle.onclick = null;
    } };
}
