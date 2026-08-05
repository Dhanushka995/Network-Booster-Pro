using System;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace NetworkBooster
{
    public partial class MainWindow : Window
    {
        private ProxyServer proxyServer;
        private bool isRunning = false;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (!isRunning)
            {
                StartProxy();
                isRunning = true;
            }
            else
            {
                StopProxy();
                isRunning = false;
            }
        }

        private void StartProxy()
        {
            proxyServer = new ProxyServer();
            
            proxyServer.BeforeRequest += OnRequest;

            var explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000, true);
            proxyServer.AddEndPoint(explicitEndPoint);
            proxyServer.Start();

            proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
        }

        private void StopProxy()
        {
            if (proxyServer != null)
            {
                proxyServer.Stop();
                proxyServer.Dispose();
                proxyServer = null;
            }
        }

        private async Task OnRequest(object sender, SessionEventArgs e)
        {
            e.HttpClient.Request.Headers.RemoveHeader("Host");
            e.HttpClient.Request.Headers.AddHeader("Host", "oneapp.hutch.lk");
        }
        
        protected override void OnClosed(EventArgs e)
        {
            StopProxy();
            base.OnClosed(e);
        }
    }
}
