using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GeminiWpfAiStudio;

public partial class MainWindow : Window
{
    private const int HotkeyCapture = 101;
    private const int HotkeyClickThrough = 102;
    private const int HotkeyHideShow = 103;
    private const int HotkeyFrameEdit = 104;
    private const uint ModControl = 0x0002;
    private const int WmHotkey = 0x0312;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly ObservableCollection<ChatMessage> _messages = [];
    private readonly List<GeminiContent> _history = [];
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private PendingImage? _pendingImage;
    private CaptureFrameWindow? _captureFrame;
    private HwndSource? _source;
    private DispatcherTimer? _topmostTimer;
    private AppConfig? _config;
    private bool _clickThrough;
    private bool _frameEditMode;
    private bool _stealthMode;
    private bool _hiddenByHotkey;
    private string ConfigPath => Path.Combine(AppContext.BaseDirectory, "desktop-auth-sync.json");
    private string LegacyConfigPath => Path.Combine(AppContext.BaseDirectory, "gemini-wpf-config.json");

    public MainWindow()
    {
        InitializeComponent();
        ChatItemsControl.ItemsSource = _messages;
        LoadConfig();
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => SaveConfig();
        AddMessage("系统", "Enter 发送，Shift+Enter 换行。Ctrl+1 截灰框发送，Ctrl+2 聊天窗鼠标穿透，Ctrl+3 隐藏/显示，Ctrl+4 调整截图框。", false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WndProc);
        RegisterHotkeys();
        ApplyDisplayAffinity(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer?.Stop();
        UnregisterHotkeys();
        _source?.RemoveHook(WndProc);
        _captureFrame?.Close();
        base.OnClosed(e);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyShellOpacity();
        CreateCaptureFrame();
        EnsureTopmostNoActivate();
        StartTopmostKeeper();
    }

    private void CreateCaptureFrame()
    {
        _captureFrame = new CaptureFrameWindow
        {
            Left = _config?.CaptureLeft ?? Left + Width + 18,
            Top = _config?.CaptureTop ?? Top + 60,
            Width = Math.Max(80, _config?.CaptureWidth ?? 520),
            Height = Math.Max(60, _config?.CaptureHeight ?? 320),
            Topmost = Topmost
        };
        _captureFrame.Show();
        SetClickThrough(_captureFrame, true);
        ForceTopmost(_captureFrame);
        ApplyDisplayAffinity(_captureFrame);
    }

    private void RegisterHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        RegisterHotKey(hwnd, HotkeyCapture, ModControl, (uint)KeyInterop.VirtualKeyFromKey(Key.D1));
        RegisterHotKey(hwnd, HotkeyClickThrough, ModControl, (uint)KeyInterop.VirtualKeyFromKey(Key.D2));
        RegisterHotKey(hwnd, HotkeyHideShow, ModControl, (uint)KeyInterop.VirtualKeyFromKey(Key.D3));
        RegisterHotKey(hwnd, HotkeyFrameEdit, ModControl, (uint)KeyInterop.VirtualKeyFromKey(Key.D4));
    }

