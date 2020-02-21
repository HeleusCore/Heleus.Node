using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Cryptography;
using Heleus.Network.Client;
using Heleus.Transactions;
using Heleus.Service;
using Heleus.Chain.Data;

namespace Heleus.Manager
{
    public class AttachementManager : ILogger
    {
        public string LogName => GetType().Name;

        readonly Base.Storage _storage;
        readonly Node.Node _node;
        public readonly string AttachementsTempPath;
        public readonly string AttachementsTempFullPath;

        readonly Dictionary<(int, uint), ChainAttachementsCache> _chainCaches = new Dictionary<(int, uint), ChainAttachementsCache>();

        class ChainAttachementsCache
        {
            public readonly DataChain Chain;

            public readonly LazyLookupTable<long, AttachementInfo> LatestAttachements = new LazyLookupTable<long, AttachementInfo> { LifeSpan = TimeSpan.FromMinutes(5) };

            public readonly string AttachementsPath;
            public readonly string AttachementsFullPath;

            public ChainAttachementsCache(Base.Storage storage, DataChain chain)
            {
                Chain = chain;

                AttachementsPath = GetAttachementPath(chain.ChainId, chain.ChainIndex, chain.AttachementKey);
                AttachementsFullPath = Path.Combine(storage.Root.FullName, AttachementsPath);

                if (!storage.CreateDirectory(AttachementsPath))
                    throw new Exception("Could not create attachements directory.");
            }
        }

        public static string GetAttachementTempFileName(AttachementInfo info, AttachementItem item)
        {
            var attachements = info.Attachements;
            return $"{attachements.TimeStamp}_{attachements.ChainId}_{attachements.ChainIndex}_{attachements.AccountId}_{attachements.Token}_{item.DataSize}_{item.DataHash.HexString}_{item.Name}";
        }

        public static string GetAttachementFileName(long transactionId, string name)
        {
            return $"{transactionId}_{name}";
        }

        public static string GetAttachementPath(int chainId, uint chainIndex, int attachementKey)
        {
            return $"attachements/{chainId}/{chainIndex}/{attachementKey}/";
        }

        public string GetAttachementPath(int chainid, uint chainIndex, int attachementKey, long transactionid, string name)
        {
            return Path.Combine(_storage.Root.FullName, GetAttachementPath(chainid, chainIndex, attachementKey), GetAttachementFileName(transactionid, name));
        }

        public AttachementManager(Node.Node node)
        {
            _storage = node.Storage;
            _node = node;

            AttachementsTempPath = "temp/attachements/";
            AttachementsTempFullPath = Path.Combine(_storage.Root.FullName, AttachementsTempPath);

            if (!_storage.CreateDirectory(AttachementsTempPath))
                throw new Exception("Could not create attachements directory.");
        }

        void AttachmentInfoRemoved(IEnumerable<AttachementInfo> attachements)
        {
            // delete tmp files
            foreach (var info in attachements)
            {
                foreach (var item in info.Attachements.Items)
                {
                    var tempName = GetAttachementTempFileName(info, item);
                    var tempPath = Path.Combine(AttachementsTempPath, tempName);

                    _storage.DeleteFile(tempPath);
                }
            }
        }

        public void Start()
        {
            var files = _storage.GetFiles(AttachementsTempPath, "*.*");
            var items = new Dictionary<long, AttachementInfo>();
            var now = Time.Timestamp;

            foreach (var file in files)
            {
                try
                {
                    var parts = file.Name.Split('_');
                    var timestamp = long.Parse(parts[0]);
                    var chainId = int.Parse(parts[1]);
                    var chainIndex = uint.Parse(parts[2]);
                    var accountId = long.Parse(parts[3]);
                    var token = long.Parse(parts[4]);
                    var size = int.Parse(parts[5]);
                    var hash = Hash.Restore(parts[6]);
                    var name = parts[7];

                    if (!_chainCaches.ContainsKey((chainId, chainIndex)))
                        goto del;

                    if (Time.PassedSeconds(now, timestamp) > Protocol.AttachementsInfoTimeout)
                        goto del;

                    using (var stream = _storage.FileReadStream(Path.Combine(AttachementsTempPath, file.Name)))
                    {
                        var fileHash = Hash.Generate(Protocol.AttachementsHashType, stream);
                        if (fileHash != hash)
                            goto del;
                    }

                    items.TryGetValue(token, out var info);
                    if (info == null)
                    {
                        var attachements = new Attachements(accountId, chainId, chainIndex, timestamp, token);
                        info = new AttachementInfo(attachements);
                        items[token] = info;
                    }

                    info.Attachements.Items.Add(new AttachementItem(name, size, hash));
                    continue;
                }
                catch (Exception ex)
                {
                    Log.IgnoreException(ex, this);
                }

            del:
                _storage.DeleteFile(Path.Combine(AttachementsTempPath, file.Name));
            }

            foreach (var item in items.Values)
            {
                _chainCaches.TryGetValue((item.Attachements.ChainId, item.Attachements.ChainIndex), out var cache);
                cache.LatestAttachements.Add(item.Token, item);
            }
        }

