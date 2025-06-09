using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GpuMemoryOverlay
{
    public partial class MainWindow : Window
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int GWL_EXSTYLE = (-20);
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -1;
        private const int HTCLIENT = 1;
        private const int HOTKEY_ID = 9000;
        private const uint MOD_NONE = 0x0000;
        private const uint VK_F8 = 0x77;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private Point _offset;
        private bool _isDragging = false;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private PerformanceCounter? _dedicatedCounter;
        private PerformanceCounter? _sharedCounter;
        private AppSettings _settings = new AppSettings();
        private readonly string _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GpuMemoryOverlay",
            "settings.json");
        private IntPtr _windowHandle;
        private HwndSource? _source;

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            InitializeUI();
            InitializePerformanceCounters();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += UpdateMemoryUsage;
            _timer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _windowHandle = new WindowInteropHelper(this).Handle;
            _source = HwndSource.FromHwnd(_windowHandle);
            _source.AddHook(WndProc);

            int extendedStyle = GetWindowLong(_windowHandle, GWL_EXSTYLE);
            SetWindowLong(_windowHandle, GWL_EXSTYLE, extendedStyle);

            RegisterHotKey(_windowHandle, HOTKEY_ID, MOD_NONE, VK_F8);
        }

        private const int WM_HOTKEY = 0x0312;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                if (wParam.ToInt32() == HOTKEY_ID)
                {
                    ToggleVisibility();
                    handled = true;
                }
            }

            if (msg == WM_NCHITTEST)
            {
                int x = (int)(lParam.ToInt32() & 0xFFFF);
                int y = (int)(lParam.ToInt32() >> 16);
                Point mousePos = new Point(x, y);
                Point overlayPoint = OverlayBorder.PointFromScreen(mousePos);

                if (overlayPoint.X >= 0 && overlayPoint.X <= OverlayBorder.ActualWidth &&
                    overlayPoint.Y >= 0 && overlayPoint.Y <= OverlayBorder.ActualHeight)
                {
                    handled = true;
                    return new IntPtr(HTCLIENT);
                }
            }
            return IntPtr.Zero;
        }

        private void InitializeUI()
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
            MemoryText.Foreground = new SolidColorBrush(_settings.FontColor);
            MemoryText.FontSize = _settings.FontSize;

            var contextMenu = new ContextMenu();

            var decimalMenuItem = new MenuItem 
            { 
                Header = "Show Decimal", 
                IsCheckable = true,
                IsChecked = _settings.ShowDecimal
            };
            decimalMenuItem.Click += (s, e) => 
            {
                _settings.ShowDecimal = decimalMenuItem.IsChecked;
                SaveSettings();
            };
            contextMenu.Items.Add(decimalMenuItem);

            var colorMenu = new MenuItem { Header = "Font Color" };
            foreach (var color in new[] {
                Colors.White, Colors.Yellow, Colors.Cyan,
                Colors.Lime, Colors.Orange, Colors.Magenta, Colors.Red
            })
            {
                var colorItem = new MenuItem
                {
                    Header = color.ToString(),
                    Background = new SolidColorBrush(color),
                    Foreground = new SolidColorBrush(Colors.Black)
                };
                colorItem.Click += (s, e) =>
                {
                    _settings.FontColor = color;
                    MemoryText.Foreground = new SolidColorBrush(color);
                    SaveSettings();
                };
                colorMenu.Items.Add(colorItem);
            }
            contextMenu.Items.Add(colorMenu);

            var fontSizeMenu = new MenuItem { Header = "Font Size" };
            foreach (var size in new[] { 12.0, 14.0, 16.0, 18.0, 20.0, 24.0 })
            {
                var sizeItem = new MenuItem { Header = $"{size}px" };
                sizeItem.Click += (s, e) =>
                {
                    _settings.FontSize = size;
                    MemoryText.FontSize = size;
                    SaveSettings();
                };
                fontSizeMenu.Items.Add(sizeItem);
            }
            contextMenu.Items.Add(fontSizeMenu);

            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (s, e) => Application.Current.Shutdown();
            contextMenu.Items.Add(exitItem);

            OverlayBorder.ContextMenu = contextMenu;
            UpdateWindowSize();
        }

        private void UpdateWindowSize()
        {
            Width = 380;
            Height = 50;
        }

        private void InitializePerformanceCounters()
        {
            try
            {
                _dedicatedCounter = new PerformanceCounter(
                    "GPU Process Memory",
                    "Dedicated Usage",
                    "_Total");

                _sharedCounter = new PerformanceCounter(
                    "GPU Process Memory",
                    "Shared Usage",
                    "_Total");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Performance counters unavailable: {ex.Message}");
            }
        }

        private void UpdateMemoryUsage(object? sender, EventArgs e)
        {
            try
            {
                double dedicatedMB = 0;
                double sharedMB = 0;

                if (_dedicatedCounter != null && _sharedCounter != null)
                {
                    try
                    {
                        dedicatedMB = _dedicatedCounter.NextValue() / (1024 * 1024);
                        sharedMB = _sharedCounter.NextValue() / (1024 * 1024);
                    }
                    catch
                    {
                        InitializePerformanceCounters();
                    }
                }

                if (dedicatedMB == 0 && sharedMB == 0)
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");

                    foreach (ManagementObject obj in searcher.Get())
                    {
                        dedicatedMB += Convert.ToDouble(obj["DedicatedUsage"]) / (1024 * 1024);
                        sharedMB += Convert.ToDouble(obj["SharedUsage"]) / (1024 * 1024);
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    string format = _settings.ShowDecimal ? "F1" : "F0";
                    MemoryText.Text = $"VRAM {dedicatedMB.ToString(format)} MB | {sharedMB.ToString(format)} MB";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update failed: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    MemoryText.Text = "GPU data unavailable";
                });
            }
        }

        private void ToggleVisibility()
        {
            if (this.Visibility == Visibility.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;

                var directory = Path.GetDirectoryName(_settingsPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_settings, options);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        private void OverlayBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isDragging = true;
                var pos = GetWpfMousePosition();
                _offset = new Point(pos.X - Left, pos.Y - Top);
                Mouse.Capture(OverlayBorder);
                e.Handled = true;
            }
        }

        private void OverlayBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                var pos = GetWpfMousePosition();
                Left = pos.X - _offset.X;
                Top = pos.Y - _offset.Y;
                e.Handled = true;
            }
        }

        private void OverlayBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                Mouse.Capture(null);
                SaveSettings();
                e.Handled = true;
            }
        }

        private void OverlayBorder_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (OverlayBorder.ContextMenu != null)
            {
                OverlayBorder.ContextMenu.PlacementTarget = OverlayBorder;
                OverlayBorder.ContextMenu.IsOpen = true;
                e.Handled = true;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _timer.Stop();
            _dedicatedCounter?.Dispose();
            _sharedCounter?.Dispose();
            _source?.RemoveHook(WndProc);
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            SaveSettings();
            base.OnClosing(e);
        }

        private Point GetWpfMousePosition()
        {
            GetCursorPos(out var point);
            var screenPoint = new Point(point.X, point.Y);

            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                var transform = source.CompositionTarget.TransformFromDevice;
                return transform.Transform(screenPoint);
            }

            return screenPoint;
        }
    }

    public class AppSettings
    {
        public double WindowLeft { get; set; } = 100;
        public double WindowTop { get; set; } = 100;
        public Color FontColor { get; set; } = Colors.White;
        public double FontSize { get; set; } = 16;
        public bool ShowDecimal { get; set; } = true;
    }
}