    private void UnregisterHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(hwnd, HotkeyCapture);
        UnregisterHotKey(hwnd, HotkeyClickThrough);
        UnregisterHotKey(hwnd, HotkeyHideShow);
        UnregisterHotKey(hwnd, HotkeyFrameEdit);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeyCapture:
                _ = CaptureFrameAndSendAsync();
                break;
            case HotkeyClickThrough:
                ToggleClickThrough();
                break;
            case HotkeyHideShow:
                ToggleHotkeyVisibility();
                break;
            case HotkeyFrameEdit:
                ToggleFrameEditMode();
                break;
        }

        return IntPtr.Zero;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendPromptAsync();
    }

    private async void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            await SendPromptAsync();
            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TryAttachClipboardImage())
        {
            e.Handled = true;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void StealthButton_Click(object sender, RoutedEventArgs e)
    {
        _stealthMode = !_stealthMode;
        ShellOpacitySlider.Value = _stealthMode ? 8 : 30;
        ChatPanelBorder.Opacity = _stealthMode ? 0.45 : 1.0;
        StealthButton.Content = _stealthMode ? "显" : "隐";
        StatusTextBlock.Text = _stealthMode ? "隐身模式" : "正常模式";
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        await CaptureFrameAndSendAsync();
    }

    private void FrameEditButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFrameEditMode();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        _messages.Clear();
        AddMessage("系统", "已清空。", false);
        PromptTextBox.Focus();
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (_captureFrame is not null)
        {
            _captureFrame.Topmost = Topmost;
        }

        PinButton.Content = Topmost ? "顶" : "浮";
        StatusTextBlock.Text = Topmost ? "已置顶" : "取消置顶";
        EnsureTopmostNoActivate();
        SaveConfig();
    }

    private void ApplyWindowSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(WindowWidthTextBox.Text.Trim(), out var width))
        {
            Width = Math.Clamp(width, MinWidth, 1600);
        }

        if (double.TryParse(WindowHeightTextBox.Text.Trim(), out var height))
        {
            Height = Math.Clamp(height, MinHeight, 1600);
        }

        SaveConfig();
        StatusTextBlock.Text = "已应用大小";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ShellOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyShellOpacity();
    }

    private void ApplyShellOpacity()
    {
        if (ShellBorder is null || OpacityTextBlock is null || ShellOpacitySlider is null)
        {
            return;
        }

        var percent = Math.Clamp(ShellOpacitySlider.Value, 0, 100);
        OpacityTextBlock.Text = $"{percent:0}%";
        ShellBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(percent / 100d * 255), 16, 16, 16));
    }

    private async Task CaptureFrameAndSendAsync()
    {
        if (_captureFrame is null)
        {
            CreateCaptureFrame();
        }

        if (_captureFrame is null)
        {
            return;
        }

        try
        {
            _pendingImage = CaptureFrameImage();
            AttachmentTextBlock.Text = "截图：灰框区域";
            AttachmentBar.Visibility = Visibility.Visible;
            var screenshotPrompt = string.IsNullOrWhiteSpace(ScreenshotPromptTextBox.Text)
                ? "请分析这张截图。"
                : ScreenshotPromptTextBox.Text.Trim();
            await SendPromptAsync(screenshotPrompt);
        }
        catch (Exception ex)
        {
            AddMessage("错误", $"截图失败：{ex.Message}", false);
        }
    }

    private PendingImage CaptureFrameImage()
    {
        if (_captureFrame is null)
        {
            throw new InvalidOperationException("截图框未启动。");
        }

        var bounds = GetWindowPixelBounds(_captureFrame);
        if (bounds.Width < 8 || bounds.Height < 8)
        {
            throw new InvalidOperationException("截图框太小。");
        }

        var wasVisible = _captureFrame.Visibility == Visibility.Visible;
        if (wasVisible)
        {
            _captureFrame.Visibility = Visibility.Hidden;
            _captureFrame.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            Thread.Sleep(80);
        }

        try
        {
            return new PendingImage("灰框截图.png", "image/png", CaptureScreenPngBase64(bounds));
        }
        finally
        {
            if (wasVisible)
            {
                _captureFrame.Visibility = Visibility.Visible;
            }
        }
    }

    private PixelRect GetWindowPixelBounds(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var leftTop = transform.Transform(new System.Windows.Point(window.Left, window.Top));
        var rightBottom = transform.Transform(new System.Windows.Point(window.Left + window.ActualWidth, window.Top + window.ActualHeight));
        return new PixelRect(
            (int)Math.Round(leftTop.X),
            (int)Math.Round(leftTop.Y),
            Math.Max(1, (int)Math.Round(rightBottom.X - leftTop.X)),
            Math.Max(1, (int)Math.Round(rightBottom.Y - leftTop.Y)));
    }

    private static string CaptureScreenPngBase64(PixelRect bounds)
    {
        const int srccopy = 0x00CC0020;
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
        var oldObject = SelectObject(memoryDc, bitmap);

        try
        {
            if (!BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.Left, bounds.Top, srccopy))
            {
                throw new InvalidOperationException("BitBlt 截屏失败。");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }
        finally
        {
            SelectObject(memoryDc, oldObject);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private bool TryAttachClipboardImage()
    {
        if (!Clipboard.ContainsImage())
        {
            return false;
        }

        var image = Clipboard.GetImage();
        if (image is null)
        {
            return false;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        _pendingImage = new PendingImage("剪贴板图片.png", "image/png", Convert.ToBase64String(stream.ToArray()));
        AttachmentTextBlock.Text = "图片：剪贴板";
        AttachmentBar.Visibility = Visibility.Visible;
        StatusTextBlock.Text = "已粘贴图片";
        return true;
    }

    private void RemoveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingImage = null;
        AttachmentBar.Visibility = Visibility.Collapsed;
        AttachmentTextBlock.Text = "No image";
        StatusTextBlock.Text = "已移除图片";
    }

    private async Task SendPromptAsync(string? fallbackPrompt = null)
    {
        var prompt = PromptTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) && _pendingImage is not null && fallbackPrompt is not null)
        {
            prompt = fallbackPrompt;
        }

        if (string.IsNullOrWhiteSpace(prompt) && _pendingImage is null)
        {
            return;
        }

        var apiKey = ApiKeyBox.Password.Trim();
        var model = ModelTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            AddMessage("错误", "API Key 和模型名不能为空。", false);
            return;
        }

        SetBusy(true);
        PromptTextBox.Clear();

        var parts = new List<GeminiPart>();
        if (_pendingImage is not null)
        {
            parts.Add(GeminiPart.FromInlineData(_pendingImage.MimeType, _pendingImage.Base64Data));
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            parts.Add(GeminiPart.FromText(prompt));
        }

        var displayPrompt = string.IsNullOrWhiteSpace(prompt) ? "(仅图片)" : prompt;
        if (_pendingImage is not null)
        {
            displayPrompt += $"{Environment.NewLine}[图片: {Path.GetFileName(_pendingImage.Path)}]";
        }

        AddMessage("你", displayPrompt, true);

        var userContent = new GeminiContent("user", [.. parts]);
        _history.Add(userContent);
        _pendingImage = null;
        AttachmentBar.Visibility = Visibility.Collapsed;

        try
        {
            var responseText = await GenerateContentAsync(apiKey, model);
            _history.Add(new GeminiContent("model", [GeminiPart.FromText(responseText)]));
            AddMessage("Gemini", responseText, false);
            StatusTextBlock.Text = $"完成 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _history.Remove(userContent);
            AddMessage("错误", ex.Message, false);
            StatusTextBlock.Text = "请求失败";
        }
        finally
        {
            SetBusy(false);
            PromptTextBox.Focus();
            ChatScrollViewer.ScrollToEnd();
        }
    }

    private async Task<string> GenerateContentAsync(string apiKey, string model)
    {
        using var http = CreateHttpClient();
        var request = new GeminiRequest(
            Contents: [.. _history],
            SystemInstruction: string.IsNullOrWhiteSpace(SystemTextBox.Text)
                ? null
                : new GeminiContent(null, [GeminiPart.FromText(SystemTextBox.Text.Trim())]),
            GenerationConfig: new GeminiGenerationConfig(
                Temperature: ReadTemperature(),
                MaxOutputTokens: ReadMaxOutputTokens(),
                ThinkingConfig: new GeminiThinkingConfig(IncludeThoughts: false, ThinkingLevel: ReadThinkingLevel())));

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.Add("x-goog-api-key", apiKey);
        message.Content = JsonContent.Create(request, options: _jsonOptions);

        using var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            var apiError = TryReadApiError(body);
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {apiError}");
        }

        var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(body, _jsonOptions);
        var text = geminiResponse?.Candidates?
            .FirstOrDefault()?.Content?.Parts?
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Aggregate("", (current, next) => current + next);

        return string.IsNullOrWhiteSpace(text) ? "(Gemini 没有返回文本内容)" : text;
    }

    private HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler();
        if (ProxyCheckBox.IsChecked == true)
        {
            var proxyText = ProxyTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(proxyText))
            {
                if (!proxyText.Contains("://", StringComparison.Ordinal))
                {
                    proxyText = "http://" + proxyText;
                }

                handler.Proxy = new WebProxy(proxyText);
                handler.UseProxy = true;
            }
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
    }

    private double ReadTemperature()
    {
        return double.TryParse(TemperatureTextBox.Text.Trim(), out var value)
            ? Math.Clamp(value, 0, 2)
            : 0.9;
    }

    private int ReadMaxOutputTokens()
    {
        return int.TryParse(MaxTokensTextBox.Text.Trim(), out var value)
            ? Math.Clamp(value, 64, 65536)
            : 65536;
    }

    private string ReadThinkingLevel()
    {
        var selected = (ThinkingLevelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "low";
        return selected.Trim().ToUpperInvariant() switch
        {
            "MINIMAL" => "MINIMAL",
            "MEDIUM" => "MEDIUM",
            "HIGH" => "HIGH",
            _ => "LOW"
        };
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        SetClickThrough(this, _clickThrough);
        if (_captureFrame is not null)
        {
            SetClickThrough(_captureFrame, true);
        }

        StatusTextBlock.Text = _clickThrough ? "鼠标穿透 ON，Ctrl+2 关闭" : "鼠标穿透 OFF";
    }

    private void ToggleFrameEditMode()
    {
        if (_captureFrame is null)
        {
            CreateCaptureFrame();
        }

        _frameEditMode = !_frameEditMode;
        if (_captureFrame is not null)
        {
            SetClickThrough(_captureFrame, !_frameEditMode);
            _captureFrame.SetEditMode(_frameEditMode);
            _captureFrame.Activate();
        }

        FrameEditButton.Content = _frameEditMode ? "穿" : "框";
        StatusTextBlock.Text = _frameEditMode ? "截图框可拖动/缩放，Ctrl+4 结束" : "截图框已穿透";
        SaveConfig();
    }

    private void ToggleHotkeyVisibility()
    {
        _hiddenByHotkey = !_hiddenByHotkey;
        if (_hiddenByHotkey)
        {
            Hide();
            _captureFrame?.Hide();
            return;
        }

        Show();
        Activate();
        _captureFrame?.Show();
    }

    private static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(hwnd, GwlExStyle);
        style = enabled
            ? style | WsExTransparent | WsExLayered
            : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private void StartTopmostKeeper()
    {
        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _topmostTimer.Tick += (_, _) =>
        {
            if (ForceTopmostCheckBox.IsChecked == true)
            {
                EnsureTopmostNoActivate();
            }
        };
        _topmostTimer.Start();
    }

    private void EnsureTopmostNoActivate()
    {
        if (Topmost)
        {
            ForceTopmost(this);
            ApplyDisplayAffinity(this);
        }

        if (_captureFrame is not null)
        {
            _captureFrame.Topmost = true;
            ForceTopmost(_captureFrame);
            SetClickThrough(_captureFrame, !_frameEditMode);
            ApplyDisplayAffinity(_captureFrame);
        }
    }

    private static void ForceTopmost(Window window)
    {
        if (!window.IsVisible)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
    }

    private static void ApplyDisplayAffinity(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        }
    }

    private void LoadConfig()
    {
        try
        {
            var configPath = File.Exists(ConfigPath) ? ConfigPath : LegacyConfigPath;
            if (File.Exists(configPath))
            {
                _config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath), _jsonOptions);
            }
        }
        catch
        {
            _config = null;
        }

        if (_config is null)
        {
            WindowWidthTextBox.Text = Width.ToString("0");
            WindowHeightTextBox.Text = Height.ToString("0");
            return;
        }

        ApiKeyBox.Password = _config.ApiKey ?? "";
        ModelTextBox.Text = _config.Model ?? ModelTextBox.Text;
        SystemTextBox.Text = _config.SystemInstruction ?? "";
        ScreenshotPromptTextBox.Text = _config.ScreenshotPrompt ?? ScreenshotPromptTextBox.Text;
        ProxyCheckBox.IsChecked = _config.UseProxy ?? true;
        ProxyTextBox.Text = _config.Proxy ?? ProxyTextBox.Text;
        TemperatureTextBox.Text = _config.Temperature?.ToString("0.###") ?? TemperatureTextBox.Text;
        MaxTokensTextBox.Text = _config.MaxTokens?.ToString() ?? MaxTokensTextBox.Text;
        SetThinkingLevel(_config.ThinkingLevel ?? "low");
        ShellOpacitySlider.Value = _config.ShellOpacity ?? ShellOpacitySlider.Value;
        ForceTopmostCheckBox.IsChecked = _config.ForceTopmost ?? true;

        if (_config.WindowWidth is > 0)
        {
            Width = Math.Max(MinWidth, _config.WindowWidth.Value);
        }

        if (_config.WindowHeight is > 0)
        {
            Height = Math.Max(MinHeight, _config.WindowHeight.Value);
        }

        if (_config.WindowLeft.HasValue)
        {
            Left = _config.WindowLeft.Value;
        }

        if (_config.WindowTop.HasValue)
        {
            Top = _config.WindowTop.Value;
        }

        WindowWidthTextBox.Text = Width.ToString("0");
        WindowHeightTextBox.Text = Height.ToString("0");
    }

    private void SaveConfig()
    {
        try
        {
            var config = new AppConfig
            {
                ApiKey = ApiKeyBox.Password.Trim(),
                Model = ModelTextBox.Text.Trim(),
                SystemInstruction = SystemTextBox.Text,
                ScreenshotPrompt = ScreenshotPromptTextBox.Text,
                UseProxy = ProxyCheckBox.IsChecked == true,
                Proxy = ProxyTextBox.Text.Trim(),
                Temperature = ReadTemperature(),
                MaxTokens = ReadMaxOutputTokens(),
                ThinkingLevel = ReadThinkingLevel().ToLowerInvariant(),
                ShellOpacity = ShellOpacitySlider.Value,
                ForceTopmost = ForceTopmostCheckBox.IsChecked == true,
                WindowLeft = Left,
                WindowTop = Top,
                WindowWidth = Width,
                WindowHeight = Height,
                CaptureLeft = _captureFrame?.Left,
                CaptureTop = _captureFrame?.Top,
                CaptureWidth = _captureFrame?.Width,
                CaptureHeight = _captureFrame?.Height
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions(_jsonOptions) { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            _config = config;
        }
        catch
        {
            // Config persistence is best-effort; chat should keep working even if the folder is read-only.
        }
    }

    private void SetThinkingLevel(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var item in ThinkingLevelComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                ThinkingLevelComboBox.SelectedItem = item;
                return;
            }
        }

        ThinkingLevelComboBox.SelectedIndex = 1;
    }

    private string TryReadApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Fall through to raw response body.
        }

        return string.IsNullOrWhiteSpace(body) ? "(empty body)" : body;
    }

    private void SetBusy(bool busy)
    {
        SendButton.IsEnabled = !busy;
        PromptTextBox.IsEnabled = !busy;
        AttachButton.IsEnabled = !busy;
        StatusTextBlock.Text = busy ? "请求中..." : StatusTextBlock.Text;
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private void AddMessage(string role, string text, bool isUser)
    {
        _messages.Add(new ChatMessage(
            Role: role,
            Text: text,
            Background: isUser ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 74, 74, 74)) : new SolidColorBrush(System.Windows.Media.Color.FromArgb(95, 28, 28, 28)),
            Accent: isUser ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 230)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(205, 205, 205))));
        ChatScrollViewer.ScrollToEnd();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}

