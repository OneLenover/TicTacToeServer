using Grpc.Core;
using TicTacToe.App.Protos; // Пространство имен основной игры
using TicTacToeServer.Logic;
using Consul;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Grpc.Net.Client;
using lab4_bd_server; // Пространство имен ORM

var builder = WebApplication.CreateBuilder(args);

// Поддержка HTTP/2 без TLS
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var port = builder.Configuration.GetValue<int>("port", 5001);
var consulAddr = builder.Configuration.GetValue<string>("ConsulAddress") ?? "http://localhost:8500";

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<GameServiceImpl>();

app.Lifetime.ApplicationStarted.Register(async () =>
{
    try
    {
        var consul = new ConsulClient(c => c.Address = new Uri(consulAddr));
        var hostIp = GetLocalIPAddress();
        var serviceId = $"tictactoe-{port}";

        await consul.Agent.ServiceRegister(new AgentServiceRegistration
        {
            ID = serviceId,
            Name = "tictactoe-service",
            Address = hostIp,
            Port = port,
            Check = new AgentServiceCheck
            {
                TCP = $"{hostIp}:{port}",
                Interval = TimeSpan.FromSeconds(5),
                DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(30)
            }
        });

        _ = Task.Run(async () =>
        {
            string leaderKey = "service/tictactoe-service/leader";
            string myUrl = $"http://{hostIp}:{port}";

            while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                string sessionId = null!;
                try
                {
                    var sessResp = await consul.Session.Create(new SessionEntry
                    {
                        Name = $"tictactoe-leader-{port}",
                        TTL = TimeSpan.FromSeconds(10),
                        Behavior = SessionBehavior.Delete
                    });
                    sessionId = sessResp.Response;

                    var kv = new KVPair(leaderKey) { Session = sessionId, Value = Encoding.UTF8.GetBytes(myUrl) };
                    var acquired = (await consul.KV.Acquire(kv)).Response;

                    if (acquired)
                    {
                        while (acquired && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            await consul.Session.Renew(sessionId);
                            await Task.Delay(3000);
                            var current = await consul.KV.Get(leaderKey);
                            if (current.Response == null || current.Response.Session != sessionId) break;
                        }
                    }
                }
                catch { await Task.Delay(2000); }
                finally { if (sessionId != null) await consul.Session.Destroy(sessionId); }
            }
        });
    }
    catch (Exception ex) { Console.WriteLine($"[Consul Error] {ex.Message}"); }
});

app.Run();

static string GetLocalIPAddress()
{
    var host = Dns.GetHostEntry(Dns.GetHostName());
    return host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !ip.ToString().StartsWith("127."))?.ToString() ?? "127.0.0.1";
}

public class GameServiceImpl : GameService.GameServiceBase
{
    private readonly lab4_bd_server.Orm.OrmClient _ormClient;

    public GameServiceImpl()
    {
        var channel = GrpcChannel.ForAddress("http://localhost:5131");
        _ormClient = new lab4_bd_server.Orm.OrmClient(channel);
    }

