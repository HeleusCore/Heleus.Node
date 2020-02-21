using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Heleus.Base;
using Heleus.Chain.Core;
using Heleus.Chain.Purchases;
using Heleus.Cryptography;
using Heleus.Node;
using Heleus.Operations;
using Heleus.Transactions;

namespace Heleus.Chain.Blocks
{
    class GenesisService
    {
        public bool Valid => Name != null && Title != null && Website != null && Endpoint != null && AccountId > 0;

        public string Name = null;
        public string Title = null;
        public string Website = null;
        public string Endpoint = null;

        public long AccountId = 0;
        public string AccountName = null;
        public int Accounts = 0;
        public int DataChainCount = 1;

        public int Revenue = 0;
        public int RevenueAccountFactor = 0;
    }

    public static class GenesisBlock
    {
        class NetworkKey
        {
            public PublicChainKey PublicKey;
            public ChainKeyStore Store;
            public Key Key;
            public string Password;
        }

        static string GetKeyName(PublicChainKeyFlags keyFlags, bool coreChain)
        {
            if ((keyFlags & PublicChainKeyFlags.ChainAdminKey) != 0)
                return "Chain Admin";

            if (coreChain)
            {
                if ((keyFlags & PublicChainKeyFlags.CoreChainKey) != 0)
                {
                    if((keyFlags & PublicChainKeyFlags.CoreChainVoteKey) != 0)
                        return "Chain Vote";

                    return "Chain";
                }
            }

            if ((keyFlags & PublicChainKeyFlags.ServiceChainKey) != 0)
            {
                if((keyFlags & PublicChainKeyFlags.ServiceChainVoteKey) != 0)
                    return "Service Chain Vote";
                return "Service Chain";
            }

            if ((keyFlags & PublicChainKeyFlags.DataChainKey) != 0)
            {
                if ((keyFlags & PublicChainKeyFlags.DataChainVoteKey) != 0)
                    return "Data Chain Vote";

                return "Data Chain";
            }

            throw new Exception();
        }

        static PublicChainKeyFlags GetCoreKeyFlags(int index)
        {
            if (index == 0)
                return PublicChainKeyFlags.ChainAdminKey;

            if (index == 1)
                return PublicChainKeyFlags.CoreChainKey | PublicChainKeyFlags.CoreChainVoteKey;

            if (index == 2)
                return PublicChainKeyFlags.CoreChainKey;

            throw new Exception();
        }

        static PublicChainKeyFlags GetServiceKeyFlags(int index)
        {
            if (index == 0)
                return PublicChainKeyFlags.ChainAdminKey;

            if (index == 1)
                return PublicChainKeyFlags.ServiceChainKey | PublicChainKeyFlags.ServiceChainVoteKey;

            return PublicChainKeyFlags.DataChainKey | PublicChainKeyFlags.DataChainVoteKey;
        }

        static List<string> GetLocalIPV4Addresss()
        {
            var result = new List<string>();

            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach(var i in interfaces)
            {
                if (i.NetworkInterfaceType == NetworkInterfaceType.Ethernet && i.OperationalStatus == OperationalStatus.Up)
                {
                    foreach (var address in i.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            result.Add(address.Address.ToString());
                            return result;
                        }
                    }
                }
            }

            return result;
        }

