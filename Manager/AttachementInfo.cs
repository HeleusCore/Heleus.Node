using System;
using System.Collections.Generic;
using Heleus.Base;
using Heleus.Cryptography;
using Heleus.Transactions;

namespace Heleus.Manager
{
    public enum AttachementInfoState
    {
        Requested,
        Uploading,
        Uploaded,
        Done
    }

    public class AttachementInfo
    {
        public AttachementInfoState State { get; private set; } = AttachementInfoState.Requested;

        public long Token => Attachements.Token;
        public readonly Attachements Attachements;

        public AttachementInfo(Attachements attachements)
        {
            Attachements = attachements;
        }

        public static bool IsExpired(Attachements attachements)
        {
            return Time.PassedSeconds(attachements.TimeStamp) > Protocol.AttachementsInfoTimeout;
        }

        public bool Expired => IsExpired(Attachements);

        public bool IsValid(Attachements attachements)
        {
            return Attachements.Token == attachements.Token && Attachements.AccountId == attachements.AccountId && Attachements.TimeStamp == attachements.TimeStamp && Attachements.ChainId == attachements.ChainId && Attachements.Items.Count == attachements.Items.Count;
        }

        public bool IsValid(IEnumerable<AttachementItem> items)
        {
            var count = 0;
            foreach (var attachement in Attachements.Items)
            {
                var valid = false;
                foreach (var item in items)
                {
                    if(attachement.Name == item.Name)
                    {
                        if (attachement.DataHash == item.DataHash)
                        {
                            valid = true;
                            count++;
                            break;
                        }

                        if (!valid)
                            return false;
                    }
                }
            }
            return count == Attachements.Items.Count;
        }

        public void SetState(AttachementInfoState state)
        {
            State = state;
        }

        public AttachementItem GetAttachementItem(string fileName)
        {
            foreach(var attachement in Attachements.Items)
            {
                if (attachement.Name == fileName)
                    return attachement;
            }
            return null;
        }

        public bool IsValidFile(string fileName, Hash hash, int size)
        {
            foreach(var attachement in Attachements.Items)
            {
                if (attachement.Name == fileName && attachement.DataHash == hash && attachement.DataSize == size)
                    return true;
            }

            return false;
        }
    }
}