    // Здесь и далее используем полные имена типов, чтобы избежать неоднозначности
    public override async Task<TicTacToe.App.Protos.CheckResponse> CheckSession(TicTacToe.App.Protos.CheckRequest r, ServerCallContext c)
    {
        try 
        {
            var ormResp = await _ormClient.CheckSessionAsync(new lab4_bd_server.CheckRequest { PlayerId = r.PlayerId });
            return new TicTacToe.App.Protos.CheckResponse
            {
                Exists = ormResp.Exists,
                GameId = ormResp.GameId
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"!!! ОШИБКА В CheckSession: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            throw; // Пробрасываем дальше для gRPC
        }
    }

    public override async Task<GameResponse> CreateGame(CreateRequest r, ServerCallContext c)
    {
        var parts = r.PlayerId.Split('|');
        if (parts.Length < 2) return new GameResponse { Error = "Invalid PlayerId format" };
        var nick = parts[0];
        var roomId = parts[1];

        var g = await Load(roomId);
        if (g == null)
        {
            g = new UltimateGameLogic { PlayerX = nick, PlayerO = "" };
        }
        else
        {
            if (g.PlayerX == nick || g.PlayerO == nick) return Map(roomId, g);
            if (string.IsNullOrWhiteSpace(g.PlayerX)) g.PlayerX = nick;
            else if (string.IsNullOrWhiteSpace(g.PlayerO)) g.PlayerO = nick;
        }

        await Save(roomId, g);
        return Map(roomId, g);
    }

    public override async Task<GameResponse> ResetGame(StateRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        if (g == null) return new GameResponse { Error = "Room not found" };

        var newLogic = new UltimateGameLogic
        {
            PlayerX = g.PlayerX,
            PlayerO = g.PlayerO,
            Status = "Playing"
        };

        await Save(r.GameId, newLogic);
        return Map(r.GameId, newLogic);
    }

    public override async Task<TicTacToe.App.Protos.ExitResponse> ExitGame(TicTacToe.App.Protos.ExitRequest r, ServerCallContext c)
    {
        var ormResp = await _ormClient.ExitGameAsync(new lab4_bd_server.ExitRequest 
        { 
            GameId = r.GameId, 
            PlayerId = r.PlayerId 
        });
        
        return new TicTacToe.App.Protos.ExitResponse { Success = ormResp.Success };
    }

    public override async Task<GameResponse> MakeMove(MoveRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        if (g == null) return new GameResponse { Error = "Комната не найдена" };
        
        lock (g)
        {
            if (g.ValidMove(r.BoardX, r.BoardY, r.CellX, r.CellY, r.PlayerId))
                g.MakeMove(r.BoardX, r.BoardY, r.CellX, r.CellY);
        }
        
        await Save(r.GameId, g);
        return Map(r.GameId, g);
    }

    public override async Task<GameResponse> GetState(StateRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        return g != null ? Map(r.GameId, g) : new GameResponse { Error = "Not found" };
    }

    private async Task Save(string id, UltimateGameLogic g)
    {
        var gameMsg = new lab4_bd_server.Game
        {
            Cells = new string(g.Cells),
            SmallWinners = new string(g.SmallWinners),
            ActiveBoardX = g.ActiveBoardX,
            ActiveBoardY = g.ActiveBoardY,
            PlayerX = g.PlayerX ?? "",
            PlayerO = g.PlayerO ?? "",
            IsXTurn = g.IsXTurn,
            Status = g.Status
        };

        await _ormClient.SaveAsync(new lab4_bd_server.SaveRequest 
        { 
            GameId = id, 
            Game = gameMsg 
        });
    }

    private async Task<UltimateGameLogic?> Load(string id)
    {
        try
        {
            var resp = await _ormClient.LoadAsync(new lab4_bd_server.LoadRequest { GameId = id });
            if (resp.Success && resp.Game != null)
            {
                return new UltimateGameLogic
                {
                    Cells = resp.Game.Cells.ToCharArray(),
                    SmallWinners = resp.Game.SmallWinners.ToCharArray(),
                    ActiveBoardX = resp.Game.ActiveBoardX,
                    ActiveBoardY = resp.Game.ActiveBoardY,
                    PlayerX = resp.Game.PlayerX,
                    PlayerO = resp.Game.PlayerO,
                    IsXTurn = resp.Game.IsXTurn,
                    Status = resp.Game.Status
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ORM Load Error] {ex.Message}");
        }
        return null;
    }

    private GameResponse Map(string id, UltimateGameLogic g, string err = "") => new GameResponse
    {
        GameId = id,
        FullBoard = new string(g.Cells),
        SmallBoardWinners = new string(g.SmallWinners),
        CurrentPlayerId = (g.IsXTurn ? g.PlayerX : g.PlayerO),
        Status = g.Status,
        ActiveBoardX = g.ActiveBoardX,
        ActiveBoardY = g.ActiveBoardY,
        Error = err,
        PlayerX = g.PlayerX ?? "",
        PlayerO = g.PlayerO ?? ""
    };
}