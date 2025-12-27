using Consul;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace TicTacToeServer
{
    public static class SimpleGameServer
    {
        private static readonly string LeaderKey = "service/tic-tac-toe/leader";
        private static readonly string ConsulAddr = Environment.GetEnvironmentVariable("CONSUL_HTTP_ADDR") ?? "http://localhost:8500";

        private static readonly IConsulClient _consulClient = new ConsulClient(c => {
            c.Address = new Uri(ConsulAddr);
        });

        public static async Task Run(int port, string hostIp)
        {
            var sessionRes = await _consulClient.Session.Create(new SessionEntry
            {
                Name = $"TicTacToe-Logic-{port}",
                TTL = TimeSpan.FromSeconds(10),
                Behavior = SessionBehavior.Release,
                LockDelay = TimeSpan.Zero
            });
            string sessionId = sessionRes.Response;

            _ = Task.Run(async () => {
                while (true)
                {
                    try { await _consulClient.Session.Renew(sessionId); await Task.Delay(5000); }
                    catch { break; }
                }
            });

            Console.WriteLine($"[Consul] Ожидание лидерства на порту {port} ({ConsulAddr})...");

            while (true)
            {
                var kvPair = new KVPair(LeaderKey)
                {
                    Value = Encoding.UTF8.GetBytes($"http://{hostIp}:{port}"),
                    Session = sessionId
                };

                bool isLeader = (await _consulClient.KV.Acquire(kvPair)).Response;

                if (isLeader)
                {
                    Console.WriteLine(">>> Я ЛИДЕР. Запуск gRPC...");
                    await StartHost(port);
                    break;
                }
                await Task.Delay(1000);
            }
        }

        private static async Task StartHost(int port)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddGrpc();
            builder.WebHost.ConfigureKestrel(o => {
                o.ListenAnyIP(port, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
            });

            var app = builder.Build();
            app.MapGrpcService<GameLogicService>();
            await app.RunAsync();
        }
    }
}