using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Data;
using Heleus.Chain.Maintain;
using Heleus.Service;
using Heleus.Transactions.Features;

namespace Heleus.Chain.Service
{
    public class ServiceHost : IServiceHost, IFeatureHost, ILogger
    {
        static List<Type> FindServiceAssemblies(string serviceName, List<DirectoryInfo> directories)
        {
            var result = new List<Type>();

            if (serviceName.IsNullOrEmpty() || directories == null)
                return result;

            foreach (var directory in directories)
            {
                if (!directory.Exists)
                    continue;

                var files = directory.GetFiles();
                var lowerName = serviceName.ToLower();
                foreach (var file in files)
                {
                    if (file.Name.ToLower().Contains(lowerName))
                    {
                        try
                        {
                            var types = Assembly.LoadFile(file.FullName).GetTypes();
                            foreach (var type in types)
                            {
                                if (typeof(IService).IsAssignableFrom(type))
                                    result.Add(type);
                            }
                        }
#pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
                        catch
#pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
                        {
                        }
                    }
                }
            }

            return result;
        }

        public static Type GetServiceType(Base.Storage storage, int chainId, string serviceName, string serviceSearchPath)
        {
            Type serviceType = null;

            if (!serviceName.IsNullOrEmpty())
            {
                var directories = new List<DirectoryInfo>
                            {
                                new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)),
                                new DirectoryInfo(Path.Combine(storage.Root.FullName, "services"))
                            };

                if (!serviceSearchPath.IsNullOrEmpty())
                    directories.Add(new DirectoryInfo(serviceSearchPath));

                var services = FindServiceAssemblies(serviceName, directories);
                if (services.Count == 1)
                {
                    serviceType = services[0];
                }
                else if (services.Count == 0)
                {
                    Log.Warn($"Could not find service {serviceName} for chainid {chainId}.");
                }
                else
                {
                    var text = string.Empty;
                    foreach (var item in services)
                    {
                        text += $"{item.FullName} in {item.Assembly.Location} ";
                    }

                    Log.Warn($"Multiple valid services {serviceName} found for chainid {chainId}, {text}.");
                }
            }

            return serviceType;
        }

        public readonly int ChainId;
        public ServiceInfo ServiceInfo { get; private set; }

        readonly IService _service;
        public IService Service { get; private set; }

        public readonly ServiceOptions ServiceOptions = new ServiceOptions();

        public IServiceUriDataHandler UriHandler { get; private set; }
        public IServiceBlockHandler BlockReceiver { get; private set; }

        public IServiceErrorReportsHandler ErrorReportReceiver { get; private set; }
        public IServiceRemoteMessageHandler ClientMessageReceiver { get; private set; }
        public IServicePushHandler PushReceiver { get; private set; }

        public string LogName => GetType().Name;

        public DataChain GetDataChain(uint chainIndex)
        {
            _dataChains.TryGetValue(chainIndex, out var dataChain);
            return dataChain;
        }

        IServiceChain IServiceHost.ServiceChain => _serviceChain;
        IMaintainChain IServiceHost.MaintainChain => _maintainChain;
        IDataChain IServiceHost.GetDataChain(uint chainIndex) => GetDataChain(chainIndex);
        IServiceRemoteHost IServiceHost.RemoteHost => _node.ClientServer;

        IFeatureChain IFeatureHost.ServiceChain => _serviceChain;
        IFeatureChain IFeatureHost.GetDataChain(uint chainIndex) => GetDataChain(chainIndex);

        public readonly string ServiceDataPath;
        public readonly string FullServiceDataPath;

        readonly Node.Node _node;
        readonly ServiceChain _serviceChain;
        readonly IReadOnlyDictionary<uint, DataChain> _dataChains;
        readonly MaintainChain _maintainChain;
        readonly string _serviceConfigString;

        public ServiceHost(Node.Node node, ServiceChain serviceChain, IReadOnlyDictionary<uint, DataChain> dataChains, MaintainChain maintainChain, Base.Storage storage, int chainId, string serviceName, string serviceSearchPath, string serviceConfigString)
        {
            ChainId = chainId;

            _node = node;
            _serviceChain = serviceChain;
            _dataChains = dataChains;
            _maintainChain = maintainChain;

            var _serviceType = GetServiceType(storage, chainId, serviceName, serviceSearchPath);

            ServiceDataPath = $"servicedata/{chainId}/";
            storage.CreateDirectory(ServiceDataPath);
            FullServiceDataPath = Path.Combine(storage.Root.FullName, ServiceDataPath);

            _serviceConfigString = serviceConfigString;
            if (!string.IsNullOrEmpty(_serviceConfigString))
                _serviceConfigString += ";";
            else
                _serviceConfigString = string.Empty;

            _serviceConfigString += $"{Heleus.Service.ServiceHelper.ServiceDataPathKey}={FullServiceDataPath};";
            _serviceConfigString += $"{Heleus.Service.ServiceHelper.ServiceChainIdKey}={chainId};";

            try
            {
                _service = (IService)Activator.CreateInstance(_serviceType);
                if(_service != null)
                {
                    _service.Initalize(ServiceOptions);
                }
            }
            catch(Exception ex)
            {
                Log.Warn($"Creating service {_serviceType.Name} failed.", this);
                Log.HandleException(ex, this);
            }
        }

        public async Task Start()
        {
            try
            {
                await Stop();

                if (_service != null)
                {
                    var result = await _service.Start(_serviceConfigString, this);
                    if (result.IsOK)
                    {
                        Service = _service;
                        ServiceInfo = new ServiceInfo(result.Message, result.UserCode);

                        UriHandler = Service as IServiceUriDataHandler;
                        BlockReceiver = Service as IServiceBlockHandler;
                        ErrorReportReceiver = Service as IServiceErrorReportsHandler;
                        ClientMessageReceiver = Service as IServiceRemoteMessageHandler;
                        PushReceiver = Service as IServicePushHandler;

                        Log.Info($"Starting service {_service.GetType().Name} for chainid {ChainId}: {result.Message} (v {result.UserCode}).");
                    }
                    else
                        Log.Warn($"Starting service {_service.GetType().Name} failed, {result.Result}: {result.Message} (v {result.UserCode}).");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Starting service {_service.GetType().Name} failed.", this);
                Log.HandleException(ex, this);
            }
        }

        public async Task Stop()
        {
            ServiceInfo = null;

            if (_service != null)
            {
                Service = null;
                UriHandler = null;
                BlockReceiver = null;
                ErrorReportReceiver = null;
                ClientMessageReceiver = null;
                PushReceiver = null;

                try
                {
                    await _service.Stop();
                }
                catch (Exception ex)
                {
                    Log.HandleException(ex, Service as ILogger);
                }
            }
        }
    }
}
