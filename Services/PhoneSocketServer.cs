using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DynamicIsland.Services;

/// <summary>
/// Минимальный сервер WebSocket (RFC 6455) поверх <see cref="TcpListener"/>.
///
/// <see cref="HttpListener"/> не подходит: без прав администратора он не даёт
/// зарегистрировать префикс с адресом локальной сети (не только «+», но и
/// конкретный IP), поэтому телефон никогда бы не подключился. TcpListener на
/// порт выше 1024 в правах не нуждается, а BCL-реализации WebSocket-сервера нет —
/// поэтому кадры разбираются здесь, без новых зависимостей.
/// </summary>
internal sealed class PhoneSocketServer : IDisposable
{
    private const int MaxHandshakeBytes = 8 * 1024;
    private const int MaxFrameBytes = 1024 * 1024;
    private const int MaxTextBytes = 256 * 1024;
    private static readonly byte[] AcceptGuid = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

    private readonly string _path;
    private int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public event Action<PhoneSocketConnection>? Connected;

    public PhoneSocketServer(string path = "/di/")
    {
        _path = path;
    }

    public bool IsRunning => _listener is not null;

    public void Start(int port)
    {
        if (_disposed || _listener is not null) return;
        _port = port;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(listener, _cts.Token);
        Diagnostics.Log($"PhoneSocketServer: слушаю порт {_port}");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                Diagnostics.Log("PhoneSocketServer: accept failed — " + ex.Message);
                return;
            }
            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        PhoneSocketConnection? connection = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            var request = await ReadHandshakeAsync(stream, token);
            if (request is null)
            {
                await WriteHttpAsync(stream, "400 Bad Request", "Bad Request");
                return;
            }

            if (!request.IsUpgrade)
            {
                // Страница сопряжения: камера открывает её в браузере, а она
                // уже запускает приложение по схеме dilink://.
                if (string.Equals(request.Path, PhonePairingPage.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var values = ParseQuery(request.Query);
                    var ip = values.GetValueOrDefault("ip") ?? "";
                    var port = int.TryParse(values.GetValueOrDefault("port"), out var parsedPort) ? parsedPort : 0;
                    var pairToken = values.GetValueOrDefault("token") ?? "";
                    var btAddress = values.GetValueOrDefault("bta") ?? "";
                    if (ip.Length == 0 || port is <= 0 or > 65535 || pairToken.Length == 0)
                    {
                        await WriteHttpAsync(stream, "400 Bad Request", "Invalid pairing data", "text/plain; charset=utf-8");
                        return;
                    }
                    await WriteHttpAsync(stream, "200 OK",
                        PhonePairingPage.BuildHtml(ip, port, pairToken, btAddress), "text/html; charset=utf-8");
                    return;
                }

                // Обычный GET: короткий ответ для диагностики «сервер жив».
                await WriteHttpAsync(stream, "200 OK",
                    "{\"app\":\"Dynamic Island\",\"link\":\"dilink/1\",\"ws\":\"" + _path + "\"}");
                return;
            }

            if (!string.Equals(request.Path, _path, StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpAsync(stream, "404 Not Found", "Not Found");
                return;
            }

            if (string.IsNullOrEmpty(request.Key))
            {
                await WriteHttpAsync(stream, "400 Bad Request", "Missing Sec-WebSocket-Key");
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData([.. Encoding.ASCII.GetBytes(request.Key), .. AcceptGuid]));
            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            var responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, token);
            await stream.FlushAsync(token);

            connection = new PhoneSocketConnection(client, stream, remote);
            Diagnostics.Log($"PhoneSocketServer: handshake ok — {remote}");
            Connected?.Invoke(connection);
            await connection.ReadLoopAsync(token);
        }
        catch (Exception ex)
        {
            if (ex is not (OperationCanceledException or IOException or SocketException or ObjectDisposedException))
                Diagnostics.Log("PhoneSocketServer: client failed — " + ex.Message);
        }
        finally
        {
            // Канал сам сообщает о закрытии событием Closed: второй сигнал
            // о том же соединении только задвоил бы изменения в интерфейсе.
            connection?.Dispose();
            try { client.Dispose(); } catch { }
        }
    }

    private static async Task WriteHttpAsync(NetworkStream stream, string status, string body, string contentType = "application/json; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
                   $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (key.Length > 0) values[key] = Uri.UnescapeDataString(value);
        }
        return values;
    }

    private static async Task<Handshake?> ReadHandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxHandshakeBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), token);
            if (read <= 0) return null;
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;
            var lines = text[..end].Split("\r\n");
            if (lines.Length == 0) return null;
            var parts = lines[0].Split(' ');
            var target = parts.Length > 1 ? parts[1] : "/";
            var queryAt = target.IndexOf('?');
            var path = queryAt >= 0 ? target[..queryAt] : target;
            var query = queryAt >= 0 ? target[(queryAt + 1)..] : "";
            var upgrade = false;
            string? key = null;
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                var name = lines[i][..colon].Trim();
                var value = lines[i][(colon + 1)..].Trim();
                if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) &&
                    value.Contains("websocket", StringComparison.OrdinalIgnoreCase)) upgrade = true;
                if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            }
            return new Handshake(path, query, upgrade, key);
        }
        return null;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        Diagnostics.Log("PhoneSocketServer: остановлен");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private sealed record Handshake(string Path, string Query, bool IsUpgrade, string? Key);
}

