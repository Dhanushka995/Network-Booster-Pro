using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetworkBooster
{
    public partial class MainWindow : Window
    {
        [DllImport("wininet.dll")]
        public static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        private bool isRunning = false;
        private const string AppName = "NetworkBoosterPro";
        private const string RegPath = @"Software\NetworkBoosterPro";
        
        private System.Windows.Forms.NotifyIcon notifyIcon;
        private TcpListener proxyListener;
        private CancellationTokenSource proxyCts;

        public MainWindow()
        {
            InitializeComponent();
            SetupTrayIcon();
            LoadSettings();
            SetSystemProxy(false);
        }

        private void SetupTrayIcon()
        {
            notifyIcon = new System.Windows.Forms.NotifyIcon();
            notifyIcon.Icon = System.Drawing.SystemIcons.Shield;
            notifyIcon.Text = "Network Booster Pro";
            notifyIcon.Visible = false;
            notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
                notifyIcon.Visible = false;
            };
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
                notifyIcon.Visible = true;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopProxy();
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
            }
        }

        private async void ActionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                string host = HostTextBox.Text.Trim();
                if (string.IsNullOrEmpty(host)) return;

                SaveSettings();

                isRunning = true;
                ActionBtn.Content = "DISCONNECT";
                ActionBtn.Background = (Brush)new BrushConverter().ConvertFrom("#C0392B");
                StatusText.Text = $"Status: Connected to {host}";
                StatusText.Foreground = (Brush)new BrushConverter().ConvertFrom("#27AE60");
                LoadIndicator.Visibility = Visibility.Visible;
                HostTextBox.IsEnabled = false;

                await StartProxy(host);
            }
            else
            {
                StopProxy();
                
                isRunning = false;
                ActionBtn.Content = "START";
                ActionBtn.Background = (Brush)new BrushConverter().ConvertFrom("#27AE60");
                StatusText.Text = "Status: Disconnected";
                StatusText.Foreground = (Brush)new BrushConverter().ConvertFrom("#7F8C8D");
                LoadIndicator.Visibility = Visibility.Hidden;
                HostTextBox.IsEnabled = true;
            }
        }

        private void SetSystemProxy(bool enable)
        {
            try
            {
                RegistryKey registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                if (enable)
                {
                    registry.SetValue("ProxyEnable", 1);
                    registry.SetValue("ProxyServer", "127.0.0.1:8080");
                    // මෙතනින් තමයි රවුටර් එකට (192.168.*) යන එක බයිපාස් කරන්නේ (404 Error එක හදන්න)
                    registry.SetValue("ProxyOverride", "<local>;192.168.*;10.*;127.*");
                }
                else
                {
                    registry.SetValue("ProxyEnable", 0);
                }
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch { }
        }

        private async Task StartProxy(string spoofHost)
        {
            proxyCts = new CancellationTokenSource();
            SetSystemProxy(true);

            try
            {
                proxyListener = new TcpListener(IPAddress.Parse("127.0.0.1"), 8080);
                proxyListener.Start();

                while (!proxyCts.Token.IsCancellationRequested)
                {
                    var client = await proxyListener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(client, spoofHost, proxyCts.Token);
                }
            }
            catch { }
        }

        private void StopProxy()
        {
            try
            {
                proxyCts?.Cancel();
                proxyListener?.Stop();
                SetSystemProxy(false);
            }
            catch { }
        }

        private async Task HandleClientAsync(TcpClient browserClient, string spoofHost, CancellationToken token)
        {
            try
            {
                using (browserClient)
                using (var browserStream = browserClient.GetStream())
                {
                    byte[] buffer = new byte[8192];
                    int bytesRead = await browserStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead == 0) return;

                    string requestHeader = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    
                    string targetHost = "";
                    int targetPort = 80;
                    bool isHttps = requestHeader.StartsWith("CONNECT");

                    Match match = Regex.Match(requestHeader, @"Host:\s*([^\r\n]+)");
                    if (match.Success)
                    {
                        string hostLine = match.Groups[1].Value.Trim();
                        if (hostLine.Contains(":"))
                        {
                            var parts = hostLine.Split(':');
                            targetHost = parts[0];
                            int.TryParse(parts[1], out targetPort);
                        }
                        else
                        {
                            targetHost = hostLine;
                            targetPort = isHttps ? 443 : 80;
                        }
                    }
                    else
                    {
                        return;
                    }

                    using (var targetClient = new TcpClient())
                    {
                        // NoDelay දාන්නේ ඩේටා පැකට් එකතු කරන්නේ නැතුව ඉක්මනින් යවන්න (Fragmentation වලට අත්‍යවශ්‍යයි)
                        targetClient.NoDelay = true;
                        browserClient.NoDelay = true;

                        await targetClient.ConnectAsync(targetHost, targetPort);
                        using (var targetStream = targetClient.GetStream())
                        {
                            if (isHttps)
                            {
                                byte[] okResponse = Encoding.UTF8.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                                await browserStream.WriteAsync(okResponse, 0, okResponse.Length, token);

                                // HTTPS සඳහා TCP Fragmentation (DPI Evasion) ක්‍රමය භාවිතා කිරීම
                                var task1 = CopyWithFragmentationAsync(browserStream, targetStream, token);
                                var task2 = targetStream.CopyToAsync(browserStream, 8192, token);
                                await Task.WhenAny(task1, task2);
                            }
                            else
                            {
                                requestHeader = Regex.Replace(requestHeader, @"Host:\s*([^\r\n]+)", $"Host: {spoofHost}");
                                byte[] modifiedRequest = Encoding.UTF8.GetBytes(requestHeader);
                                await targetStream.WriteAsync(modifiedRequest, 0, modifiedRequest.Length, token);

                                var task1 = browserStream.CopyToAsync(targetStream, 8192, token);
                                var task2 = targetStream.CopyToAsync(browserStream, 8192, token);
                                await Task.WhenAny(task1, task2);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // මේක තමයි අලුතින් එකතු කරපු සුපිරිම කෑල්ල (DPI Firewall එක රවට්ටන තැන)
        private async Task CopyWithFragmentationAsync(NetworkStream input, NetworkStream output, CancellationToken token)
        {
            byte[] buffer = new byte[8192];
            bool isFirstPacket = true;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead == 0) break;

                    if (isFirstPacket)
                    {
                        // පළවෙනි පැකට් එක (Client Hello) බයිට් 2න් 2ට කඩලා යවනවා
                        int chunkSize = 2; 
                        for (int i = 0; i < bytesRead; i += chunkSize)
                        {
                            int currentChunkSize = Math.Min(chunkSize, bytesRead - i);
                            await output.WriteAsync(buffer, i, currentChunkSize, token);
                            await output.FlushAsync(token);
                            await Task.Delay(5, token); // පොඩි වෙලාවක් තියනවා පැකට් වෙන් වෙලා යන්න
                        }
                        isFirstPacket = false;
                    }
                    else
                    {
                        await output.WriteAsync(buffer, 0, bytesRead, token);
                    }
                }
            }
            catch { }
        }

        private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (AutoStartCheckBox.IsChecked == true)
                {
                    string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                    rk.SetValue(AppName, exePath);
                }
                else
                {
                    rk.DeleteValue(AppName, false);
                }
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                if (rk != null && rk.GetValue(AppName) != null)
                {
                    AutoStartCheckBox.IsChecked = true;
                }

                RegistryKey settingsKey = Registry.CurrentUser.OpenSubKey(RegPath);
                if (settingsKey != null)
                {
                    string savedHost = settingsKey.GetValue("Host") as string;
                    if (!string.IsNullOrEmpty(savedHost)) HostTextBox.Text = savedHost;
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                RegistryKey settingsKey = Registry.CurrentUser.CreateSubKey(RegPath);
                settingsKey.SetValue("Host", HostTextBox.Text);
            }
            catch { }
        }
    }
}
