using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DesktopPet;

// Own TCP sockets only: no HTTP.sys URL reservations or firewall changes.
internal sealed class MonitorHttpServer : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly SemaphoreSlim _capacity = new(16);
    private readonly Func<string, string, string, (int Status, string Type, byte[] Body)> _response;
    public MonitorHttpServer(int port, bool lan, bool ipv6, Func<string, string, string, (int, string, byte[])> response)
    {
        _response = response;
        try
        {
            Add(lan ? IPAddress.Any : IPAddress.Loopback, port);
            if (ipv6) Add(lan ? IPAddress.IPv6Any : IPAddress.IPv6Loopback, port);
        }
        catch { Dispose(); throw; }
    }
    private void Add(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        if (address.AddressFamily == AddressFamily.InterNetworkV6) listener.Server.DualMode = false;
        listener.Start(16); _listeners.Add(listener); _ = Task.Run(() => Accept(listener));
    }
    private async Task Accept(TcpListener listener)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_lifetime.Token);
                if (!_capacity.Wait(0)) { client.Dispose(); continue; }
                _ = Serve(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var stream = client.GetStream();
                var header = new byte[8192]; var count = 0;
                while (count < header.Length)
                {
                    if (await stream.ReadAsync(header.AsMemory(count, 1), deadline.Token) == 0) return;
                    count++;
                    if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                }
                var line = Encoding.ASCII.GetString(header, 0, count).Split("\r\n")[0]; var fields = line.Split(' ');
                (int Status, string Type, byte[] Body) response;
                if (count == header.Length || fields.Length != 3 || fields[2] is not ("HTTP/1.1" or "HTTP/1.0") ||
                    !fields[1].StartsWith('/') || !Uri.TryCreate("http://localhost" + fields[1], UriKind.Absolute, out var uri)) response = (400, "text/plain", []);
                else response = _response(uri.AbsolutePath, uri.Query, fields[0]);
                var message = response.Status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Bad Request" };
                var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} {message}\r\nContent-Type: {response.Type}; charset=utf-8\r\nContent-Length: {response.Body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'\r\n\r\n");
                await stream.WriteAsync(bytes, deadline.Token); await stream.WriteAsync(response.Body, deadline.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or UriFormatException) { }
            finally { _capacity.Release(); }
        }
    }
    public void Dispose()
    { if (_lifetime.IsCancellationRequested) return; _lifetime.Cancel(); foreach (var listener in _listeners) listener.Stop(); }
}
