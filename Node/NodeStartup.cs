using System;
using Heleus.Base;
using Heleus.Messages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Heleus.Node
{
    public class NodeStartup
    {
        Node _node;

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers().AddNewtonsoftJson();
        }

        public void Configure(IApplicationBuilder app)
        {
            _node = app.ApplicationServices.GetService<Node>();

            var env = app.ApplicationServices.GetService<IHostEnvironment>();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/error");
            }

            app.UseStatusCodePagesWithReExecute("/error/{0}");

            app.UseRouting();

            app.UseEndpoints((endpoints) =>
            {
                endpoints.MapControllers();
            });

            app.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(120),
                ReceiveBufferSize = (int)Message.MessageMaxSize
            });

            app.Use(async (context, next) =>
            {
                var path = context.Request.Path;

                if (path.StartsWithSegments("/nodeconnection"))
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        try
                        {
                            await _node.NodeServer.NewConnection(await context.WebSockets.AcceptWebSocketAsync());
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
                else if (_node.NodeConfiguration.EnableClientConnections && path.StartsWithSegments("/clientconnection"))
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        try
                        {
                            await _node.ClientServer.NewConnection(await context.WebSockets.AcceptWebSocketAsync());
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
                else
                {
                    await next();
                }
            });
        }
    }
}
