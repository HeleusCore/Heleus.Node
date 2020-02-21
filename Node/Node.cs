using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Chain.Blocks;
using Heleus.Manager;
using Heleus.Network.Client;
using Heleus.Network.Kademlia;
using Heleus.Network.Server;
using Heleus.Node.Configuration;

namespace Heleus.Node
{
    public class Node : ILogger
    {
        //public static readonly Node Current = new Node();
        CancellationTokenSource _quitToken;

        public CancellationToken QuitToken => _quitToken.Token;
        public bool HasQuit => _quitToken.IsCancellationRequested;

        public PubSub PubSub { get; private set; }
        public Storage Storage { get; private set; }

        public NodeConfiguration NodeConfiguration { get; private set; }
        public Host Host { get; private set; }
        public Kademlia Kademlia { get; private set; }
        public CouncilManager CouncilManager { get; private set; }
        public TransactionManager TransactionManager { get; private set; }
        public NodeServer NodeServer { get; private set; }
        public ClientServer ClientServer { get; private set; }
        public ServiceServer ServiceServer { get; private set; }

        public AttachementManager AttachementManager { get; private set; }
        public ChainManager ChainManager { get; private set; }
        public SyncManager SyncManager { get; private set; }

        public string LogName => GetType().Name;

        void Usage()
        {
            Console.WriteLine("\nUsage: heleusnode [data path] [options]\n" +
                      "\n" +
                      "Options:\n" +
                      "  init\t\t\t Initalize new data path.\n" +
                      "  run\t\t\t Run local node.\n" +
                      "  sync\t\t\t Synchronize and exit.\n" +
                      "  chainconfig\t\t Add empty chain config.\n" +
                      "  genesis \t\t Generate new genesis block.\n" +
                      "");
        }

