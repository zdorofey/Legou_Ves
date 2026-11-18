using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace ScaleCore;

public sealed class BleScaleScanner : IDisposable
{
    private static readonly Guid BluetoothWeightScaleServiceGuid =
        Guid.Parse("0000181D-0000-1000-8000-00805F9B34FB");
    private BluetoothLEAdvertisementWatcher? _watcher;
    public event Action<object?, ScaleAdvertisement>? ScaleSeen;
    public event Action<object?, WeightReading>? ReadingReceived;
    public event Action<object?, string>? ScannerError;

    public void Start()
    {
        if (_watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started) return;
        Stop();
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };
            _watcher.Received += OnAdvertisementReceived;
            _watcher.Stopped += OnStopped;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            ScannerError?.Invoke(this, ex.Message);
            Stop();
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher watcher,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName ?? string.Empty;
        var address = FormatAddress(args.BluetoothAddress);
        var timestamp = DateTimeOffset.Now;
        var uuid = ScaleProtocolDecoder.FindServiceUuid(args.Advertisement.DataSections, out var payload);
        if (uuid is null && args.Advertisement.ServiceUuids.Contains(BluetoothWeightScaleServiceGuid))
            uuid = ScaleProtocolDecoder.BluetoothWeightScaleGatt;

        if (!ScaleProtocolDecoder.IsKnownScale(uuid, name)) return;
        var protocol = uuid is null ? "Неизвестный протокол" : ScaleProtocolDecoder.ProtocolName(uuid);
        ScaleSeen?.Invoke(this, new ScaleAdvertisement(address, name, args.RawSignalStrengthInDBm,
            uuid ?? "", protocol, payload, timestamp, SummarizeAdvertisement(args.Advertisement)));

        if (uuid is not null && ScaleProtocolDecoder.TryDecode(address, uuid, payload, timestamp, out var reading)
            && reading is not null)
            ReadingReceived?.Invoke(this, reading);
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher watcher,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
            ScannerError?.Invoke(this, $"Bluetooth: {args.Error}");
    }

    public void Stop()
    {
        if (_watcher is null) return;
        try
        {
            _watcher.Received -= OnAdvertisementReceived;
            _watcher.Stopped -= OnStopped;
            if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started) _watcher.Stop();
        }
        catch { }
        _watcher = null;
    }

    public static string FormatAddress(ulong value)
    {
        var hex = value.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static string SummarizeAdvertisement(BluetoothLEAdvertisement advertisement)
    {
        static string ReadBuffer(IBuffer buffer)
        {
            var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(bytes);
            return Convert.ToHexString(bytes);
        }

        var sections = string.Join(" ", advertisement.DataSections.Select(section =>
            $"AD{section.DataType:X2}:{ReadBuffer(section.Data)}"));
        var uuids = string.Join(",", advertisement.ServiceUuids.Select(uuid => uuid.ToString()));
        var manufacturers = string.Join(" ", advertisement.ManufacturerData.Select(item =>
            $"MFG{item.CompanyId:X4}:{ReadBuffer(item.Data)}"));
        return string.Join(" | ", new[] { sections, uuids.Length > 0 ? $"UUID:{uuids}" : "", manufacturers }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public void Dispose() => Stop();
}
