using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TicTacToeServer;

internal class Program
{
    private static async Task Main(string[] args)
    {
        try
        {
            AppConfig config = AppConfig.Load("appsettings.json");
            await SimpleGameServer.Run(config.gRPC.Port);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRITICAL ERROR] {ex.Message}");
        }
    }
}