        public async Task<bool> Start(string[] args, CancellationTokenSource quiteToken)
        {
            _quitToken = quiteToken;

            if (_quitToken.IsCancellationRequested)
                return false;

            var dataPath = "heleusdata";

            var genesis = false;
            var sync = false;
            var run = false;
            var init = false;
            var newChainConfig = false;

            if (args.Length == 1)
            {
                dataPath = args[0];
                run = true;
            }
            else if (args.Length == 2)
            {
                dataPath = args[0];
                var cmd = args[1];

                if (cmd == "init")
                    init = true;
                else if (cmd == "run")
                    run = true;
                else if (cmd == "sync")
                    sync = true;
                else if (cmd == "chainconfig")
                    newChainConfig = true;
                else if (cmd == "genesis")
                {
                    genesis = true;
                }
                else
                {
                    Usage();
                    return false;
                }
            }
            else
            {
                Usage();
                return false;
            }

            if ((run || sync) && !File.Exists(Path.Combine(dataPath, $"{nameof(NodeConfig).ToLower()}.txt")))
            {
                Usage();
                var dp = new DirectoryInfo(dataPath);
                Log.Error($"Data path {dp.FullName} not initalized.", this);
                return false;
            }

            Storage = new Storage(dataPath);
            if (!Storage.IsWriteable)
            {
                Log.Fatal($"Data path {Storage.Root} is not writeable!", this);
                return false;
            }

            if (genesis)
            {
                Storage.DeleteDirectory("cache");
                Storage.DeleteDirectory("chains");
            }

            PubSub = Log.PubSub = new PubSub();

            Log.Write($"Starting Heleus Node (Version {Program.Version}).");
            Log.Trace($"PID {System.Diagnostics.Process.GetCurrentProcess().Id}");

            Log.Write($"Data path is '{Storage.Root.FullName}'.");

            var config = Config.Load<NodeConfig>(Storage);
            Log.AddIgnoreList(config.LogIgnore);
            Log.LogLevel = config.LogLevel;

            if (Program.IsDebugging)
                Log.LogLevel = LogLevels.Trace;

            if (newChainConfig)
            {
                var chainConfig = Config.Load<ChainConfig>(Storage, true);
                //if (chainConfig.Chains.Count == 0)
                {
                    Log.Write("Chain config generated.");

                    chainConfig.Chains.Add(new ChainConfig.ChainInfo { ChainKeys = new List<ChainConfig.ChainKeyInfo> { new ChainConfig.ChainKeyInfo { ChainKey = string.Empty, ChainKeyPassword = string.Empty, AttachementKey = -1 } } });
                    Config.Save(chainConfig);
                }

                return false;
            }

            if (init)
            {
                Log.Write("Config file generated.");
                return false;
            }

            if (!genesis)
            {
                if (config.NetworkPublicKey.IsNullOrEmpty())
                {
                    Log.Write("Network key not set. Querying beacon nodes.");
                    var beacons = config.BeaconNodes;
                    foreach (var beacon in beacons)
                    {
                        Log.Write($"Querying beacon node {beacon}.");
                        var client = new NodeClient(new Uri(beacon));
                        var nodeInfo = (await client.DownloadNodeInfo()).Data;
                        if (nodeInfo != null)
                        {
                            config.NetworkPublicKey = nodeInfo.NetworkKey.HexString;
                            Config.Save(config);
                            Log.Write($"Network key set to {config.NetworkPublicKey}.");
                            break;
                        }
                    }
                }

                if (config.NetworkPublicKey.IsNullOrEmpty())
                {
                    Log.Write("No valid network key found or set.", this);
                    return false;
                }
            }

            NodeConfiguration = new NodeConfiguration(config, Config.Load<CoreKeyConfig>(Storage, false), Config.Load<ChainConfig>(Storage, false));
            Host = new Host(config);
            AttachementManager = new AttachementManager(this);
            ChainManager = new ChainManager(this);
            if (!await ChainManager.Initalize())
                return false;

            if (genesis)
            {
                var result = GenesisBlock.Generate(Storage);

                var blockData = new BlockData<CoreBlock>(result.Block, result.Signature);
                await ChainManager.Start(false);
                await ChainManager.CoreChain.BlockStorage.StoreBlock(blockData);
                ChainManager.ConsumeBlockData(blockData);

                Log.Write($"Genesis block and keys generated. Network public key: {result.NetworkPublicKey.HexString}.");

                var coreKeyConfig = Config.Load<CoreKeyConfig>(Storage);
                coreKeyConfig.Key = result.NetworkVoteKey.HexString;
                coreKeyConfig.Password = result.NetworkVotePassword;

                config.NetworkPublicKey = result.NetworkPublicKey.HexString;

                Config.Save(config);
                Config.Save(coreKeyConfig);

                await ChainManager.Stop();
                await ChainManager.Start(true);

                if (result.ServiceTransactions.Count > 0)
                {
                    foreach (var serviceTransactions in result.ServiceTransactions)
                    {
                        var chainId = serviceTransactions.Key;
                        var transactions = serviceTransactions.Value;

                        var serviceChain = ChainManager.GetServiceChain(chainId);
                        var maintainChain = ChainManager.GetMaintainChain(chainId);
                        if (serviceChain != null)
                        {
                            var generator = new ServiceBlockGenerator(ChainManager.CoreChain, serviceChain, maintainChain, null);
                            foreach (var transaction in transactions)
                                generator.ConsumeTransaction(transaction);

                            var serviceBlock = generator.GenerateBlock(0, 0);
                            var serviceBlockData = new BlockData<ServiceBlock>(serviceBlock, new BlockSignatures(serviceBlock));
                            await serviceChain.BlockStorage.StoreBlock(serviceBlockData);
                            serviceChain.ConsumeBlockData(serviceBlockData);
                        }
                    }
                }

                await ChainManager.Stop();
                return false;
            }

            SyncManager = new SyncManager(this);
            await SyncManager.Start();
            //if (!await SyncManager.Start())
            //    return false;

            if (sync)
            {
                Log.Write("Sync done.");
                return false;
            }

            AttachementManager.Start();

            Kademlia = new Kademlia(Storage, this);
            TransactionManager = new TransactionManager(this);
            CouncilManager = new CouncilManager(this);

            NodeServer = new NodeServer(this, config.MaxIncomingConnections, config.MaxOutgoingConnectoins);
            ClientServer = new ClientServer(this);

            if (Host.EnableRemoteServices)
                ServiceServer = new ServiceServer();

            await (Host as Host).Start(this);
            return true;
        }

        public async Task<int> Run()
        {
            if (_quitToken.IsCancellationRequested)
                return 0;

            await Kademlia.Start();
            TransactionManager.Start();
            await CouncilManager.Start();
            await NodeServer.Start();
            await ClientServer.Start();
            if (ServiceServer != null)
                await ServiceServer.Start();

            await _quitToken.WaitAsync();
            await PubSub.PublishAsync(new QuitEvent());

            await NodeServer.Stop();
            await ClientServer.Stop();
            await CouncilManager.Stop();
            TransactionManager.Stop();
            await Kademlia.Stop();
            await ChainManager.Stop();

            if (ServiceServer != null)
                await ServiceServer.Stop();

            await (Host as Host).Stop();

            return 0;
        }

        public void Quit()
        {
            if (_quitToken.IsCancellationRequested)
                return;

            _quitToken.Cancel();
        }
    }
}