public record PixelRect(int Left, int Top, int Width, int Height);

public sealed class AppConfig
{
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? SystemInstruction { get; set; }
    public string? ScreenshotPrompt { get; set; }
    public bool? UseProxy { get; set; }
    public string? Proxy { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public string? ThinkingLevel { get; set; }
    public double? ShellOpacity { get; set; }
    public bool? ForceTopmost { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? CaptureLeft { get; set; }
    public double? CaptureTop { get; set; }
    public double? CaptureWidth { get; set; }
    public double? CaptureHeight { get; set; }
}

public sealed class CaptureFrameWindow : Window
{
    private readonly Border _frameBorder;

    public CaptureFrameWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Title = "Capture Frame";

        _frameBorder = new Border
        {
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 190, 190, 190)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(8, 255, 255, 255))
        };
        Content = _frameBorder;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public void SetEditMode(bool enabled)
    {
        _frameBorder.BorderBrush = new SolidColorBrush(enabled
            ? System.Windows.Media.Color.FromArgb(220, 230, 230, 230)
            : System.Windows.Media.Color.FromArgb(26, 190, 190, 190));
        _frameBorder.BorderThickness = new Thickness(enabled ? 2 : 1);
        _frameBorder.Background = new SolidColorBrush(enabled
            ? System.Windows.Media.Color.FromArgb(24, 255, 255, 255)
            : System.Windows.Media.Color.FromArgb(8, 255, 255, 255));
    }
}

