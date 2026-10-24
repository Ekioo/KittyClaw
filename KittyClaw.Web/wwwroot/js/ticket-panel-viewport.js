export function observe(reference) {
    const query = window.matchMedia('(max-width: 767px)');
    let disposed = false;
    const update = () => {
        if (!disposed) reference.invokeMethodAsync('SetMobile', query.matches).catch(() => {});
    };
    query.addEventListener('change', update);
    update();
    return {
        dispose() {
            disposed = true;
            query.removeEventListener('change', update);
        }
    };
}
