using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetworkBooster
{
    public partial class MainWindow : Window
    {
        private bool isRunning = false;
        private CancellationTokenSource cts;
        private const string AppName = "NetworkBoosterPro";
        private const string RegPath = @"Software\NetworkBoosterPro";

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private async void ActionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                string host = HostTextBox.Text.Trim();
                if (string.IsNullOrEmpty(host)) return;

                int interval = 10; 
                if (IntervalComboBox.SelectedIndex == 0) interval = 5;
                else if (IntervalComboBox.SelectedIndex == 1) interval = 10;
                else if (IntervalComboBox.SelectedIndex == 2) interval = 15;

                isRunning = true;
                ActionBtn.Content = "DISCONNECT";
                ActionBtn.Background = (Brush)new BrushConverter().ConvertFrom("#C0392B"); 
                StatusText.Text = $"Status: Pinging {host}...";
                StatusText.Foreground = (Brush)new BrushConverter().ConvertFrom("#27AE60"); 

                cts = new CancellationTokenSource();
                SaveSettings(); 

                await Task.Run(() => StartPinging(host, interval, cts.Token));
            }
            else
            {
                isRunning = false;
                cts?.Cancel();
                
                ActionBtn.Content = "START";
                ActionBtn.Background = (Brush)new BrushConverter().ConvertFrom("#27AE60"); 
                StatusText.Text = "Status: Disconnected";
                StatusText.Foreground = (Brush)new BrushConverter().ConvertFrom("#7F8C8D"); 
            }
        }

        private void StartPinging(string host, int intervalSeconds, CancellationToken token)
        {
            Ping pingSender = new Ping();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    pingSender.Send(host, 1000);
                }
                catch { }

                bool cancelled = token.WaitHandle.WaitOne(intervalSeconds * 1000);
                if (cancelled) break;
            }
        }

        private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (AutoStartCheckBox.IsChecked == true)
                {
                    rk.SetValue(AppName, System.Reflection.Assembly.GetExecutingAssembly().Location);
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

                    int savedInterval = (int)settingsKey.GetValue("IntervalIndex", 1);
                    if (savedInterval >= 0 && savedInterval <= 2) IntervalComboBox.SelectedIndex = savedInterval;
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
                settingsKey.SetValue("IntervalIndex", IntervalComboBox.SelectedIndex);
            }
            catch { }
        }
    }
}
