using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeServer;

internal class Program
{
    private static async Task Main(string[] args)
    {
        try
        {
            var portStr = Environment.GetEnvironmentVariable("PORT") ?? "50051";
            int port = int.Parse(portStr);

            var advertisedIp = Environment.GetEnvironmentVariable("ADVERTISED_IP") ?? "localhost";

            Console.WriteLine($"[START] Port: {port}, Advertising as: {advertisedIp}");

            await SimpleGameServer.Run(port, advertisedIp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL ERROR] {ex.Message}");
        }
    }
}