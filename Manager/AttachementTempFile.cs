using System.IO;
using Heleus.Transactions;

namespace Heleus.Manager
{
    public class AttachementTempFile
    {
        public readonly AttachementItem Item;
        public readonly Stream Stream;

        public AttachementTempFile(AttachementItem item, Stream stream)
        {
            Item = item;
            Stream = stream;
        }
    }
}