public record ChatMessage(string Role, string Text, Brush Background, Brush Accent);

public record PendingImage(string Path, string MimeType, string Base64Data);

public record GeminiRequest(
    [property: JsonPropertyName("contents")] GeminiContent[] Contents,
    [property: JsonPropertyName("systemInstruction")] GeminiContent? SystemInstruction,
    [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

public record GeminiGenerationConfig(
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("thinkingConfig")] GeminiThinkingConfig ThinkingConfig);

public record GeminiThinkingConfig(
    [property: JsonPropertyName("includeThoughts")] bool IncludeThoughts,
    [property: JsonPropertyName("thinkingLevel")] string ThinkingLevel);

public record GeminiContent(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("parts")] GeminiPart[] Parts);

public record GeminiPart(
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("inlineData")] GeminiInlineData? InlineData = null)
{
    public static GeminiPart FromText(string text)
    {
        return new GeminiPart(Text: text);
    }

    public static GeminiPart FromInlineData(string mimeType, string base64Data)
    {
        return new GeminiPart(InlineData: new GeminiInlineData(mimeType, base64Data));
    }
}

public record GeminiInlineData(
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("data")] string Data);

public record GeminiResponse([property: JsonPropertyName("candidates")] GeminiCandidate[]? Candidates);

public record GeminiCandidate([property: JsonPropertyName("content")] GeminiContent? Content);
