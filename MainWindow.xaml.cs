using System;
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
        // ── Constants ──────────────────────────────────────────────────────────
        private const string APP_NAME   = "NetworkBoosterPro";
        private const string REG_PATH   = @"Software\NetworkBoosterPro";
        private const int    TARGET_PORT = 443;

        private readonly int[] _intervals = { 5, 10, 15, 30 }; // seconds

        // ── State ──────────────────────────────────────────────────────────────
        private bool   _isRunning = false;
        private long   _totalBytesSent = 0;
        private int    _reconnectAttempts = 0;

        private CancellationTokenSource      _cts;
        private System.Windows.Forms.NotifyIcon _trayIcon;

        // ══════════════════════════════════════════════════════════════════════
        //  Init
        // ══════════════════════════════════════════════════════════════════════

        public MainWindow()
        {
            InitializeComponent();
            SetupTrayIcon();
            LoadSettings();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Tray Icon
        // ══════════════════════════════════════════════════════════════════════

        private void SetupTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon    = System.Drawing.SystemIcons.Shield,
                Text    = "Network Booster Pro",
                Visible = false
            };

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open",  null, (s, e) => ShowMainWindow());
            menu.Items.Add("-");
            menu.Items.Add("Exit",  null, (s, e) =>
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
            _trayIcon.Visible = false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Window Events
        // ══════════════════════════════════════════════════════════════════════

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                _trayIcon.Visible = true;
                _trayIcon.ShowBalloonTip(
                    2000,
                    "Network Booster Pro",
                    _isRunning
                        ? "Running — your connection is being boosted!"
                        : "Running in background.",
                    System.Windows.Forms.ToolTipIcon.Info
                );
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopConnection();
            _trayIcon?.Dispose();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Button Click
        // ══════════════════════════════════════════════════════════════════════

        private void ActionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
            {
                string host = HostTextBox.Text.Trim();

                if (string.IsNullOrEmpty(host))
                {
                    SetStatus("Please enter a valid host!", "#EF4444");
                    return;
                }

                SaveSettings();
                _ = StartConnectionLoop(host);   // fire-and-forget, UI stays responsive
            }
            else
            {
                StopConnection();
                SetUIDisconnected();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Connection Loop  (reconnects automatically on any failure)
        // ══════════════════════════════════════════════════════════════════════

        private async Task StartConnectionLoop(string host)
        {
            _cts              = new CancellationTokenSource();
            var token         = _cts.Token;
            _reconnectAttempts = 0;
            _totalBytesSent   = 0;

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
                    await ConnectAndKeepaliveAsync(host, token);
                }
                catch (OperationCanceledException)
                {
                    break;   // User pressed Disconnect — clean exit
                }
                catch (Exception ex)
                {
                    // Network error — show message, wait 5 s, retry
                    Dispatcher.Invoke(() =>
                    {
                        SetStatus($"Error — {ex.Message}", "#EF4444");
                        ActionBtn.IsEnabled = true;   // allow user to disconnect manually
                    });

                    try   { await Task.Delay(5_000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            Dispatcher.Invoke(SetUIDisconnected);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Core: TLS Connect + Keepalive Loop
        //
        //  How it works:
        //    1. TCP connect to oneapp.hutch.lk:443
        //    2. TLS handshake (SSL/TLS layer)
        //    3. Send HTTP GET request every N seconds to keep connection alive
        //    4. Hutch network sees active TLS session → removes speed throttle
        //    5. All browser traffic (YouTube etc.) gets full speed automatically
        // ══════════════════════════════════════════════════════════════════════

        private async Task ConnectAndKeepaliveAsync(string host, CancellationToken token)
        {
            // ── Step 1: TCP Connect ─────────────────────────────────────────
            Dispatcher.Invoke(() => SetStatus($"Connecting to {host}...", "#F59E0B"));

            using var tcp = new TcpClient();

            // TCP-level keepalive so the OS keeps the socket alive between our requests
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket,
                                       SocketOptionName.KeepAlive, true);

            // 15-second timeout for initial connection
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(15_000);

            await tcp.ConnectAsync(host, TARGET_PORT, connectCts.Token);

            // ── Step 2: TLS Handshake ───────────────────────────────────────
            Dispatcher.Invoke(() => SetStatus("Securing connection (TLS)...", "#F59E0B"));

            // Accept Hutch's server cert (avoid app crashing on cert issues)
            using var ssl = new SslStream(tcp.GetStream(), false,
                                          (_, _, _, _) => true);

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost             = host,
                EnabledSslProtocols    = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, token);

            // ── Step 3: Connected! Update UI ────────────────────────────────
            _reconnectAttempts = 0;
            Dispatcher.Invoke(() =>
            {
                SetUIConnected(host);
                ActionBtn.IsEnabled = true;
            });

            // HTTP keepalive request template
            // User-Agent mimics the Hutch OneApp so the server responds normally
            string httpRequest =
                $"GET / HTTP/1.1\r\n"                    +
                $"Host: {host}\r\n"                      +
                $"Connection: keep-alive\r\n"            +
                $"User-Agent: HutchOneApp/3.0 Android\r\n" +
                $"Accept: */*\r\n"                       +
                $"\r\n";

            byte[] requestBytes  = Encoding.UTF8.GetBytes(httpRequest);
            byte[] responseBuffer = new byte[8192];

            // ── Step 4: Keepalive Loop ──────────────────────────────────────
            while (!token.IsCancellationRequested && tcp.Connected)
            {
                // Send HTTP request to keep TLS session alive
                await ssl.WriteAsync(requestBytes, 0, requestBytes.Length, token);
                await ssl.FlushAsync(token);

                _totalBytesSent += requestBytes.Length;
                Dispatcher.Invoke(UpdateBytesLabel);

                // Drain the server response (read up to 3 s)
                // We don't use the response — we just need to empty the buffer
                // so the server doesn't close the connection
                try
                {
                    using var readCts =
                        CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(3_000);

                    int bytesRead = await ssl.ReadAsync(
                        responseBuffer, 0, responseBuffer.Length, readCts.Token);

                    if (bytesRead == 0)
                    {
                        // Server closed the connection cleanly — force reconnect
                        throw new IOException("Server closed connection.");
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // Read timeout is fine — server is just silent
                }

                // Wait for next keepalive cycle
                int interval = GetSelectedInterval() * 1_000;
                await Task.Delay(interval, token);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stop
        // ══════════════════════════════════════════════════════════════════════

        private void StopConnection()
        {
            try { _cts?.Cancel(); }
            catch { /* ignore */ }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  UI State Helpers
        // ══════════════════════════════════════════════════════════════════════

        private void SetUIConnecting()
        {
            _isRunning              = true;
            ActionBtn.IsEnabled     = false;
            ActionBtn.Content       = "Connecting...";
            ActionBtn.Background    = MakeBrush("#F59E0B");
            HostTextBox.IsEnabled   = false;
            IntervalCombo.IsEnabled = false;
            LoadIndicator.Visibility = Visibility.Visible;
            SetStatus("Connecting...", "#F59E0B");
        }

        private void SetUIConnected(string host)
        {
            ActionBtn.Content    = "DISCONNECT";
            ActionBtn.Background = MakeBrush("#DC2626");
            SetStatus($"Connected → {host}", "#22C55E");
        }

        private void SetUIDisconnected()
        {
            _isRunning              = false;
            ActionBtn.IsEnabled     = true;
            ActionBtn.Content       = "START";
            ActionBtn.Background    = MakeBrush("#16A34A");
            HostTextBox.IsEnabled   = true;
            IntervalCombo.IsEnabled = true;
            LoadIndicator.Visibility = Visibility.Hidden;
            SetStatus("Disconnected", "#64748B");
            BytesLabel.Text         = "Data Sent: —";
        }

        private void SetStatus(string message, string hexColor)
        {
            StatusText.Text       = $"Status: {message}";
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

        // ══════════════════════════════════════════════════════════════════════
        //  Auto-Start (Windows Registry)
        // ══════════════════════════════════════════════════════════════════════

        private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using RegistryKey rk = Registry.CurrentUser.OpenSubKey(runKey, true)!;

                if (AutoStartCheckBox.IsChecked == true)
                {
                    string exe = System.Diagnostics.Process
                        .GetCurrentProcess().MainModule!.FileName;
                    rk.SetValue(APP_NAME, exe);
                }
                else
                {
                    rk.DeleteValue(APP_NAME, false);
                }
            }
            catch { /* Registry access failed — non-critical */ }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Settings  (saved to Registry)
        // ══════════════════════════════════════════════════════════════════════

        private void LoadSettings()
        {
            try
            {
                // Auto-start checkbox state
                const string runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                using RegistryKey? run = Registry.CurrentUser.OpenSubKey(runKey);
                AutoStartCheckBox.IsChecked = run?.GetValue(APP_NAME) != null;

                // Host + interval
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
                IntervalCombo.SelectedIndex = 1;   // default: 10 seconds
            }
        }

        private void SaveSettings()
        {
            try
            {
                using RegistryKey s = Registry.CurrentUser.CreateSubKey(REG_PATH);
                s.SetValue("Host",     HostTextBox.Text);
                s.SetValue("Interval", IntervalCombo.SelectedIndex);
            }
            catch { }
        }
    }
}
