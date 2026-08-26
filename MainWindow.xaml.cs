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
        private const string SPOOF_HOST = "selfcare.hutch.lk";

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
                        _isRunning ? "Proxy running — Host spoofing active!" : "Running in background.",
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
                    _ = Task.Run(() => StartProxyServer(_cts.Token), _cts.Token);
                    SetUIConnected("Host Spoofing Proxy");
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

                    // Host spoofing: intercept TLS ClientHello and replace SNI
                    await HandleSniSpoofing(clientStream, remoteStream, remoteHost, token);
                }
                else
                {
                    // Plain HTTP: rewrite Host header
                    using TcpClient remote = new TcpClient();
                    Uri uri = new Uri(target);
                    await remote.ConnectAsync(uri.Host, uri.Port != 80 ? uri.Port : 80, token);
                    NetworkStream remoteStream = remote.GetStream();

                    // Replace Host header with SPOOF_HOST
                    string newRequest = request.Replace($"Host: {uri.Host}", $"Host: {SPOOF_HOST}");
                    byte[] requestBytes = Encoding.ASCII.GetBytes(newRequest);
                    await remoteStream.WriteAsync(requestBytes, 0, requestBytes.Length, token);

                    await Task.WhenAll(
                        clientStream.CopyToAsync(remoteStream, token),
                        remoteStream.CopyToAsync(clientStream, token)
                    );
                }
            }
        }

        private async Task HandleSniSpoofing(NetworkStream clientStream, NetworkStream remoteStream, string remoteHost, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            int bytesRead = await clientStream.ReadAsync(buffer, 0, buffer.Length, token);
            if (bytesRead <= 0) return;

            if (buffer[0] != 0x16) // not TLS handshake
            {
                await remoteStream.WriteAsync(buffer, 0, bytesRead, token);
                await clientStream.CopyToAsync(remoteStream, token);
                await remoteStream.CopyToAsync(clientStream, token);
                return;
            }

            // Simple SNI spoof: replace remoteHost with SPOOF_HOST in ClientHello bytes
            byte[] remoteHostBytes = Encoding.ASCII.GetBytes(remoteHost);
            byte[] spoofHostBytes = Encoding.ASCII.GetBytes(SPOOF_HOST);
            int index = FindBytes(buffer, remoteHostBytes);
            if (index >= 0)
            {
                // Replace bytes
                Array.Copy(spoofHostBytes, 0, buffer, index, spoofHostBytes.Length);
                // Pad with zeros if lengths differ
                for (int i = index + spoofHostBytes.Length; i < index + remoteHostBytes.Length; i++)
                    buffer[i] = 0;
            }

            await remoteStream.WriteAsync(buffer, 0, bytesRead, token);
            await clientStream.CopyToAsync(remoteStream, token);
            await remoteStream.CopyToAsync(clientStream, token);
        }

        private int FindBytes(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0) return -1;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { found = false; break; }
                }
                if (found) return i;
            }
            return -1;
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
        }
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    }
}