        public static GenesisBlockResult Generate(Base.Storage storage)
        {
            (var networkKeyStore, var networkKey, _) = LoadCoreAccountKey(storage, "Network Account", "Heleus Core");

            if (networkKeyStore == null)
            {
                networkKey = Key.Generate(KeyTypes.Ed25519);
                var networkPassword = Hex.ToString(Rand.NextSeed(32));
                networkKeyStore = new CoreAccountKeyStore("Heleus Core Network Account", CoreAccount.NetworkAccountId, networkKey, networkPassword);

                SaveCoreAccountKey(storage, networkKeyStore, "Network Account", networkPassword, "Heleus Core");
            }

            Console.WriteLine($"Heleus Core Network Account Key: {networkKey.PublicKey.HexString}.");

            var networkKeys = new List<NetworkKey>();
            var networkChainKeys = new List<PublicChainKey>();
            for (var i = 0; i < 3; i++)
            {
                var keyFlags = GetCoreKeyFlags(i);
                var keyName = $"Network {GetKeyName(keyFlags, true)}";

                (var store, var key, var storePassword) = LoadKeyStore<ChainKeyStore>(storage, null, keyName, "Heleus Core");

                if (store == null)
                {
                    key = Key.Generate(KeyTypes.Ed25519);
                    storePassword = Hex.ToString(Rand.NextSeed(32));

                    var publicChainKey = new PublicChainKey(keyFlags, CoreChain.CoreChainId, 0, 0, (short)i, key);
                    store = new ChainKeyStore($"Heleus Core {keyName}", publicChainKey, key, storePassword);

                    SaveKeyStore(storage, store, storePassword, null, keyName, "Heleus Core");
                }

                Console.WriteLine($"Heleus Core {keyName}: {key.PublicKey.HexString}.");

                networkKeys.Add(new NetworkKey { Key = key, Password = storePassword, PublicKey = store.PublicChainKey, Store = store });
                networkChainKeys.Add(store.PublicChainKey);
            }

            var timestamp = Time.Timestamp;
            timestamp = Protocol.GenesisTime;

            var coreOperations = new List<CoreOperation>();

            var endPoints = new List<string>();

            Console.WriteLine("Type the core chain name (default: Heleus Core)");
            var name = Program.IsDebugging ? "Heleus Core" : Console.ReadLine();
            if (string.IsNullOrWhiteSpace(name))
                name = "Heleus Core";
            Console.WriteLine($"Chain name: {name}");

            Console.WriteLine("Type the core chain website (default: https://heleuscore.com)");
            var website = Program.IsDebugging ? "https://heleuscore.com" : Console.ReadLine();
            if (string.IsNullOrWhiteSpace(website))
                website = "https://heleuscore.com";
            Console.WriteLine($"Chain Website: {website}");

            Console.WriteLine("Type the core chain endpoints. (none for none, default: https://heleusnode.heleuscore.com)");
            while (true)
            {
                if (Program.IsDebugging)
                {
                    var ips = GetLocalIPV4Addresss();
                    foreach (var ip in ips)
                    {
                        endPoints.Add($"http://{ip}:54321/");
                    }
                    break;
                }

                var ep = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(ep))
                {
                    if (endPoints.Count == 0)
                    {
                        endPoints.Add("https://heleusnode.heleuscore.com");
                    }
                    break;
                }

                if (ep.IsValdiUrl(false))
                {
                    Console.WriteLine($"Added endpoint {ep}");
                    endPoints.Add(ep);
                }

                if (ep == "none")
                    break;
            }

            Console.WriteLine($"Chain Endpoints: {string.Join(", ", endPoints)}");

            var useGenesisInfo = false;
            var useCoreChainEndpoint = false;

            if (storage.FileExists("genesisservices.txt"))
            {
                Console.WriteLine("Process genesisservices.txt [yes/no] (default: yes)");

                var p = Program.IsDebugging ? "yes" : Console.ReadLine();
                if (string.IsNullOrWhiteSpace(p) || p.ToLower() == "yes")
                {
                    Console.WriteLine("Processing genesisservices.txt");

                    useGenesisInfo = true;

                    if (endPoints.Count > 0)
                    {
                        Console.WriteLine("Use first core chain endpoint, not endpoint from genesisservices.txt [yes/no] (default: no)");

                        var p2 = Program.IsDebugging ? "yes" : Console.ReadLine()?.ToLower();

                        if (p2 == "yes")
                        {
                            useCoreChainEndpoint = true;
                            Console.WriteLine($"Using endpoint {endPoints[0]} for all services from genesisservices.txt");
                        }
                    }
                }
            }

            coreOperations.Add((ChainInfoOperation)new ChainInfoOperation(CoreChain.CoreChainId, CoreAccount.NetworkAccountId, name, website, timestamp, networkChainKeys, endPoints, new List<PurchaseInfo>()).UpdateOperationId(Operation.FirstTransactionId));
            coreOperations.Add((AccountOperation)new AccountOperation(CoreAccount.NetworkAccountId, networkKey.PublicKey, timestamp).UpdateOperationId(coreOperations.Count + 1)); // network account
            coreOperations.Add(((AccountUpdateOperation)new AccountUpdateOperation().UpdateOperationId(Operation.FirstTransactionId + 2)).AddAccount(CoreAccount.NetworkAccountId, coreOperations.Count + 1, long.MaxValue / 2).AddTransfer(CoreAccount.NetworkAccountId, CoreAccount.NetworkAccountId, long.MaxValue / 2, null, timestamp));

            var blockStateOperation = new BlockStateOperation();

            var nextChainId = CoreChain.CoreChainId + 1;
            var nextTransactionId = coreOperations.Count + 1;
            var nextAccountId = CoreAccount.NetworkAccountId + 1;

            var accounts = new List<AccountOperation>();
            var serviceTransactionsList = new Dictionary<int, List<ServiceTransaction>>();

