using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Node.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Heleus.Node
{
    public class Host : Base.ILogger
    {
        IHost _nodeHost;
        IHost _serviceHost;

		public Uri LocalEndpoint { get; }

        public bool EnableRemoteServices { get => RemoteServiceEndPoint != null; }
		public Uri RemoteServiceEndPoint { get; }

        public bool IsPublicEndPoint { get => PublicEndPoint != null; }
		public Uri PublicEndPoint { get; }

        public string LogName => GetType().Name;

        public Host(NodeConfig config)
        {
            var node = config.Node;
            var host = node.Host;
            var port = node.Port;

            if(node.IsPublic && !string.IsNullOrWhiteSpace(node.PublicEndpoint))
                PublicEndPoint = new Uri(node.PublicEndpoint);

            if (string.IsNullOrEmpty(host))
            {
                try
                {
                    var addresses = Dns.GetHostAddresses(Dns.GetHostName());
					var ips = new List<string>();
                    foreach (var a in addresses)
                    {
						if (a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6)
                        {
							ips.Add(a.ToString());
                            if (string.IsNullOrEmpty(host))
                                host = a.ToString();
                        }
                    }
                    if(ips.Count > 0)
                        Log.Debug($"Available IPs {string.Join(", ", ips)}.");

					if (ips.Count == 0)
                    {
                        Log.Warn("No valid network device found or no host set in the config.");
                        host = "0.0.0.0";
                    }
					else if (ips.Count > 1)
                    {
                        Log.Info("More than one network device found. Using the IP from the first. To use another, please set the host manually in the config.");
                    }
                }
                catch (Exception ex)
                {
                    Log.IgnoreException(ex);
                }
            }

            LocalEndpoint = new Uri("http://" + host + ":" + port + "/");

			if (config.Node.EnableRemoteService)
                RemoteServiceEndPoint = new Uri("http://" + host + ":" + config.Node.RemoteServicePort + "/");
        }

        static IHost BuildHost<T>(Node node, Uri endPoint)
        {
            var builder = new HostBuilder()
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton(node);
                })
                .ConfigureLogging((context, loggingBuilder) =>
                {
                    //loggingBuilder.AddConsole();
                    loggingBuilder.AddDebug();
                    loggingBuilder.SetMinimumLevel(LogLevel.Warning);
                })
                .ConfigureWebHostDefaults((webBuilder) =>
                {
                    webBuilder.UseSetting(WebHostDefaults.ApplicationKey, typeof(T).GetTypeInfo().Assembly.FullName);
                    webBuilder.UseUrls(endPoint.AbsoluteUri);
                    webBuilder.UseKestrel();

                    if (Program.IsRunningOnNetCore)
                        webBuilder.UseLibuv();

                    webBuilder.UseStartup(typeof(T));
                });

            return builder.Build();
        }

        public async Task Start(Node node)
        {
            if (_nodeHost != null)
                await _nodeHost.StopAsync();

            Log.Write($"Local endpoint '{LocalEndpoint}'.");
            if (IsPublicEndPoint)
                Log.Write($"Public endpoint '{PublicEndPoint}'.");

            _nodeHost = BuildHost<NodeStartup>(node, LocalEndpoint);

            await _nodeHost.StartAsync();

            if (EnableRemoteServices)
            {
                _serviceHost = BuildHost<ServiceStartup>(node, RemoteServiceEndPoint);

                await _serviceHost.StartAsync();

                Log.Write($"Remote service endpoint '{new Uri(RemoteServiceEndPoint, "serviceconnection").ToString().Replace("http:", "ws:")}'.");
            }
        }

        public async Task Stop()
        {
            if(_nodeHost != null)
                await _nodeHost.StopAsync();
            _nodeHost = null;

            if (_serviceHost != null)
                await _serviceHost.StopAsync();
            _serviceHost = null;
        }
    }
}
