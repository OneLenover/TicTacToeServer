using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;

namespace TicTacToeServer
{
    public static class SimpleGameServer
    {
        public static async Task Run(int port)
        {
            var builder = WebApplication.CreateBuilder();

            builder.Services.AddGrpc();

            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(port, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
            });

            var app = builder.Build();

            app.MapGrpcService<GameLogicService>();

            Console.WriteLine($"[SERVER] gRPC запущен на порту {port}. Ждем игроков...");
            await app.RunAsync();
        }
    }
}
