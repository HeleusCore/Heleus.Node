using Heleus.Messages;
using Heleus.Transactions;

namespace Heleus.Chain.Data
{
    public struct TransactionValidationResult
    {
        public readonly TransactionResultTypes Result;
        public readonly long UserCode;
        public readonly string Message;
        public readonly TransactionValidation TransactionValidation;

        public TransactionValidationResult(TransactionResultTypes resultType, long usercode, string message, TransactionValidation nodeValidation)
        {
            Result = resultType;
            UserCode = usercode;
            Message = message;
            TransactionValidation = nodeValidation;
        }
    }
}
