namespace ScaleCore;

public interface IWeightProtocolAdapter
{
    string ServiceUuid { get; }
    string ProtocolName { get; }
    bool TryDecode(string address, ReadOnlySpan<byte> payload, DateTimeOffset seenAt, out WeightReading? reading);
}

public sealed class XiaomiMiScaleAdapter : IWeightProtocolAdapter
{
    public string ServiceUuid => ScaleProtocolDecoder.XiaomiWeightService;
    public string ProtocolName => "Xiaomi Mi Scale (181D)";

    public bool TryDecode(string address, ReadOnlySpan<byte> payload, DateTimeOffset seenAt,
        out WeightReading? reading)
    {
        reading = null;
        if (payload.Length != 10) return false;

        var control = payload[0];
        var raw = (ushort)(payload[1] | (payload[2] << 8));
        // The low control bits identify units; bit 5 means stable and bit 7
        // marks a control frame without a current weight (e.g. 0x82 / 0xA2).
        // In particular, 0x02 is a live, unstable kilogram reading.
        var unit = control & 0x1F;
        var kilograms = unit switch
        {
            0x02 => raw * 0.005, // kg
            0x12 => raw * 0.006, // jin
            0x03 or 0x13 => raw * 0.0045359237, // lb (0x13 is the no-weight control variant)
            _ => double.NaN
        };
        if (double.IsNaN(kilograms)) return false;

        var hasWeight = (control & 0x80) == 0;
        var stable = (control & 0x20) != 0;
        // The 181D service has no dedicated load-removed flag. Only treat a
        // near-zero *weight-bearing* frame as removal; 0xA2 is a control frame.
        var removed = hasWeight && kilograms < 5;
        if (hasWeight && kilograms > 300) return false;
        reading = new WeightReading(address, kilograms, stable, removed, ProtocolName, seenAt, control, hasWeight);
        return true;
    }
}

public sealed class XiaomiBodyCompositionAdapter : IWeightProtocolAdapter
{
    public string ServiceUuid => ScaleProtocolDecoder.XiaomiBodyCompositionService;
    public string ProtocolName => "Xiaomi Body Composition (181B)";

    public bool TryDecode(string address, ReadOnlySpan<byte> payload, DateTimeOffset seenAt,
        out WeightReading? reading)
    {
        reading = null;
        if (payload.Length != 13 || payload[0] is not (0x02 or 0x03)) return false;

        var flags = payload[1];
        var stable = (flags & 0x20) != 0;
        var removed = (flags & 0x80) != 0;
        var raw = (ushort)(payload[11] | (payload[12] << 8));
        var kilograms = raw / 200.0;
        if (payload[0] == 0x03) kilograms *= 0.45359237;
        if (!removed && (kilograms < 1 || kilograms > 300)) return false;

        reading = new WeightReading(address, kilograms, stable, removed, ProtocolName, seenAt, flags);
        return true;
    }
}

public static class ScaleProtocolDecoder
{
    private const byte ServiceData16BitUuidAdType = 0x16;
    public const string XiaomiWeightService = "181D";
    public const string XiaomiBodyCompositionService = "181B";
    public const string BluetoothWeightScaleGatt = "181D-GATT";
    public const string BluetoothWeightScaleGattName = "Bluetooth Weight Scale (GATT)";
    public static IReadOnlyList<IWeightProtocolAdapter> Adapters { get; } =
    [
        new XiaomiMiScaleAdapter(),
        new XiaomiBodyCompositionAdapter()
    ];

    public static string? FindServiceUuid(IEnumerable<Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementDataSection> sections,
        out byte[] payload)
    {
        payload = [];
        foreach (var section in sections)
        {
            // The Windows WinRT enum omits AD type 0x16 on some target SDKs.
            if (section.DataType != ServiceData16BitUuidAdType)
                continue;

            var reader = Windows.Storage.Streams.DataReader.FromBuffer(section.Data);
            if (reader.UnconsumedBufferLength < 3) continue;
            var data = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(data);
            var service = (ushort)(data[0] | (data[1] << 8));
            var uuid = service.ToString("X4");
            if (!Adapters.Any(adapter => adapter.ServiceUuid == uuid)) continue;
            var servicePayload = data[2..];
            var expectedLength = uuid == XiaomiWeightService ? 10 : 13;
            if (servicePayload.Length != expectedLength) continue;
            payload = servicePayload;
            return uuid;
        }
        return null;
    }

    public static bool IsKnownScale(string? serviceUuid, string name) =>
        serviceUuid == BluetoothWeightScaleGatt ||
        serviceUuid is not null && Adapters.Any(adapter => adapter.ServiceUuid == serviceUuid) ||
        name.Contains("MI SCALE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("MIBFS", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("XIAOMI", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("WEIGHT", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SCALE", StringComparison.OrdinalIgnoreCase);

    public static string ProtocolName(string serviceUuid) => serviceUuid == BluetoothWeightScaleGatt
        ? BluetoothWeightScaleGattName
        : Adapters.FirstOrDefault(adapter => adapter.ServiceUuid == serviceUuid)?.ProtocolName ?? "Неизвестный протокол";

    public static bool TryDecode(string address, string serviceUuid, ReadOnlySpan<byte> payload,
        DateTimeOffset seenAt, out WeightReading? reading)
    {
        reading = null;
        var adapter = Adapters.FirstOrDefault(item => item.ServiceUuid == serviceUuid);
        return adapter is not null && adapter.TryDecode(address, payload, seenAt, out reading);
    }
}
