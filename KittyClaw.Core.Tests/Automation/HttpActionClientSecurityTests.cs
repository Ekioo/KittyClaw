using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace KittyClaw.Core.Tests.Automation;

public sealed class HttpActionClientSecurityTests
{
    public HttpActionClientSecurityTests() =>
        RuntimeHelpers.RunClassConstructor(typeof(HttpActionClient).TypeHandle);

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", false)]
    [InlineData("[::1]", false)]
    [InlineData("[::ffff:127.0.0.1]", false)]
    public async Task Guarded_requests_do_not_delegate_local_destinations_to_the_default_proxy(string host, bool https)
    {
        await using var destination = new Server();
        await using var proxyServer = new Server();
        var proxy = new TrackingProxy(proxyServer.Address);
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = proxy;
        try
        {
            using var client = Create(guard: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var scheme = https ? "https" : "http";
            var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var response = await client.GetAsync($"{scheme}://{host}:{destination.Address.Port}/change", timeout.Token);
            });
            Assert.Contains("blocked", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, proxy.Lookups);
            Assert.Equal(0, proxyServer.Requests);
            Assert.Equal(0, destination.Requests);
        }
        finally { HttpClient.DefaultProxy = previous; }
    }

    [Fact]
    public async Task Explicit_local_target_opt_in_retains_default_proxy_routing()
    {
        await using var proxyServer = new Server();
        var proxy = new TrackingProxy(proxyServer.Address);
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = proxy;
        try
        {
            using var client = Create(guard: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.PostAsync("http://webhook.invalid/notify", new StringContent("fixture"), timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(proxy.Lookups > 0);
            Assert.Equal(1, proxyServer.Requests);
            Assert.Equal("POST http://webhook.invalid/notify HTTP/1.1", proxyServer.RequestLine);
        }
        finally { HttpClient.DefaultProxy = previous; }
    }

    [Fact]
    public async Task Explicit_local_target_opt_in_can_still_connect_directly()
    {
        await using var destination = new Server();
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();
        try
        {
            using var client = Create(guard: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(destination.Address, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, destination.Requests);
        }
        finally { HttpClient.DefaultProxy = previous; }
    }

    [Fact]
    public async Task Local_target_opt_in_does_not_follow_redirects()
    {
        await using var destination = new Server();
        await using var redirect = new Server($"HTTP/1.1 302 Found\r\nLocation: {destination.Address}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new WebProxy();
        try
        {
            using var client = Create(guard: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync(redirect.Address, timeout.Token);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(0, destination.Requests);
        }
        finally { HttpClient.DefaultProxy = previous; }
    }

    // Fresh production clients isolate each test's process-local default proxy and connection pool.
    private static HttpClient Create(bool guard) => (HttpClient)typeof(HttpActionClient)
        .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [guard])!;

    private sealed class TrackingProxy(Uri address) : IWebProxy
    {
        private int _lookups;
        public int Lookups => Volatile.Read(ref _lookups);
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) { Interlocked.Increment(ref _lookups); return address; }
        public bool IsBypassed(Uri host) { Interlocked.Increment(ref _lookups); return false; }
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public string? RequestLine { get; private set; }
        public Uri Address { get; }

        public Server(string response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")
        {
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        using var peer = await _listener.AcceptTcpClientAsync(_stop.Token);
                        var stream = peer.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                        RequestLine = await reader.ReadLineAsync(_stop.Token);
                        for (var i = 0; i < 100; i++)
                        {
                            var line = await reader.ReadLineAsync(_stop.Token);
                            if (string.IsNullOrEmpty(line)) break;
                        }
                        // Request content is irrelevant to these fixtures; no forwarding is performed.
                        Interlocked.Increment(ref _requests);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }
}
