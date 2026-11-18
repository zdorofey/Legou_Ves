using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ScaleCore;

namespace ScaleSetup;

public partial class MainWindow : Window
{
    private readonly BleScaleScanner _scanner = new();
    private readonly ObservableCollection<ScaleCandidate> _candidates = [];
    private ScaleConfig? _savedConfig = ScaleConfigStore.Load();
    private readonly Queue<string> _recentReadings = new();
    private long _lastReadingTimestamp;
    private int _readingCount;

    public MainWindow()
    {
        InitializeComponent();
        ScaleList.ItemsSource = _candidates;
        _scanner.ScaleSeen += Scanner_ScaleSeen;
        _scanner.ReadingReceived += Scanner_ReadingReceived;
        _scanner.ScannerError += Scanner_ScannerError;
        Loaded += (_, _) =>
        {
            SavedText.Text = _savedConfig is null
                ? "Пока не выбраны"
                : $"Сохранено: {_savedConfig.Name} · {_savedConfig.Address}";
        };
        Closed += (_, _) => _scanner.Dispose();
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Ищу весы поблизости. Встаньте на весы, чтобы они передали пакет.";
        _scanner.Start();
    }

    private void Scanner_ScaleSeen(object? sender, ScaleAdvertisement advertisement)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var existing = _candidates.FirstOrDefault(item =>
                string.Equals(item.Address, advertisement.Address, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new ScaleCandidate(advertisement);
                _candidates.Add(existing);
            }
            else
            {
                existing.Update(advertisement);
            }
            if (ScaleList.SelectedItem is null && _savedConfig is not null &&
                string.Equals(existing.Address, _savedConfig.Address, StringComparison.OrdinalIgnoreCase))
                ScaleList.SelectedItem = existing;
            if (ScaleList.SelectedItem is ScaleCandidate selected &&
                string.Equals(selected.Address, existing.Address, StringComparison.OrdinalIgnoreCase))
                AdvertisementHistoryText.Text = existing.AdvertisementHistoryText;
            RefreshSaveAvailability();
            if (!StatusText.Text.StartsWith("Привязка готова", StringComparison.Ordinal) &&
                !StatusText.Text.StartsWith("Сохранено:", StringComparison.Ordinal))
                StatusText.Text = $"Найдено устройств: {_candidates.Count}. Выберите нужные весы.";
        }));
    }

    private void Scanner_ReadingReceived(object? sender, WeightReading reading)
    {
        var timestamp = Stopwatch.GetTimestamp();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _readingCount++;
            var interval = _lastReadingTimestamp == 0
                ? (double?)null
                : Stopwatch.GetElapsedTime(_lastReadingTimestamp, timestamp).TotalMilliseconds;
            _lastReadingTimestamp = timestamp;
            var state = !reading.HasWeight ? "контрольный пакет без нового веса" : reading.IsRemoved ? "весы пусты" : reading.IsStable ? "устойчивое значение" : "измеряется";
            LatestReadingText.Text = reading.HasWeight
                ? $"Последнее показание: {reading.WeightKg:N2} кг · {state} · {reading.Address}"
                : $"Последний пакет: {state} · {reading.Address}";

            var mark = !reading.HasWeight ? "ctrl" : reading.IsRemoved ? "OFF" : reading.IsStable ? "=" : "~";
            _recentReadings.Enqueue(reading.HasWeight ? $"{reading.WeightKg:N2}{mark}" : mark);
            while (_recentReadings.Count > 12) _recentReadings.Dequeue();
            var intervalText = interval is null ? "первый" : $"{interval.Value:N0} мс";
            ReadingHistoryText.Text = $"Пакетов: {_readingCount} · интервал: {intervalText} · {string.Join(" → ", _recentReadings)}";
        }));
    }

    private void Scanner_ScannerError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(new Action(() => StatusText.Text = $"Bluetooth: {message}"));
    }

    private void ScaleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSaveAvailability();
        if (ScaleList.SelectedItem is ScaleCandidate candidate)
        {
            AdvertisementHistoryText.Text = candidate.AdvertisementHistoryText;
            if (!candidate.IsSupported)
                StatusText.Text = "Это устройство видно, но его формат измерений пока не поддерживается.";
        }
    }

    private void RefreshSaveAvailability() =>
        SaveButton.IsEnabled = ScaleList.SelectedItem is ScaleCandidate { IsSupported: true };

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScaleList.SelectedItem is not ScaleCandidate { IsSupported: true } candidate) return;
        try
        {
            _savedConfig = new ScaleConfig(candidate.Address, candidate.Name,
                candidate.ServiceUuid, candidate.ProtocolName);
            ScaleConfigStore.Save(_savedConfig);
            SavedText.Text = $"Сохранено: {_savedConfig.Name} · {_savedConfig.Address}";
            StatusText.Text = $"Привязка готова. Основное приложение будет искать эти весы после перезапуска.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Не удалось сохранить: {ex.Message}";
        }
    }
}

