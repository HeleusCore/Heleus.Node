using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Heleus.Base;
using System.Reflection;

namespace Heleus.Node
{
    class Program
    {
        public static readonly bool IsRunningOnNetCore = RuntimeInformation.FrameworkDescription.StartsWith(".NET Core", StringComparison.Ordinal);
        public static readonly bool IsDebugging = Debugger.IsAttached;

        static readonly CancellationTokenSource _quitToken = new CancellationTokenSource();
        static readonly CancellationTokenSource _doneToken = new CancellationTokenSource();

        public static readonly string Version = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;

        static void Quit()
        {
            Log.Trace($"Shutting Down");
            _quitToken.Cancel();
            _doneToken.Token.WaitHandle.WaitOne();
        }

        static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Quit();
        }

        static async Task<int> Main(string[] args)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;

            try
            {
                var nodeTasks = new List<Task<int>>();

                if (Debugger.IsAttached)
                {
                    var start = 0;
                    var nodeCount = 1;

                    var root = new DirectoryInfo(".");
                    var debugRootPath = "";
                    while (true)
                    {
                        try
                        {
                            var path = new DirectoryInfo(Path.Combine(root.FullName, debugRootPath, "heleusdata0"));
                            if (path.Exists)
                            {
                                try
                                {
                                    for (var i = start; i < (start + nodeCount); i++)
                                    {
                                        await (new Node()).Start(new string[] { debugRootPath + "heleusdata" + i, "init" }, _quitToken);
                                        //await (new Node()).Start(new string[] { debugRootPath + "heleusdata" + i, "chainconfig" }, _quitToken);

                                        var node = new Node();
                                        if (await node.Start(new string[] { debugRootPath + "heleusdata" + i, "run" }, _quitToken))
                                            nodeTasks.Add(node.Run());
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.HandleException(ex);
                                }

                                break;
                            }

                            debugRootPath += "../";
                        }
                        catch
                        {
                            Log.Error("No heleusdata directory found. Exiting.");
                            break;
                        }
                    }
                }
                else
                {
                    var node = new Node();
                    if (await node.Start(args, _quitToken))
                        nodeTasks.Add(node.Run());
                }

                await Task.WhenAll(nodeTasks);
                //_doneToken.Cancel();

                return 0;
            }
            catch (Exception ex)
            {
                Log.HandleException(ex);
            }

            _doneToken.Cancel();
            return -1;
        }
    }
}