/// <summary>Одно принятое TCP-соединение с телефоном. Кадры — текстовые (JSON) и бинарные (файлы).</summary>
internal sealed class PhoneSocketConnection : IPhoneChannel
{
    private const int MaxFrameBytes = 1024 * 1024;
    private const int MaxTextBytes = 256 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte> _fragments = [];
    private int _fragmentOpcode;
    private bool _closed;

    public PhoneSocketConnection(TcpClient client, NetworkStream stream, string remote)
    {
        _client = client;
        _stream = stream;
        Remote = remote;
    }

    public string Remote { get; }
    public string Transport => "Wi-Fi";
    public string? DeviceId { get; set; }

    /// <summary>По TCP аппаратный адрес телефона неизвестен: остаётся идентификатор установки.</summary>
    public string HardwareAddress => "";

    public DateTime LastPongUtc { get; set; } = DateTime.UtcNow;
    public bool IsAuthenticated => DeviceId is not null;

    public event Action<IPhoneChannel, string>? TextReceived;
    public event Action<IPhoneChannel, byte[]>? BinaryReceived;
    public event Action<IPhoneChannel>? Closed;

    public async Task ReadLoopAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
        var ct = linked.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var header = new byte[2];
                if (!await ReadExactAsync(header, ct)) break;
                var fin = (header[0] & 0x80) != 0;
                var opcode = header[0] & 0x0F;
                var masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;

                if (length == 126)
                {
                    var extended = new byte[2];
                    if (!await ReadExactAsync(extended, ct)) break;
                    length = (extended[0] << 8) | extended[1];
                }
                else if (length == 127)
                {
                    var extended = new byte[8];
                    if (!await ReadExactAsync(extended, ct)) break;
                    length = 0;
                    for (var i = 0; i < 8; i++) length = (length << 8) | extended[i];
                }

                if (length < 0 || length > MaxFrameBytes)
                {
                    Diagnostics.Log($"PhoneSocketServer: кадр {length} байт отклонён");
                    break;
                }

                byte[]? mask = null;
                if (masked)
                {
                    mask = new byte[4];
                    if (!await ReadExactAsync(mask, ct)) break;
                }