public sealed class ScaleCandidate : INotifyPropertyChanged
{
    private string _name;
    private string _protocolName;
    private string _serviceUuid;
    private short _rssi;
    private int _payloadLength;
    private string _rawDataSummary;
    private readonly Queue<string> _recentPayloads = new();
    private DateTimeOffset? _lastPayloadAt;
    private int _payloadCount;
    private string _advertisementHistoryText = "Сырые BLE-пакеты: пока нет данных";
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Address { get; }
    public string Name => string.IsNullOrWhiteSpace(_name) ? "BLE-весы без имени" : _name;
    public string ProtocolName => _protocolName;
    public string ServiceUuid => _serviceUuid;
    public string PacketText => string.IsNullOrWhiteSpace(_rawDataSummary)
        ? (_serviceUuid.Length == 4 ? $"BLE {_serviceUuid} · пакет {_payloadLength} байт" : "Нет данных BLE-пакета")
        : _rawDataSummary;
    public string AdvertisementHistoryText => _advertisementHistoryText;
    public short Rssi => _rssi;
    public string SignalText => $"RSSI {_rssi} dBm";
    public bool IsSupported => _serviceUuid is ScaleProtocolDecoder.XiaomiWeightService
        or ScaleProtocolDecoder.XiaomiBodyCompositionService
        or ScaleProtocolDecoder.BluetoothWeightScaleGatt;

    public ScaleCandidate(ScaleAdvertisement advertisement)
    {
        Address = advertisement.Address;
        _name = advertisement.Name;
        _protocolName = advertisement.ProtocolName;
        _serviceUuid = advertisement.ServiceUuid;
        _rssi = advertisement.Rssi;
        _payloadLength = advertisement.Payload.Length;
        _rawDataSummary = advertisement.RawDataSummary;
    }

    public void Update(ScaleAdvertisement advertisement)
    {
        if (advertisement.Payload.Length > 0)
        {
            _payloadCount++;
            var intervalText = _lastPayloadAt is null
                ? "первый"
                : $"{(advertisement.SeenAt - _lastPayloadAt.Value).TotalMilliseconds:N0} мс";
            _lastPayloadAt = advertisement.SeenAt;
            _recentPayloads.Enqueue(Convert.ToHexString(advertisement.Payload));
            while (_recentPayloads.Count > 10) _recentPayloads.Dequeue();
            _advertisementHistoryText = $"Сырых BLE-пакетов: {_payloadCount} · интервал: {intervalText} · {string.Join(" → ", _recentPayloads)}";
            OnPropertyChanged(nameof(AdvertisementHistoryText));
        }
        if (!string.IsNullOrWhiteSpace(advertisement.Name))
            _name = advertisement.Name;
        if (!string.IsNullOrWhiteSpace(advertisement.ServiceUuid))
        {
            _protocolName = advertisement.ProtocolName;
            _serviceUuid = advertisement.ServiceUuid;
        }
        _rssi = advertisement.Rssi;
        if (advertisement.Payload.Length > 0 ||
            advertisement.RawDataSummary.Contains("AD16:", StringComparison.OrdinalIgnoreCase) ||
            advertisement.RawDataSummary.Contains("UUID:", StringComparison.OrdinalIgnoreCase) ||
            advertisement.RawDataSummary.Contains("MFG", StringComparison.OrdinalIgnoreCase))
        {
            _payloadLength = advertisement.Payload.Length;
            _rawDataSummary = advertisement.RawDataSummary;
        }
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ProtocolName));
        OnPropertyChanged(nameof(ServiceUuid));
        OnPropertyChanged(nameof(PacketText));
        OnPropertyChanged(nameof(Rssi));
        OnPropertyChanged(nameof(SignalText));
        OnPropertyChanged(nameof(IsSupported));
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
