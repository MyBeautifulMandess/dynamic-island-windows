using System.Buffers.Binary;

namespace DynamicIsland.Services;

/// <summary>
/// Один канал до телефона. Транспорт не важен: WebSocket по Wi-Fi и RFCOMM по
/// Bluetooth передают одинаковые текстовые и бинарные кадры, поэтому фичевые
/// сервисы работают с каналом, а не с конкретным сокетом.
/// </summary>
internal interface IPhoneChannel : IDisposable
{
    string Remote { get; }

    /// <summary>Имя транспорта для интерфейса: «Wi-Fi» или «Bluetooth».</summary>
    string Transport { get; }

    string? DeviceId { get; set; }

    /// <summary>
    /// MAC-адрес телефона, если транспорт его знает. Единственный признак,
    /// который не меняется при переустановке приложения: по нему компьютер
    /// сливает копии одного устройства, а не заводит новую запись.
    /// </summary>
    string HardwareAddress { get; }

    DateTime LastPongUtc { get; set; }
    bool IsAuthenticated { get; }

    event Action<IPhoneChannel, string>? TextReceived;
    event Action<IPhoneChannel, byte[]>? BinaryReceived;
    event Action<IPhoneChannel>? Closed;

    Task SendTextAsync(string text);
    Task SendBinaryAsync(byte[] payload);
    Task SendTextAndBinaryAsync(string descriptor, byte[] payload);
    Task SendPingAsync();
    void Close();
}

/// <summary>
/// Кадр Bluetooth-канала: вид (1 — текст, 2 — данные), длина и тело.
/// В WebSocket эта информация лежит в заголовке, у RFCOMM его нет, поэтому
/// кадр собирается здесь вручную.
/// </summary>
internal static class PhoneFrames
{
    public const byte Text = 1;
    public const byte Binary = 2;
    public const int HeaderSize = 5;
    public const int MaxPayload = 1024 * 1024;

    public static byte[] Encode(byte kind, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = kind;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, HeaderSize - 1), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }
}
