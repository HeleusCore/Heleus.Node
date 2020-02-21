using System;
using System.Threading.Tasks;

namespace System.Threading
{
    public static class TokenExtension
    {
        public static Task WaitAsync(this CancellationTokenSource cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Token.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        public static Task WaitAsync(this CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }
    }
}