                var payload = new byte[length];
                if (length > 0 && !await ReadExactAsync(payload, ct)) break;
                if (mask is not null)
                    for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                switch (opcode)
                {
                    case 0x0:
                        if (_fragmentOpcode == 0) break;
                        if (_fragments.Count + payload.Length > MaxTextBytes) { _fragments.Clear(); _fragmentOpcode = 0; break; }
                        _fragments.AddRange(payload);
                        if (fin)
                        {
                            var data = _fragments.ToArray();
                            var kind = _fragmentOpcode;
                            _fragments.Clear();
                            _fragmentOpcode = 0;
                            Deliver(kind, data);
                        }
                        break;
                    case 0x1:
                    case 0x2:
                        if (!fin)
                        {
                            _fragmentOpcode = opcode;
                            _fragments.Clear();
                            _fragments.AddRange(payload);
                            break;
                        }
                        Deliver(opcode, payload);
                        break;
                    case 0x8:
                        await SendFrameAsync(0x8, [], ct);
                        return;
                    case 0x9:
                        await SendFrameAsync(0xA, payload, ct);
                        break;
                    case 0xA:
                        LastPongUtc = DateTime.UtcNow;
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Diagnostics.Log("PhoneSocketServer: read failed — " + ex.Message); }
        finally
        {
            _closed = true;
            try { Closed?.Invoke(this); } catch { }
        }
    }

    private void Deliver(int opcode, byte[] payload)
    {
        try
        {
            if (opcode == 0x1) TextReceived?.Invoke(this, Encoding.UTF8.GetString(payload));
            else BinaryReceived?.Invoke(this, payload);
        }
        catch (Exception ex) { Diagnostics.Log("PhoneSocketServer: handler failed — " + ex.Message); }
    }

    public Task SendTextAsync(string text) => SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text), _cts.Token);

    public Task SendBinaryAsync(byte[] payload) => SendFrameAsync(0x2, payload, _cts.Token);

    /// <summary>
    /// Отправляет описание чанка и сам чанк одним захватом блокировки. Без этого
    /// между двумя кадрами могло вклиниться другое сообщение, и получатель
    /// сопоставил бы бинарный кадр не с тем описанием.
    /// </summary>
    public async Task SendTextAndBinaryAsync(string descriptor, byte[] payload)
    {
        if (_closed) return;
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _stream.WriteAsync(BuildFrame(0x1, Encoding.UTF8.GetBytes(descriptor)), _cts.Token);
            await _stream.WriteAsync(BuildFrame(0x2, payload), _cts.Token);
            await _stream.FlushAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            if (ex is not (IOException or ObjectDisposedException or OperationCanceledException))
                Diagnostics.Log("PhoneSocketServer: send failed — " + ex.Message);
            _closed = true;
        }
        finally { _sendLock.Release(); }
    }

    public Task SendPingAsync() => SendFrameAsync(0x9, [], _cts.Token);

    private async Task SendFrameAsync(int opcode, byte[] payload, CancellationToken token)
    {
        if (_closed) return;
        await _sendLock.WaitAsync(token);
        try
        {
            var frame = BuildFrame(opcode, payload);
            await _stream.WriteAsync(frame, token);
            await _stream.FlushAsync(token);
        }
        catch (Exception ex)
        {
            if (ex is not (IOException or ObjectDisposedException or OperationCanceledException))
                Diagnostics.Log("PhoneSocketServer: send failed — " + ex.Message);
            _closed = true;
        }
        finally { _sendLock.Release(); }
    }

    private static byte[] BuildFrame(int opcode, byte[] payload)
    {
        // Сервер не маскирует кадры (RFC 6455, 5.1).
        var length = payload.Length;
        var headerSize = length < 126 ? 2 : length <= ushort.MaxValue ? 4 : 10;
        var frame = new byte[headerSize + length];
        frame[0] = (byte)(0x80 | opcode);
        if (length < 126) frame[1] = (byte)length;
        else if (length <= ushort.MaxValue)
        {
            frame[1] = 126;
            frame[2] = (byte)(length >> 8);
            frame[3] = (byte)length;
        }
        else
        {
            frame[1] = 127;
            for (var i = 0; i < 8; i++) frame[2 + i] = (byte)((long)length >> (56 - i * 8));
        }
        Buffer.BlockCopy(payload, 0, frame, headerSize, length);
        return frame;
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public void Close()
    {
        try { _cts.Cancel(); } catch { }
        try { _client.Client.Shutdown(SocketShutdown.Both); } catch { }
    }

    public void Dispose()
    {
        _closed = true;
        try { _cts.Cancel(); } catch { }
        try { _client.Dispose(); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