            if (useGenesisInfo)
            {
                try
                {
                    var json = storage.ReadFileText("genesisservices.txt");
                    if (json != null)
                    {
                        var services = Newtonsoft.Json.JsonConvert.DeserializeObject<List<GenesisService>>(json);
                        if (services != null)
                        {
                            foreach (var service in services)
                            {
                                if (!service.Valid)
                                    continue;

                                var serviceKeys = new List<PublicChainKey>();
                                var ep = useCoreChainEndpoint ? endPoints[0] : service.Endpoint;
                                var accountId = service.AccountId;

                                if (accountId != CoreAccount.NetworkAccountId)
                                {
                                    var found = accounts.Find((a) => a.AccountId == accountId) != null;
                                    if (!found)
                                    {
                                        (var store, var key, var accountPassword) = LoadCoreAccountKey(storage, "Core Account " + accountId, "Core Accounts");
                                        if (store == null)
                                        {
                                            key = Key.Generate(KeyTypes.Ed25519);
                                            accountPassword = Hex.ToString(Rand.NextSeed(32));
                                            store = new CoreAccountKeyStore(service.AccountName, accountId, key, accountPassword);

                                            SaveCoreAccountKey(storage, store, "Core Account " + accountId, accountPassword, "Core Accounts");
                                        }

                                        if (accountId == nextAccountId)
                                        {
                                            var ao = new AccountOperation(accountId, key.PublicKey, timestamp);
                                            ao.UpdateOperationId(nextTransactionId);

                                            coreOperations.Add(ao);
                                            accounts.Add(ao);

                                            nextTransactionId++;
                                            nextAccountId++;
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Invalid account id for {service.Title}, should be {nextAccountId}, but is {accountId}.");
                                            continue;
                                        }

                                    }
                                }

                                Console.WriteLine($"Adding Service {service.Title} with endpoint {ep}.");

                                var count = 2 + service.DataChainCount;
                                for (var i = 0; i < count; i++)
                                {
                                    var keyFlags = GetServiceKeyFlags(i);
                                    var keyName = $"{service.Name} {GetKeyName(keyFlags, false)}";
                                    var dataChain = i >= 2;
                                    if (dataChain)
                                        keyName += $" (ChainIndex {i - 2})";

                                    (var store, var key, var servicePassword) = LoadKeyStore<ChainKeyStore>(storage, null, keyName, service.Name);

                                    if (store == null)
                                    {
                                        key = Key.Generate(KeyTypes.Ed25519);
                                        servicePassword = Hex.ToString(Rand.NextSeed(32));

                                        var signedKey = new PublicChainKey(keyFlags, nextChainId, dataChain ? (uint)i - 2 : 0, 0, (short)i, key);
                                        store = new ChainKeyStore($"{service.Name} {keyName}", signedKey, key, servicePassword);

                                        SaveKeyStore(storage, store, servicePassword, null, keyName, service.Name);
                                    }

                                    Console.WriteLine($"{service.Name} {keyName}: {key.PublicKey.HexString}.");

                                    serviceKeys.Add(store.PublicChainKey);
                                }

                                var pc = new ChainInfoOperation(nextChainId, accountId, service.Title, service.Website, timestamp, serviceKeys, new List<string> { ep }, new List<PurchaseInfo>());
                                pc.UpdateOperationId(nextTransactionId);
                                coreOperations.Add(pc);

                                nextTransactionId++;

                                if(service.Revenue > 0)
                                {
                                    var rev = new ChainRevenueInfoOperation(nextChainId, service.Revenue, service.RevenueAccountFactor, timestamp);
                                    rev.UpdateOperationId(nextTransactionId);
                                    coreOperations.Add(rev);
                                    nextTransactionId++;
                                }

                                if (service.Accounts > 0)
                                {
                                    var serviceTransactions = new List<ServiceTransaction>();
                                    for (var i = 0; i < service.Accounts; i++)
                                    {
                                        (var accountStore, var accountKey, var accountPassword) = LoadCoreAccountKey(storage, "Core Account " + nextAccountId, "Core Accounts");
                                        if (accountStore == null)
                                        {
                                            accountKey = Key.Generate(KeyTypes.Ed25519);
                                            accountPassword = Hex.ToString(Rand.NextSeed(32));
                                            accountStore = new CoreAccountKeyStore($"Core Account {nextAccountId}", nextAccountId, accountKey, accountPassword);

                                            SaveCoreAccountKey(storage, accountStore, "Core Account " + nextAccountId, accountPassword, "Core Accounts");
                                        }

                                        (var serviceAccountStore, var serviceAccountKey, var serviceAccountPassword) = LoadKeyStore<ServiceAccountKeyStore>(storage, null, $"{service.Name} Service Account {nextAccountId}", $"{service.Name}/Service Accounts");
                                        if (serviceAccountStore == null)
                                        {
                                            serviceAccountKey = Key.Generate(KeyTypes.Ed25519);
                                            serviceAccountPassword = Hex.ToString(Rand.NextSeed(32));

                                            var signedPublicKey = PublicServiceAccountKey.GenerateSignedPublicKey(nextAccountId, nextChainId, 0, 0, serviceAccountKey.PublicKey, accountKey);
                                            serviceAccountStore = new ServiceAccountKeyStore($"{service.Name} Service Account {nextAccountId}", signedPublicKey, serviceAccountKey, serviceAccountPassword);

                                            SaveKeyStore(storage, serviceAccountStore, serviceAccountPassword, null, $"{service.Name} Service Account {nextAccountId}", $"{service.Name}/Service Accounts");
                                        }

                                        var join = new JoinServiceTransaction(serviceAccountStore.SignedPublicKey) { SignKey = accountKey };
                                        join.ToArray();
                                        serviceTransactions.Add(join);

                                        var ao = new AccountOperation(nextAccountId, accountKey.PublicKey, timestamp);
                                        ao.UpdateOperationId(nextTransactionId);

                                        coreOperations.Add(ao);
                                        accounts.Add(ao);

                                        nextTransactionId++;
                                        nextAccountId++;

                                    }

                                    blockStateOperation.AddBlockState(nextChainId, Protocol.GenesisBlockId, 0, 0, serviceTransactions.Count);
                                    serviceTransactionsList[nextChainId] = serviceTransactions;
                                }

                                nextChainId++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.HandleException(ex, LogLevels.Error);
                }
            }

            coreOperations.Add(blockStateOperation);
            blockStateOperation.UpdateOperationId(nextTransactionId);
            blockStateOperation.AddBlockState(Protocol.CoreChainId, Protocol.GenesisBlockId, Protocol.GenesisBlockNetworkKeyIssuer, 0, nextTransactionId);

            var block = new CoreBlock(Protocol.GenesisBlockId, Protocol.GenesisBlockNetworkKeyIssuer, 0, timestamp, nextAccountId, nextChainId, Hash.Empty(Protocol.TransactionHashType), Hash.Empty(ValidationOperation.ValidationHashType), coreOperations, new List<CoreTransaction>());

            var signatures = new BlockSignatures(block);
            signatures.AddSignature(block.Issuer, block, networkKey);

            return new GenesisBlockResult(block, signatures, networkKey.PublicKey, networkKeys[1].Store, networkKeys[1].Password, serviceTransactionsList);
        }

        static (CoreAccountKeyStore, Key, string) LoadCoreAccountKey(Base.Storage storage, string name, string path = null)
        {
            try
            {
                var p = "keys/";
                if (path != null)
                    p = $"keys/{path}/";

                //name = name.ToLower().Replace(" ", "");

                var keyStoreData = storage.ReadFileText($"{p}{name} Keystore.txt");
                if (keyStoreData != null)
                {
                    var keyStorage = KeyStore.Restore<CoreAccountKeyStore>(keyStoreData);

                    var password = storage.ReadFileText($"{p}{name} Keystore Password.txt");
                    keyStorage.DecryptKeyAsync(password, true).Wait();

                    return (keyStorage, keyStorage.DecryptedKey, password);
                }
            }
            catch { }

            return (null, null, null);
        }

        static void SaveCoreAccountKey(Base.Storage storage, CoreAccountKeyStore keyStore, string name, string password, string path = null)
        {
            var p = "keys/";
            if (path != null)
                p = $"keys/{path}/";

            storage.CreateDirectory(p);

            storage.WriteFileText($"{p}/{name} Keystore.txt", $"{name}|{keyStore.HexString}");
            storage.WriteFileText($"{p}/{name} Keystore Password.txt", password);
        }

        static string GetKeyStoreName(string service, string keyName)
        {
            if(service != null)
                return $"{service} {keyName}";

            return keyName;
        }

        static (T, Key, string) LoadKeyStore<T>(Base.Storage storage, string service, string keyName, string path = null) where T : KeyStore
        {
            try
            {
                var p = "keys/";
                if (path != null)
                    p = $"keys/{path}/";

                var name = GetKeyStoreName(service, keyName);

                var keyStoreData = storage.ReadFileText($"{p}{name} Keystore.txt");
                if (keyStoreData != null)
                {
                    var keyStorage = KeyStore.Restore<T>(keyStoreData);

                    var password = storage.ReadFileText($"{p}{name} Keystore Password.txt");

                    keyStorage.DecryptKeyAsync(password, true).Wait();

                    return (keyStorage, keyStorage.DecryptedKey, password);
                }
            }
            catch (Exception ex)
            {
                Log.HandleException(ex);
            }

            return (null, null, null);
        }

        static void SaveKeyStore(Base.Storage storage, KeyStore keyStore, string password, string service, string keyName, string path = null)
        {
            var p = "keys/";
            if (path != null)
                p = $"keys/{path}/";

            var name = GetKeyStoreName(service, keyName);

            storage.CreateDirectory(p);

            storage.WriteFileText($"{p}{name} Keystore.txt", $"{keyName}|{keyStore.HexString}");
            storage.WriteFileText($"{p}{name} Keystore Password.txt", password);
        }
    }
}