        public void AddChainAttachementsCache(DataChain chain)
        {
            var item = new ChainAttachementsCache(_storage, chain);
            item.LatestAttachements.OnRemove = AttachmentInfoRemoved;
            _chainCaches.Add((chain.ChainId, chain.ChainIndex), item);
            Log.Info($"Data chain {chain.ChainId}/{chain.ChainIndex} (AttachementKey {chain.AttachementKey}) attachements path {item.AttachementsFullPath}.", this);
        }

        public bool AddAttachementsRequests(Attachements attachements)
        {
            _chainCaches.TryGetValue((attachements.ChainId, attachements.ChainIndex), out var cacheItem);
            if (cacheItem == null)
                return false;

            var att = cacheItem.LatestAttachements.TryAdd(attachements.Token, new AttachementInfo(attachements));
            if (att == null)
                return true;

            // check if the item is the same
            return att.IsValid(attachements) && !att.Expired;
        }

        public AttachementInfo GetAttachementInfo(int chainId, uint chainIndex, long token)
        {
            _chainCaches.TryGetValue((chainId, chainIndex), out var cacheItem);
            if (cacheItem == null)
                return null;

            cacheItem.LatestAttachements.TryGetValue(token, out var result);
            return result;
        }

        public bool AreAttachementsUploaded(AttachementDataTransaction transaction)
        {
            if (transaction == null)
                return false;

            _chainCaches.TryGetValue((transaction.TargetChainId, transaction.ChainIndex), out var cache);
            if (cache == null)
                return false;

            cache.LatestAttachements.TryGetValue(transaction.Token, out var info);
            if (info == null)
                return false;

            return info.IsValid(transaction.Items) && info.State == AttachementInfoState.Uploaded;
        }

        public void StoreAttachements(AttachementDataTransaction transaction)
        {
            if (_chainCaches.TryGetValue((transaction.TargetChainId, transaction.ChainIndex), out var cache) && cache.LatestAttachements.TryGetValue(transaction.Token, out var info) && info.IsValid(transaction.Items) && info.State == AttachementInfoState.Uploaded)
            {
                foreach (var item in transaction.Items)
                {
                    var tempFilePath = Path.Combine(AttachementsTempPath, GetAttachementTempFileName(info, item));
                    var targetPath = Path.Combine(cache.AttachementsPath, GetAttachementFileName(transaction.TransactionId, item.Name));

                    _storage.MoveFile(tempFilePath, targetPath, true);
                }

                info.SetState(AttachementInfoState.Done);
            }
        }

        public async Task<HeleusClientUserResult> CheckAndCacheAttachements(AttachementInfo info, List<AttachementTempFile> files)
        {
            var userCode = 0L;
            var attachements = info.Attachements;
            var copied = new List<ServiceAttachementFile>();

            _chainCaches.TryGetValue((attachements.ChainId, attachements.ChainIndex), out var cache);
            if (cache == null)
                return new HeleusClientUserResult(TransactionResultTypes.AttachementsNotAllowed, userCode);

            var service = _node.ChainManager.GetService(attachements.ChainId);
            if (service == null)
                return new HeleusClientUserResult(TransactionResultTypes.ChainServiceUnavailable, userCode);

            try
            {
                foreach (var file in files)
                {
                    var stream = file.Stream;
                    var hash = Hash.Generate(Protocol.AttachementsHashType, stream);

                    stream.Position = 0;
                    var fileName = GetAttachementTempFileName(info, file.Item);

                    using (var tempStream = _storage.FileStream(Path.Combine(AttachementsTempPath, fileName), FileAccess.Write))
                    {
                        await file.Stream.CopyToAsync(tempStream);
                        copied.Add(new ServiceAttachementFile(Path.Combine(AttachementsTempFullPath, fileName), file.Item));
                    }
                }

                var validation = await service.AreAttachementsValid(info.Attachements, copied);
                userCode = validation.UserCode;

                if (validation.IsOK)
                    return new HeleusClientUserResult(TransactionResultTypes.Ok, userCode);
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex);
            }

            // delete
            foreach (var file in copied)
                _storage.DeleteFile(Path.Combine(AttachementsTempPath, file.TempPath));

            return new HeleusClientUserResult(TransactionResultTypes.AttachementsInvalid, userCode);
        }
    }
}