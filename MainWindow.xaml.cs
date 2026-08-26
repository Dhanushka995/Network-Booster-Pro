using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetworkBooster
{
    public partial class MainWindow : Window
    {
        private const string APP_NAME    = "NetworkBoosterPro";
        private const string REG_PATH    = @"Software\NetworkBoosterPro";
        private const int    TARGET_PORT = 443;

        // Possible endpoints to try (order matters: base first, then dashboard)
        private readonly string[] _endpoints = new string[]
        {
            "/hutch_2_0/",
            "/hutch_2_0/capp/Dashboard/getConnectionDetails",
            "/hutch_2_0/capp/Dashboard/readDashboardData"
        };

        private readonly int[] _intervals = { 5, 10, 15, 30 };

        private bool   _isRunning = false;
        private long   _totalBytesSent = 0;
        private int    _reconnectAttempts = 0;

        private CancellationTokenSource? _cts;
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            SetupTrayIcon();
            LoadSettings();
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon    = System.Drawing.SystemIcons.Shield,
                Text    = "Network Booster Pro",
                Visible = false
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowMainWindow());
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) =>
            {
                StopConnection();
                Application.Current.Shutdown();
            });

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = true;
                    _trayIcon.ShowBalloonTip(
                        2000,
                        "Network Booster Pro",
                        _isRunning ? "Running — your connection is being boosted!" : "Running in background.",
                        System.Windows.Forms.ToolTipIcon.Info
                    );
                }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopConnection();
            _trayIcon?.Dispose();
        }

        private void ActionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
            {
                string input = HostTextBox.Text.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    SetStatus("Please enter a valid host!", "#EF4444");
                    return;
                }

                // We'll ignore path input now; use our endpoint list
                string host = input.Contains("://") ? new Uri(input).Host : input.Split('/')[0];

                SaveSettings();
                _ = StartConnectionLoop(host);
            }
            else
            {
                StopConnection();
                SetUIDisconnected();
            }
        }

        private async Task StartConnectionLoop(string host)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _reconnectAttempts = 0;
            _totalBytesSent = 0;

            SetUIConnecting();

            while (!token.IsCancellationRequested)
            {
                _reconnectAttempts++;

                if (_reconnectAttempts > 1)
                {
                    Dispatcher.Invoke(() =>
                        SetStatus($"Reconnecting... (Attempt #{_reconnectAttempts})", "#F59E0B"));
                }

                try
                {
                    // Try each endpoint in order, first one that works wins
                    string? workingPath = null;
                    foreach (var path in _endpoints)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            Dispatcher.Invoke(() => SetStatus($"Trying {path}...", "#F59E0B"));
                            await ConnectAndKeepaliveAsync(host, path, token);
                            // If ConnectAndKeepaliveAsync returns normally, server closed connection; try next
                        }
                        catch (OperationCanceledException)
                        {
                            throw; // user cancelled
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => SetStatus($"Failed {path}: {ex.Message}", "#EF4444"));
                            continue; // try next endpoint
                        }
                    }

                    // If no endpoint worked, wait and retry all again
                    if (workingPath == null)
                    {
                        Dispatcher.Invoke(() => SetStatus("No endpoint worked, retrying...", "#F59E0B"));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus($"Error — {ex.Message}", "#EF4444");
                        ActionBtn.IsEnabled = true;
                    });
                }

                try { await Task.Delay(5_000, token); }
                catch (OperationCanceledException) { break; }
            }

            Dispatcher.Invoke(SetUIDisconnected);
        }

        private async Task ConnectAndKeepaliveAsync(string host, string path, CancellationToken token)
        {
            using var tcp = new TcpClient();
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(15_000);
            await tcp.ConnectAsync(host, TARGET_PORT, connectCts.Token);

            using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, token);

            // Build request; if path contains "Dashboard" use POST with empty JSON, else GET
            bool isPost = path.Contains("Dashboard");
            string method = isPost ? "POST" : "GET";
            string requestBody = isPost ? "{}" : "";
            string httpRequest = $"{method} {path} HTTP/1.1\r\n" +
                                 $"Host: {host}\r\n" +
                                 $"Connection: keep-alive\r\n" +
                                 $"User-Agent: HutchOneApp/3.0 Android\r\n" +
                                 $"Accept: */*\r\n" +
                                 (isPost ? $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(requestBody)}\r\n" : "") +
                                 $"\r\n" + requestBody;

            byte[] requestBytes = Encoding.UTF8.GetBytes(httpRequest);
            byte[] responseBuffer = new byte[8192];

            // Send first request
            await ssl.WriteAsync(requestBytes, 0, requestBytes.Length, token);
            await ssl.FlushAsync(token);
            _totalBytesSent += requestBytes.Length;
            Dispatcher.Invoke(UpdateBytesLabel);

            // Read response (with timeout)
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            readCts.CancelAfter(5_000);
            int bytesRead = 0;
            try
            {
                bytesRead = await ssl.ReadAsync(responseBuffer, 0, responseBuffer.Length, readCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Timeout, but connection may still be alive; we'll treat as success if connected
            }

            if (bytesRead == 0)
            {
                throw new IOException("Server closed connection.");
            }

            // Success! Update UI
            Dispatcher.Invoke(() =>
            {
                SetUIConnected(host);
                ActionBtn.IsEnabled = true;
            });

            // Keepalive loop
            while (!token.IsCancellationRequested && tcp.Connected)
            {
                await ssl.WriteAsync(requestBytes, 0, requestBytes.Length, token);
                await ssl.FlushAsync(token);
                _totalBytesSent += requestBytes.Length;
                Dispatcher.Invoke(UpdateBytesLabel);

                try
                {
                    using var keepCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    keepCts.CancelAfter(3_000);
                    int read = await ssl.ReadAsync(responseBuffer, 0, responseBuffer.Length, keepCts.Token);
                    if (read == 0) throw new IOException("Server closed connection.");
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // ignore timeout
                }

                int interval = GetSelectedInterval() * 1_000;
                await Task.Delay(interval, token);
            }
        }

        private void StopConnection()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private void SetUIConnecting()
        {
            _isRunning = true;
            ActionBtn.IsEnabled = false;
            ActionBtn.Content = "Connecting...";
            ActionBtn.Background = MakeBrush("#F59E0B");
            HostTextBox.IsEnabled = false;
            IntervalCombo.IsEnabled = false;
            LoadIndicator.Visibility = Visibility.Visible;
            SetStatus("Connecting...", "#F59E0B");
        }

        private void SetUIConnected(string host)
        {
            ActionBtn.Content = "DISCONNECT";
            ActionBtn.Background = MakeBrush("#DC2626");
            SetStatus($"Connected → {host}", "#22C55E");
        }

        private void SetUIDisconnected()
        {
            _isRunning = false;
            ActionBtn.IsEnabled = true;
            ActionBtn.Content = "START";
            ActionBtn.Background = MakeBrush("#16A34A");
            HostTextBox.IsEnabled = true;
            IntervalCombo.IsEnabled = true;
            LoadIndicator.Visibility = Visibility.Hidden;
            SetStatus("Disconnected", "#64748B");
            BytesLabel.Text = "Data Sent: —";
        }

        private void SetStatus(string message, string hexColor)
        {
            StatusText.Text = $"Status: {message}";
            StatusText.Foreground = MakeBrush(hexColor);
        }

        private void UpdateBytesLabel()
        {
            long b = _totalBytesSent;
            BytesLabel.Text = b < 1_024
                ? $"Data Sent: {b} B"
                : b < 1_048_576
                    ? $"Data Sent: {b / 1024.0:F1} KB"
                    : $"Data Sent: {b / 1_048_576.0:F2} MB";
        }

        private int GetSelectedInterval() =>
            Dispatcher.Invoke(() =>
            {
                int i = IntervalCombo.SelectedIndex;
                return (i >= 0 && i < _intervals.Length) ? _intervals[i] : 10;
            });

        private static SolidColorBrush MakeBrush(string hex) =>
            (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

        private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using RegistryKey? rk = Registry.CurrentUser.OpenSubKey(runKey, true);
                if (rk == null) return;

                if (AutoStartCheckBox.IsChecked == true)
                {
                    string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    rk.SetValue(APP_NAME, exe);
                }
                else
                {
                    rk.DeleteValue(APP_NAME, false);
                }
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using RegistryKey? run = Registry.CurrentUser.OpenSubKey(runKey);
                AutoStartCheckBox.IsChecked = run?.GetValue(APP_NAME) != null;

                using RegistryKey? s = Registry.CurrentUser.OpenSubKey(REG_PATH);
                if (s != null)
                {
                    string? savedHost = s.GetValue("Host") as string;
                    if (!string.IsNullOrEmpty(savedHost))
                        HostTextBox.Text = savedHost;

                    int idx = Convert.ToInt32(s.GetValue("Interval", 1));
                    IntervalCombo.SelectedIndex = Math.Clamp(idx, 0, _intervals.Length - 1);
                }
            }
            catch
            {
                IntervalCombo.SelectedIndex = 1;
            }
        }

        private void SaveSettings()
        {
            try
            {
                using RegistryKey? s = Registry.CurrentUser.CreateSubKey(REG_PATH);
                if (s == null) return;
                s.SetValue("Host", HostTextBox.Text);
                s.SetValue("Interval", IntervalCombo.SelectedIndex);
            }
            catch { }
        }
    }
}
