using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScaleCore;

namespace LegoKiosk;

public partial class MainWindow : Window
{
    private readonly BleScaleScanner _scanner = new();
    private readonly ScaleConfig? _config = ScaleConfigStore.Load();
    private readonly DispatcherTimer _demoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _scannerRetryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _gattRetryTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _scaleIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _countAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private GattWeightScaleClient? _gattClient;
    private DateTimeOffset _lastScaleReadingAt;
    private bool _showingResult;
    private bool _awaitingMeasurementStart = true;
    private bool _stableControlSeen;
    private bool _demoResultVisible;
    private bool _debugVisible;
    private bool _kioskMode = true;
    private double _displayedCount;
    private double _countAnimationStart;
    private long _countAnimationTarget;
    private DateTimeOffset _countAnimationStartedAt;
    private DateTimeOffset _lastRawAdvertisementAt;
    private DateTimeOffset _lastWeightAdvertisementAt;
    private int _totalPacketCount;
    private int _rawAdvertisementCount;
    private int _rawWeightAdvertisementCount;
    private int _intermediatePacketCount;
    private int _stablePacketCount;
    private int _controlPacketCount;
    private int _removedPacketCount;
    private double? _lastAdvertisementIntervalMs;
    private double? _lastWeightAdvertisementIntervalMs;
    private double? _lastReadingIntervalMs;
    private string _lastRawPayload = "—";
    private string _lastReadingStatus = "нет декодированных измерений";
    private readonly Queue<string> _recentRawWeightPackets = new();
    private readonly Queue<string> _recentDecodedReadings = new();
    private string? _lastPartNumber;
    private LegoBrick? _activeBrick;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
        Closed += (_, _) =>
        {
            _demoTimer.Stop();
            _scannerRetryTimer.Stop();
            _gattRetryTimer.Stop();
            _scaleIdleTimer.Stop();
            _countAnimationTimer.Stop();
            _scanner.Dispose();
            _gattClient?.Dispose();
        };
        _scanner.ReadingReceived += Scanner_ReadingReceived;
        _scanner.ScaleSeen += Scanner_ScaleSeen;
        _scanner.ScannerError += Scanner_ScannerError;
        _demoTimer.Tick += DemoTimer_Tick;
        _scannerRetryTimer.Tick += (_, _) => _scanner.Start();
        _gattRetryTimer.Tick += GattRetryTimer_Tick;
        _scaleIdleTimer.Tick += ScaleIdleTimer_Tick;
        _countAnimationTimer.Tick += CountAnimationTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartIdlePulse();

        if (App.DemoMode)
        {
            StatusText.Text = "ДЕМО-РЕЖИМ";
            _demoTimer.Start();
            return;
        }

        if (_config is null)
        {
            StatusText.Text = "Сначала запустите «Настройка весов»";
            return;
        }

        if (_config.ProtocolName == ScaleProtocolDecoder.BluetoothWeightScaleGattName)
        {
            StatusText.Text = $"Подключаю весы: {_config.Name}";
            _gattClient = new GattWeightScaleClient();
            _gattClient.ReadingReceived += Scanner_ReadingReceived;
            _gattClient.ClientError += Scanner_ScannerError;
            await _gattClient.StartAsync(_config.Address);
            _gattRetryTimer.Start();
            _scaleIdleTimer.Start();
            return;
        }

