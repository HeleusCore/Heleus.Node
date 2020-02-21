using System;
using Heleus.Base;
using Heleus.Transactions;

namespace Heleus.Network.Client
{
    public class HeleusClientUserResult
    {
        public readonly TransactionResultTypes Result;
        public readonly long UserCode;

        public HeleusClientUserResult(TransactionResultTypes resultType, long userCode)
        {
            Result = resultType;
            UserCode = userCode;
        }

        public HeleusClientUserResult(Unpacker unpacker)
        {
            Result = (TransactionResultTypes)unpacker.UnpackShort();
            unpacker.Unpack(out UserCode);
        }

        public void Pack(Packer packer)
        {
            packer.Pack((short)Result);
            packer.Pack(UserCode);
        }
    }
}
