using System;
using Heleus.Base;
using Heleus.Messages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heleus.Node
{
    public class ServiceStartup 
    {
        Node _node;

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Configure(IApplicationBuilder app)
        {
            _node = app.ApplicationServices.GetService<Node>();

            var env = app.ApplicationServices.GetService<IHostEnvironment>();

            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(120),
                ReceiveBufferSize = (int)Message.MessageMaxSize
            });

            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/serviceconnection")
                {
                    if (context.WebSockets.IsWebSocketRequest && _node.ServiceServer != null)
                    {
                        try
                        {
                            await _node.ServiceServer.NewConnection(await context.WebSockets.AcceptWebSocketAsync());
                        }
                        catch(Exception ex)
                        {
                            Log.IgnoreException(ex);
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                    }
                }
            });
        }
    }
}