        StatusText.Text = $"Весы: {_config.Name} · {_config.Address}";
        _scanner.Start();
        _scannerRetryTimer.Start();
        _scaleIdleTimer.Start();
    }

    private void Scanner_ReadingReceived(object? sender, WeightReading reading)
    {
        if (_config is null || !string.Equals(reading.Address, _config.Address, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var receivedAt = DateTimeOffset.Now;
            _lastReadingIntervalMs = _lastScaleReadingAt == default
                ? null
                : (receivedAt - _lastScaleReadingAt).TotalMilliseconds;
            _lastScaleReadingAt = receivedAt;
            _lastReadingStatus = !reading.HasWeight ? "контроль без веса" : reading.IsRemoved ? "пусто" : reading.IsStable ? "stable" : "промежуточный";
            UpdateDiagnostics(reading);
            HandleReading(reading);
        }));
    }

    private void Scanner_ScaleSeen(object? sender, ScaleAdvertisement advertisement)
    {
        if (_config is null || !string.Equals(advertisement.Address, _config.Address, StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _rawAdvertisementCount++;
            _lastAdvertisementIntervalMs = _lastRawAdvertisementAt == default
                ? null
                : (advertisement.SeenAt - _lastRawAdvertisementAt).TotalMilliseconds;
            _lastRawAdvertisementAt = advertisement.SeenAt;
            if (advertisement.Payload.Length > 0)
            {
                _rawWeightAdvertisementCount++;
                _lastWeightAdvertisementIntervalMs = _lastWeightAdvertisementAt == default
                    ? null
                    : (advertisement.SeenAt - _lastWeightAdvertisementAt).TotalMilliseconds;
                _lastWeightAdvertisementAt = advertisement.SeenAt;
                _lastRawPayload = Convert.ToHexString(advertisement.Payload);
                _recentRawWeightPackets.Enqueue(_lastRawPayload);
                while (_recentRawWeightPackets.Count > 8) _recentRawWeightPackets.Dequeue();
            }
            RefreshDiagnostics();
        }));
    }

    private void HandleReading(WeightReading reading)
    {
        // 0x82/0xA2 carry state but no new weight. A stable control frame
        // closes a measurement only by starting the 3-second quiet period.
        if (!reading.HasWeight)
        {
            if (_showingResult && reading.IsStable) _stableControlSeen = true;
            return;
        }

        if (reading.IsRemoved)
        {
            ReturnToIdle();
            StatusText.Text = $"Весы на связи · {_config!.Address}";
            return;
        }

        // A scale can keep repeating its last stable result after a visitor steps off.
        // Start a new visit only on a fresh, changing measurement packet.
        if (_awaitingMeasurementStart && reading.IsStable &&
            reading.ProtocolName.StartsWith("Xiaomi", StringComparison.OrdinalIgnoreCase)) return;

        if (_awaitingMeasurementStart) _awaitingMeasurementStart = false;
        if (reading.IsStable) _stableControlSeen = true;

        if (!_showingResult)
        {
            _activeBrick = BrickCatalog.PickDifferent(_lastPartNumber);
            _lastPartNumber = _activeBrick.PartNumber;
            var imagePath = Path.Combine(AppContext.BaseDirectory, _activeBrick.ImagePath.Replace('/', Path.DirectorySeparatorChar));
            BrickImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
            ShowResultSurface();
            _showingResult = true;
        }

        // Xiaomi sends changing, unstable weight packets while the person is stepping on.
        // Keep the selected brick fixed for this visit, but update its count for every packet.
        AnimateBrickCount(_activeBrick!.CountForWeightKg(reading.WeightKg));
        StatusText.Text = $"Весы на связи · {_config!.Address}";
    }

    private void Scanner_ScannerError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(new Action(() => StatusText.Text = $"Bluetooth: {message}"));
    }

    private async void GattRetryTimer_Tick(object? sender, EventArgs e)
    {
        if (_gattClient is null || _config is null || _gattClient.IsConnected) return;
        StatusText.Text = $"Повторное подключение к {_config.Name}…";
        await _gattClient.StartAsync(_config.Address);
    }

    private void ScaleIdleTimer_Tick(object? sender, EventArgs e)
    {
        if (!_showingResult || !_stableControlSeen || _lastScaleReadingAt == default ||
            DateTimeOffset.Now - _lastScaleReadingAt < TimeSpan.FromSeconds(3)) return;

        ReturnToIdle();
    }

    private void ReturnToIdle()
    {
        var hadResult = _showingResult;
        _showingResult = false;
        _awaitingMeasurementStart = true;
        _stableControlSeen = false;
        _activeBrick = null;

        if (!hadResult)
        {
            if (IdlePanel.Visibility != Visibility.Visible)
            {
                IdlePanel.BeginAnimation(OpacityProperty, null);
                IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
                IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
                IdlePanel.Visibility = Visibility.Visible;
                IdlePanel.Opacity = 1;
                IdlePanelScale.ScaleX = 1;
                IdlePanelScale.ScaleY = 1;
                StartIdlePulse();
            }
            return;
        }

        _countAnimationTimer.Stop();
        IdleIllustrationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        IdleIllustrationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);

        ResultPanel.BeginAnimation(OpacityProperty, null);
        ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        ResultPanel.Visibility = Visibility.Visible;
        ResultPanel.Opacity = 1;
        ResultPanelScale.ScaleX = 1;
        ResultPanelScale.ScaleY = 1;

        var resultFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        resultFade.Completed += (_, _) =>
        {
            if (_showingResult) return;
            ResultPanel.Visibility = Visibility.Collapsed;
            ResultPanel.BeginAnimation(OpacityProperty, null);
            ResultPanel.Opacity = 1;
            ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            ResultPanelScale.ScaleX = 1;
            ResultPanelScale.ScaleY = 1;
        };
        ResultPanel.BeginAnimation(OpacityProperty, resultFade);

        var resultScaleX = new DoubleAnimation(1, 0.86, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, resultScaleX);
        ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, resultScaleX.Clone());

        IdlePanel.BeginAnimation(OpacityProperty, null);
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        IdlePanel.Visibility = Visibility.Visible;
        IdlePanel.Opacity = 0;
        IdlePanelScale.ScaleX = 0.88;
        IdlePanelScale.ScaleY = 0.88;

        var idleFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360))
        {
            BeginTime = TimeSpan.FromMilliseconds(100),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        idleFade.Completed += (_, _) =>
        {
            if (!_showingResult) StartIdlePulse();
        };
        IdlePanel.BeginAnimation(OpacityProperty, idleFade);

        var idleScaleX = new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(360))
        {
            BeginTime = TimeSpan.FromMilliseconds(100),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, idleScaleX);
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, idleScaleX.Clone());
    }

    private void ShowResultSurface()
    {
        _countAnimationTimer.Stop();
        _displayedCount = 0;
        BrickCount.Text = FormatCount(0);
        IdlePanel.BeginAnimation(OpacityProperty, null);
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        IdlePanel.Visibility = Visibility.Visible;
        IdlePanel.Opacity = 1;
        IdlePanelScale.ScaleX = 1;
        IdlePanelScale.ScaleY = 1;

        var idleFade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        idleFade.Completed += (_, _) =>
        {
            if (_showingResult) IdlePanel.Visibility = Visibility.Collapsed;
        };
        IdlePanel.BeginAnimation(OpacityProperty, idleFade);

        var idleScaleX = new DoubleAnimation(1, 0.86, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, idleScaleX);
        IdlePanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, idleScaleX.Clone());

        ResultPanel.BeginAnimation(OpacityProperty, null);
        ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        ResultPanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        ResultPanel.Opacity = 1;
        ResultPanelScale.ScaleX = 1;
        ResultPanelScale.ScaleY = 1;
        ResultPanel.Visibility = Visibility.Visible;
        AnimatePop(ResultTitle, ResultTitleScale, TimeSpan.FromMilliseconds(120));
        AnimatePop(BrickImage, BrickImageScale, TimeSpan.FromMilliseconds(390));
        AnimatePop(BrickCount, BrickCountScale, TimeSpan.FromMilliseconds(650));
    }

    private static void AnimatePop(UIElement element, System.Windows.Media.ScaleTransform scale, TimeSpan delay)
    {
        element.BeginAnimation(OpacityProperty, null);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        element.Opacity = 0;
        scale.ScaleX = 0.84;
        scale.ScaleY = 0.84;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleX = new DoubleAnimation(0.84, 1, TimeSpan.FromMilliseconds(340))
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        element.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleX.Clone());
    }

    private void StartIdlePulse()
    {
        IdleIllustrationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        IdleIllustrationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        IdleIllustrationScale.ScaleX = 1;
        IdleIllustrationScale.ScaleY = 1;

        var pulseX = new DoubleAnimation(0.985, 1.015, TimeSpan.FromSeconds(1.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        IdleIllustrationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulseX);
        IdleIllustrationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulseX.Clone());
    }

    private void AnimateBrickCount(long target)
    {
        _countAnimationTimer.Stop();
        _countAnimationStart = _displayedCount;
        _countAnimationTarget = target;
        _countAnimationStartedAt = DateTimeOffset.UtcNow;
        if (Math.Abs(_countAnimationTarget - _displayedCount) < 0.5)
        {
            _displayedCount = target;
            BrickCount.Text = FormatCount(target);
            return;
        }

        _countAnimationTimer.Start();
    }

    private void CountAnimationTimer_Tick(object? sender, EventArgs e)
    {
        const double durationMs = 420;
        var progress = Math.Clamp((DateTimeOffset.UtcNow - _countAnimationStartedAt).TotalMilliseconds / durationMs, 0, 1);
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        _displayedCount = _countAnimationStart + (_countAnimationTarget - _countAnimationStart) * easedProgress;
        BrickCount.Text = FormatCount(_displayedCount);

        if (progress < 1) return;
        _displayedCount = _countAnimationTarget;
        BrickCount.Text = FormatCount(_displayedCount);
        _countAnimationTimer.Stop();
    }

    private static string FormatCount(double count) =>
        Math.Max(0, (long)Math.Round(count, MidpointRounding.AwayFromZero))
            .ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));

    private void UpdateDiagnostics(WeightReading reading)
    {
        _totalPacketCount++;
        if (!reading.HasWeight) _controlPacketCount++;
        else if (reading.IsRemoved) _removedPacketCount++;
        else if (reading.IsStable) _stablePacketCount++;
        else _intermediatePacketCount++;

        var marker = !reading.HasWeight ? "ctrl" : reading.IsRemoved ? "OFF" : reading.IsStable ? "=" : "~";
        var controlByte = reading.ControlByte is byte rawControl ? $"[{rawControl:X2}]" : "";
        _recentDecodedReadings.Enqueue(reading.HasWeight
            ? $"{reading.WeightKg:N2}{marker}{controlByte}"
            : $"{marker}{controlByte}");
        while (_recentDecodedReadings.Count > 12) _recentDecodedReadings.Dequeue();
        var control = reading.ControlByte is byte value ? $" · ctrl 0x{value:X2}" : "";
        _lastReadingStatus = reading.HasWeight
            ? $"{reading.WeightKg:N2} кг · {_lastReadingStatus}{control}"
            : $"контрольный кадр · {_lastReadingStatus}{control}";
        RefreshDiagnostics();
    }

    private void RefreshDiagnostics()
    {
        var rawInterval = _lastAdvertisementIntervalMs is null ? "—" : $"{_lastAdvertisementIntervalMs.Value:N0} мс";
        var weightRawInterval = _lastWeightAdvertisementIntervalMs is null ? "—" : $"{_lastWeightAdvertisementIntervalMs.Value:N0} мс";
        var weightInterval = _lastReadingIntervalMs is null ? "—" : $"{_lastReadingIntervalMs.Value:N0} мс";
        var sessionState = _showingResult ? "сессия активна" : "ожидание нового измерения";
        DebugText.Text = $"AD {_rawAdvertisementCount} (Δ {rawInterval}) · весовых AD {_rawWeightAdvertisementCount} (Δ {weightRawInterval}) · " +
                         $"декодировано {_totalPacketCount}: ~ {_intermediatePacketCount} / = {_stablePacketCount} / ctrl {_controlPacketCount} / OFF {_removedPacketCount} · " +
                         $"{sessionState} · последнее: {_lastReadingStatus} (Δ {weightInterval})\n" +
                         $"Значения: {string.Join(" → ", _recentDecodedReadings)}\n" +
                         $"Сырые кадры: {string.Join(" → ", _recentRawWeightPackets)} · F11 окно / F2 данные";
    }

    private void DemoTimer_Tick(object? sender, EventArgs e)
    {
        if (_demoResultVisible)
        {
            _demoResultVisible = false;
            ReturnToIdle();
            return;
        }

        var kg = 42 + Random.Shared.NextDouble() * 70;
        var brick = BrickCatalog.PickDifferent(_lastPartNumber);
        _lastPartNumber = brick.PartNumber;
        var imagePath = Path.Combine(AppContext.BaseDirectory, brick.ImagePath.Replace('/', Path.DirectorySeparatorChar));
        BrickImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
        ShowResultSurface();
        _demoResultVisible = true;
        _showingResult = true;
        AnimateBrickCount(brick.CountForWeightKg(kg));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Q)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            _debugVisible = !_debugVisible;
            DebugPanel.Visibility = _debugVisible ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F11) return;

        _kioskMode = !_kioskMode;
        Topmost = false;
        if (_kioskMode)
        {
            WindowState = WindowState.Normal;
            WindowStyle = System.Windows.WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Width = 1280;
            Height = 720;
            Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
            Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2);
        }
        e.Handled = true;
    }
}
