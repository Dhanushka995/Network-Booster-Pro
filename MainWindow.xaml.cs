using System;
using System.Net;
using System.Net.Sockets;
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
        private const string APP_NAME = "NetworkBoosterPro";
        private const string REG_PATH = @"Software\NetworkBoosterPro";
        private const int PROXY_PORT = 8080;

        private readonly string[] _throttledHosts = new string[]
        {
            "youtube.com", "www.youtube.com", "googlevideo.com", "ytimg.com",
            "netflix.com", "facebook.com", "twitter.com", "instagram.com"
        };

        private bool _isRunning = false;
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
                Icon = System.Drawing.SystemIcons.Shield,
                Text = "Network Booster Pro",
                Visible = false
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (s, e) => ShowMainWindow());
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) => { StopProxy(); Application.Current.Shutdown(); });
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
                    _trayIcon.ShowBalloonTip(2000, "Network Booster Pro",
                        _isRunning ? "Proxy running — SNI splitting active!" : "Running in background.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopProxy();
            _trayIcon?.Dispose();
        }

        private async void ActionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
            {
                _cts = new CancellationTokenSource();
                _isRunning = true;
                SetUIConnecting();
                try
                {
                    SetWindowsProxy(true);
                    // Start proxy server in background (don't await)
                    _ = Task.Run(() => StartProxyServer(_cts.Token), _cts.Token);
                    SetUIConnected("SNI Splitting Proxy");
                }
                catch (Exception ex)
                {
                    SetStatus($"Error: {ex.Message}", "#EF4444");
                    _isRunning = false;
                    SetUIDisconnected();
                }
            }
            else
            {
                StopProxy();
                SetUIDisconnected();
            }
        }

        private void StartProxyServer(CancellationToken token)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, PROXY_PORT);
            listener.Start();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    _ = Task.Run(() => HandleClient(client, token), token);
                }
                catch (SocketException)
                {
                    // listener stopped
                    break;
                }
            }
            listener.Stop();
        }

        private async Task HandleClient(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                NetworkStream clientStream = client.GetStream();
                byte[] buffer = new byte[8192];
                int bytesRead = await clientStream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead <= 0) return;

                string request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;

                string[] parts = lines[0].Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0];
                string target = parts[1];

                if (method == "CONNECT")
                {
                    string[] hostPort = target.Split(':');
                    string remoteHost = hostPort[0];
                    int remotePort = hostPort.Length > 1 ? int.Parse(hostPort[1]) : 443;

                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                    await clientStream.WriteAsync(response, 0, response.Length, token);

                    using TcpClient remote = new TcpClient();
                    await remote.ConnectAsync(remoteHost, remotePort, token);
                    NetworkStream remoteStream = remote.GetStream();

                    if (IsThrottledHost(remoteHost))
                    {
                        await HandleSniSplitting(clientStream, remoteStream, token);
                    }
                    else
                    {
                        await Task.WhenAll(
                            clientStream.CopyToAsync(remoteStream, token),
                            remoteStream.CopyToAsync(clientStream, token)
                        );
                    }
                }
                else
                {
                    // Plain HTTP (rarely used)
                    using TcpClient remote = new TcpClient();
                    Uri uri = new Uri(target);
                    await remote.ConnectAsync(uri.Host, uri.Port != 80 ? uri.Port : 80, token);
                    NetworkStream remoteStream = remote.GetStream();
                    byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                    await remoteStream.WriteAsync(requestBytes, 0, requestBytes.Length, token);
                    await Task.WhenAll(
                        clientStream.CopyToAsync(remoteStream, token),
                        remoteStream.CopyToAsync(clientStream, token)
                    );
                }
            }
        }

        private bool IsThrottledHost(string host)
        {
            foreach (var h in _throttledHosts)
            {
                if (host.Contains(h, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private async Task HandleSniSplitting(NetworkStream clientStream, NetworkStream remoteStream, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            int bytesRead = await clientStream.ReadAsync(buffer, 0, buffer.Length, token);
            if (bytesRead <= 0) return;

            if (buffer[0] != 0x16)
            {
                await remoteStream.WriteAsync(buffer, 0, bytesRead, token);
                await clientStream.CopyToAsync(remoteStream, token);
                await remoteStream.CopyToAsync(clientStream, token);
                return;
            }

            // Split ClientHello: send first byte, wait, send rest
            await remoteStream.WriteAsync(buffer, 0, 1, token);
            await Task.Delay(10, token);
            await remoteStream.WriteAsync(buffer, 1, bytesRead - 1, token);

            await Task.WhenAll(
                clientStream.CopyToAsync(remoteStream, token),
                remoteStream.CopyToAsync(clientStream, token)
            );
        }

        private void StopProxy()
        {
            try
            {
                SetWindowsProxy(false);
                _cts?.Cancel();
            }
            catch { }
            _isRunning = false;
        }

        private void SetWindowsProxy(bool enable)
        {
            const string proxyServer = "127.0.0.1:8080";
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (key == null) return;

            if (enable)
            {
                key.SetValue("ProxyEnable", 1);
                key.SetValue("ProxyServer", proxyServer);
                key.SetValue("ProxyOverride", "localhost;127.0.0.1;*.local;<local>");
            }
            else
            {
                key.SetValue("ProxyEnable", 0);
                key.DeleteValue("ProxyServer", false);
            }
            NativeMethods.InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            NativeMethods.InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }

        private void SetUIConnecting()
        {
            ActionBtn.IsEnabled = false;
            ActionBtn.Content = "Starting...";
            ActionBtn.Background = MakeBrush("#F59E0B");
            LoadIndicator.Visibility = Visibility.Visible;
            SetStatus("Starting proxy...", "#F59E0B");
        }

        private void SetUIConnected(string text)
        {
            ActionBtn.Content = "DISCONNECT";
            ActionBtn.Background = MakeBrush("#DC2626");
            ActionBtn.IsEnabled = true;
            LoadIndicator.Visibility = Visibility.Hidden;
            SetStatus($"Active: {text}", "#22C55E");
        }

        private void SetUIDisconnected()
        {
            ActionBtn.Content = "START";
            ActionBtn.Background = MakeBrush("#16A34A");
            ActionBtn.IsEnabled = true;
            LoadIndicator.Visibility = Visibility.Hidden;
            SetStatus("Disconnected", "#64748B");
        }

        private void SetStatus(string message, string hexColor)
        {
            StatusText.Text = $"Status: {message}";
            StatusText.Foreground = MakeBrush(hexColor);
        }

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
            }
            catch { }
        }

        private void SaveSettings()
        {
            // Not used in proxy version
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    }
}
