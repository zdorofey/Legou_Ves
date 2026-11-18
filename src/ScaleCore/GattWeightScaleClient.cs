using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ScaleCore;

/// <summary>Client for scales that expose the Bluetooth SIG Weight Scale Service.</summary>
public sealed class GattWeightScaleClient : IDisposable
{
    private static readonly Guid WeightScaleService = Guid.Parse("0000181D-0000-1000-8000-00805F9B34FB");
    private static readonly Guid WeightMeasurementCharacteristic = Guid.Parse("00002A9D-0000-1000-8000-00805F9B34FB");
    private static readonly Guid WeightScaleFeatureCharacteristic = Guid.Parse("00002A9E-0000-1000-8000-00805F9B34FB");
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _measurement;
    private double _kilogramResolution = 0.001;
    private double _poundResolution = 0.01;
    private string _address = string.Empty;

    public event Action<object?, WeightReading>? ReadingReceived;
    public event Action<object?, string>? ClientError;
    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected && _measurement is not null;

    public async Task StartAsync(string address)
    {
        Stop();
        _address = address;
        try
        {
            var numericAddress = Convert.ToUInt64(address.Replace(":", "", StringComparison.Ordinal), 16);
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(numericAddress);
            if (_device is null)
            {
                ClientError?.Invoke(this, "Не удалось открыть Bluetooth-устройство. Проверьте, что весы рядом.");
                return;
            }

            var serviceResult = await _device.GetGattServicesForUuidAsync(WeightScaleService, BluetoothCacheMode.Uncached);
            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                ClientError?.Invoke(this, "Весы не предоставляют стандартный Weight Scale Service.");
                return;
            }

            var service = serviceResult.Services[0];
            var featureResult = await service.GetCharacteristicsForUuidAsync(WeightScaleFeatureCharacteristic,
                BluetoothCacheMode.Uncached);
            if (featureResult.Status == GattCommunicationStatus.Success && featureResult.Characteristics.Count > 0)
                await ReadResolutionAsync(featureResult.Characteristics[0]);

            var measurementResult = await service.GetCharacteristicsForUuidAsync(WeightMeasurementCharacteristic,
                BluetoothCacheMode.Uncached);
            if (measurementResult.Status != GattCommunicationStatus.Success || measurementResult.Characteristics.Count == 0)
            {
                ClientError?.Invoke(this, "В профиле весов нет характеристики Weight Measurement (2A9D).");
                return;
            }

            _measurement = measurementResult.Characteristics[0];
            _measurement.ValueChanged += Measurement_ValueChanged;
            var status = await _measurement.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate);
            if (status != GattCommunicationStatus.Success)
            {
                _measurement.ValueChanged -= Measurement_ValueChanged;
                _measurement = null;
                ClientError?.Invoke(this, $"Не удалось включить передачу измерений: {status}.");
            }
        }
        catch (Exception ex)
        {
            ClientError?.Invoke(this, $"Bluetooth GATT: {ex.Message}");
        }
    }

    private async Task ReadResolutionAsync(GattCharacteristic featureCharacteristic)
    {
        try
        {
            var result = await featureCharacteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status != GattCommunicationStatus.Success || result.Value is null) return;
            var reader = DataReader.FromBuffer(result.Value);
            if (reader.UnconsumedBufferLength < 4) return;
            var feature = reader.ReadUInt32();
            var resolutionCode = (int)((feature >> 3) & 0x0F);
            (_kilogramResolution, _poundResolution) = resolutionCode switch
            {
                1 => (0.5, 1.0),
                2 => (0.2, 0.5),
                3 => (0.1, 0.2),
                4 => (0.05, 0.1),
                5 => (0.02, 0.05),
                6 => (0.01, 0.02),
                7 => (0.005, 0.01),
                _ => (0.001, 0.01)
            };
        }
        catch
        {
            // Keep the standard fallback resolution if the optional feature read is unavailable.
        }
    }

    private void Measurement_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            if (reader.UnconsumedBufferLength < 3) return;
            var flags = reader.ReadByte();
            var raw = reader.ReadUInt16();
            if (raw == ushort.MaxValue) return;

            var imperial = (flags & 0x01) != 0;
            var value = raw * (imperial ? _poundResolution : _kilogramResolution);
            var kilograms = imperial ? value * 0.45359237 : value;
            if (kilograms < 1 || kilograms > 300) return;

            ReadingReceived?.Invoke(this, new WeightReading(_address, kilograms, true, false,
                ScaleProtocolDecoder.BluetoothWeightScaleGattName, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            ClientError?.Invoke(this, $"Не удалось разобрать измерение: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_measurement is not null)
        {
            try { _measurement.ValueChanged -= Measurement_ValueChanged; } catch { }
            _measurement = null;
        }
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    public void Dispose() => Stop();
}
