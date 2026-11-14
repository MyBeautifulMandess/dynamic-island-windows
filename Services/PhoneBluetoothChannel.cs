using System.Buffers.Binary;
using System.IO;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>
/// Одно соединение Bluetooth (RFCOMM). Поток WinRT оборачивается в обычный
/// <see cref="Stream"/>: так чтение и запись идут теми же вызовами, что и у
/// WebSocket, и разбор кадра живёт в одном месте.
/// </summary>
internal sealed class PhoneBluetoothConnection : IPhoneChannel
{
    private const int MaxHeaderBytes = 5;
    private const int MaxPayload = PhoneFrames.MaxPayload;
    private const int MaxTextBytes = 256 * 1024;
    private const int ReceiveBuffer = 64 * 1024;

    private readonly StreamSocket _socket;
    private readonly Stream _read;
    private readonly Stream _write;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _closed;

    public PhoneBluetoothConnection(StreamSocket socket, string remote, string hardwareAddress)
    {
        _socket = socket;
        _read = socket.InputStream.AsStreamForRead(ReceiveBuffer);
        _write = socket.OutputStream.AsStreamForWrite(ReceiveBuffer);
        Remote = remote;
        HardwareAddress = hardwareAddress;
    }

    public string Remote { get; }
    public string Transport => "Bluetooth";

    /// <summary>MAC телефона: по нему компьютер узнаёт устройство даже после переустановки приложения.</summary>
    public string HardwareAddress { get; }

    public string? DeviceId { get; set; }
    public DateTime LastPongUtc { get; set; } = DateTime.UtcNow;
    public bool IsAuthenticated => DeviceId is not null;

    public event Action<IPhoneChannel, string>? TextReceived;
    public event Action<IPhoneChannel, byte[]>? BinaryReceived;
    public event Action<IPhoneChannel>? Closed;

    public async Task ReadLoopAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _cts.Token);
        var ct = linked.Token;
        var header = new byte[MaxHeaderBytes];
        // Файл идёт чанками по 64 КБ; буфер один и тот же, чтобы не мусорить в куче.
        var payload = new byte[MaxPayload];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(header, ct)) break;
                var kind = header[0];
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
                if (length < 0 || length > MaxPayload)
                {
                    Diagnostics.Log($"Bluetooth: кадр {length} байт отклонён");
                    break;
                }
                if (length > 0 && !await ReadExactAsync(payload.AsMemory(0, length), ct)) break;

                switch (kind)
                {
                    case PhoneFrames.Text:
                        Deliver(kind, payload.AsSpan(0, length).ToArray());
                        break;
                    case PhoneFrames.Binary:
                        Deliver(kind, length == MaxPayload ? payload.ToArray() : payload.AsSpan(0, length).ToArray());
                        break;
                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Diagnostics.Log("Bluetooth: чтение прервалось — " + ex.Message); }
        finally
        {
            _closed = true;
            try { Closed?.Invoke(this); } catch { }
        }
    }

    private void Deliver(byte kind, byte[] data)
    {
        try
        {
            if (kind == PhoneFrames.Text)
            {
                if (data.Length > MaxTextBytes) return;
                TextReceived?.Invoke(this, System.Text.Encoding.UTF8.GetString(data));
            }
            else
            {
                BinaryReceived?.Invoke(this, data);
            }
        }
        catch (Exception ex) { Diagnostics.Log("Bluetooth: обработка кадра не удалась — " + ex.Message); }
    }

    public Task SendTextAsync(string text) =>
        SendFrameAsync(PhoneFrames.Text, System.Text.Encoding.UTF8.GetBytes(text));

    public Task SendBinaryAsync(byte[] payload) => SendFrameAsync(PhoneFrames.Binary, payload);

    /// <summary>Описание чанка и сам чанк одним захватом блокировки — как в WebSocket-канале.</summary>
    public async Task SendTextAndBinaryAsync(string descriptor, byte[] payload)
    {
        if (_closed) return;
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _write.WriteAsync(PhoneFrames.Encode(PhoneFrames.Text, System.Text.Encoding.UTF8.GetBytes(descriptor)), _cts.Token);
            await _write.WriteAsync(PhoneFrames.Encode(PhoneFrames.Binary, payload), _cts.Token);
            await _write.FlushAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            if (ex is not (IOException or ObjectDisposedException or OperationCanceledException))
                Diagnostics.Log("Bluetooth: отправка не удалась — " + ex.Message);
            _closed = true;
        }
        finally { _sendLock.Release(); }
    }

    /// <summary>В RFCOMM нет служебных ping-кадров, поэтому живость проверяется сообщением протокола.</summary>
    public Task SendPingAsync() =>
        SendTextAsync(PhoneProtocol.Build("ping", new { ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));

    private async Task SendFrameAsync(byte kind, byte[] payload)
    {
        if (_closed) return;
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _write.WriteAsync(PhoneFrames.Encode(kind, payload), _cts.Token);
            await _write.FlushAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            if (ex is not (IOException or ObjectDisposedException or OperationCanceledException))
                Diagnostics.Log("Bluetooth: отправка не удалась — " + ex.Message);
            _closed = true;
        }
        finally { _sendLock.Release(); }
    }

    private async Task<bool> ReadExactAsync(Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _read.ReadAsync(buffer[offset..], token);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public void Close()
    {
        try { _cts.Cancel(); } catch { }
        try { _socket.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _closed = true;
        try { _cts.Cancel(); } catch { }
        try { _read.Dispose(); } catch { }
        try { _write.Dispose(); } catch { }
        try { _socket.Dispose(); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
