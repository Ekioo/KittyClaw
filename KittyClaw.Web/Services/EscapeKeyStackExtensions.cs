using Microsoft.JSInterop;

namespace KittyClaw.Web.Services;

public static class EscapeKeyStackExtensions
{
    public static IDisposable PushWithFocus(this EscapeKeyStack stack, IJSRuntime js, Action onEscape)
    {
        try { _ = js.InvokeVoidAsync("escapeStack.pushFocus"); } catch { /* circuit gone */ }
        var inner = stack.Push(onEscape);
        return new FocusToken(inner, js);
    }

    private sealed class FocusToken : IDisposable
    {
        private readonly IDisposable _inner;
        private readonly IJSRuntime _js;
        private bool _disposed;

        public FocusToken(IDisposable inner, IJSRuntime js)
        {
            _inner = inner;
            _js = js;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inner.Dispose();
            try { _ = _js.InvokeVoidAsync("escapeStack.popFocus"); } catch { /* circuit gone */ }
        }
    }
}
