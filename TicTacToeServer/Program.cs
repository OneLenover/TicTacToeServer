using Grpc.Core;
using TicTacToe.App.Protos;
using TicTacToeServer.Logic;
using Consul;
using Grpc.Net.Client;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var port = builder.Configuration.GetValue<int>("port", 5001);
var consulAddr = builder.Configuration.GetValue<string>("ConsulAddress") ?? "http://localhost:8500";

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port, o => o.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<GameServiceImpl>();

var app = builder.Build();
app.MapGrpcService<GameServiceImpl>();

app.Lifetime.ApplicationStarted.Register(async () =>
{
    try
    {
        var consul = new ConsulClient(c => c.Address = new Uri(consulAddr));
        var hostIp = GetLocalIPAddress();
        var serviceId = $"tictactoe-server-{port}";
        var myUrl = $"http://{hostIp}:{port}";

        Console.WriteLine("=================================================");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SERVER START] Порт: {port}");

        await consul.Agent.ServiceRegister(new AgentServiceRegistration
        {
            ID = serviceId,
            Name = "tictactoe-service",
            Address = hostIp,
            Port = port,
            Check = new AgentServiceCheck { TCP = $"{hostIp}:{port}", Interval = TimeSpan.FromSeconds(2) }
        });

        // ЦИКЛ ЛИДЕРСТВА ИГРОВОГО СЕРВЕРА
        _ = Task.Run(async () =>
        {
            string leaderKey = "service/tictactoe-service/leader";
            while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                string sessionId = "";
                try
                {
                    // TTL 10с + LockDelay Zero = быстрый переход лидерства
                    var sResp = await consul.Session.Create(new SessionEntry { 
                        Name = $"game-leader-{port}", 
                        TTL = TimeSpan.FromSeconds(10), 
                        LockDelay = TimeSpan.Zero, 
                        Behavior = SessionBehavior.Delete 
                    });
                    sessionId = sResp.Response;

                    var kv = new KVPair(leaderKey) { Session = sessionId, Value = Encoding.UTF8.GetBytes(myUrl) };
                    
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Leader Search] Попытка стать ГЛАВНЫМ сервером...");
                    bool acquired = (await consul.KV.Acquire(kv)).Response;

                    if (acquired)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Leader] УСПЕХ: Я - ГЛАВНЫЙ СЕРВЕР.");
                        while (acquired && !app.Lifetime.ApplicationStopping.IsCancellationRequested)
                        {
                            await consul.Session.Renew(sessionId);
                            await Task.Delay(1000);
                            var curr = await consul.KV.Get(leaderKey);
                            if (curr.Response == null || curr.Response.Session != sessionId) break;
                        }
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Leader] Лидерство ПОТЕРЯНО.");
                    }
                    else
                    {
                        var res = await consul.KV.Get(leaderKey);
                        string currentLeader = res.Response?.Value != null ? Encoding.UTF8.GetString(res.Response.Value) : "неизвестен";
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Follower] Лидер сервера уже существует ({currentLeader}). Повтор через 1 сек.");
                        await Task.Delay(1000); // Попытки раз в секунду по ТЗ
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[Leader Loop Error] {ex.Message}"); await Task.Delay(1000); }
                finally { if (!string.IsNullOrEmpty(sessionId)) try { await consul.Session.Destroy(sessionId); } catch { } }
            }
        });
    }
    catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Consul Error] {ex.Message}"); }
});

app.Run();

static string GetLocalIPAddress()
{
    foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
    {
        // Фильтруем только работающие Ethernet/WiFi адаптеры
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            (networkInterface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
             networkInterface.NetworkInterfaceType != NetworkInterfaceType.Ethernet))
            continue;

        // Исключаем виртуальные и специфические адаптеры
        var description = networkInterface.Description.ToLower();
        if (description.Contains("virtual") ||
            description.Contains("tailscale") ||
            description.Contains("tap") ||
            description.Contains("vpn") ||
            description.Contains("wsl") ||
            description.Contains("hyper-v") ||
            description.Contains("vmware") ||
            description.Contains("virtualbox"))
            continue;

        var ipProperties = networkInterface.GetIPProperties();
        
        foreach (var ipAddress in ipProperties.UnicastAddresses)
        {
            if (ipAddress.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                var ip = ipAddress.Address.ToString();
                
                // Возвращаем первый не-локальный адрес
                if (!ip.StartsWith("127.") && 
                    !ip.StartsWith("169.254.") &&
                    !ip.StartsWith("192.168.56.") &&
                    !ip.StartsWith("192.168.137."))
                {
                    return ip;
                }
            }
        }
    }
    
    return "127.0.0.1";
}

public class GameServiceImpl : GameService.GameServiceBase
{
    private lab4_bd_server.Orm.OrmClient? _ormClient;
    private string? _currentOrmLeaderUrl;
    private readonly ConsulClient _consul;

    public GameServiceImpl(IConfiguration config)
    {
        string cAddr = config.GetValue<string>("ConsulAddress") ?? "http://localhost:8500";
        _consul = new ConsulClient(c => c.Address = new Uri(cAddr));
        _ = MonitorOrmLeader();
    }

    private async Task MonitorOrmLeader()
    {
        while (true)
        {
            try
            {
                var kv = await _consul.KV.Get("service/tictactoe-orm/leader");
                if (kv.Response?.Value != null)
                {
                    string url = Encoding.UTF8.GetString(kv.Response.Value);
                    if (url != _currentOrmLeaderUrl)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ORM Discovery] Подключение к ORM Лидеру: {url}");
                        _currentOrmLeaderUrl = url;
                        var channel = GrpcChannel.ForAddress(url);
                        _ormClient = new lab4_bd_server.Orm.OrmClient(channel);
                    }
                }
                else
                {
                    if (_currentOrmLeaderUrl != null) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [ORM Discovery] Лидер ORM ПОТЕРЯН!");
                    _ormClient = null;
                    _currentOrmLeaderUrl = null;
                }
            }
            catch { }
            await Task.Delay(1000);
        }
    }

    private lab4_bd_server.Orm.OrmClient Orm => _ormClient ?? throw new RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "SYSTEM_SYNCING"));

    public override async Task<TicTacToe.App.Protos.CheckResponse> CheckSession(TicTacToe.App.Protos.CheckRequest r, ServerCallContext c)
    {
        var ormResp = await Orm.CheckSessionAsync(new lab4_bd_server.CheckRequest { PlayerId = r.PlayerId });
        return new TicTacToe.App.Protos.CheckResponse { Exists = ormResp.Exists, GameId = ormResp.GameId };
    }

    public override async Task<GameResponse> CreateGame(CreateRequest r, ServerCallContext c)
    {
        var parts = r.PlayerId.Split('|');
        if (parts.Length < 2) return new GameResponse { Error = "ID_ERR" };
        var nick = parts[0]; var roomId = parts[1];
        var g = await Load(roomId);
        if (g == null) g = new UltimateGameLogic { PlayerX = nick, PlayerO = "" };
        else {
            if (g.PlayerX == nick || g.PlayerO == nick) return Map(roomId, g);
            if (string.IsNullOrEmpty(g.PlayerX)) g.PlayerX = nick;
            else if (string.IsNullOrEmpty(g.PlayerO)) g.PlayerO = nick;
        }
        await Save(roomId, g);
        return Map(roomId, g);
    }

    public override async Task<GameResponse> ResetGame(StateRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        if (g == null) return new GameResponse { Error = "NOT_FOUND" };
        var nl = new UltimateGameLogic { PlayerX = g.PlayerX, PlayerO = g.PlayerO, Status = "Playing" };
        await Save(r.GameId, nl);
        return Map(r.GameId, nl);
    }

    public override async Task<TicTacToe.App.Protos.ExitResponse> ExitGame(TicTacToe.App.Protos.ExitRequest r, ServerCallContext c)
    {
        var resp = await Orm.ExitGameAsync(new lab4_bd_server.ExitRequest { GameId = r.GameId, PlayerId = r.PlayerId });
        return new TicTacToe.App.Protos.ExitResponse { Success = resp.Success };
    }

    public override async Task<GameResponse> MakeMove(MoveRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        if (g == null) return new GameResponse { Error = "ROOM_ERR" };
        lock(g) {
            if (g.ValidMove(r.BoardX, r.BoardY, r.CellX, r.CellY, r.PlayerId))
                g.MakeMove(r.BoardX, r.BoardY, r.CellX, r.CellY);
        }
        await Save(r.GameId, g);
        return Map(r.GameId, g);
    }

    public override async Task<GameResponse> GetState(StateRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        return g != null ? Map(r.GameId, g) : new GameResponse { Error = "NOT_FOUND" };
    }

    private async Task Save(string id, UltimateGameLogic g)
    {
        await Orm.SaveAsync(new lab4_bd_server.SaveRequest {
            GameId = id,
            Game = new lab4_bd_server.Game {
                Cells = new string(g.Cells), SmallWinners = new string(g.SmallWinners),
                ActiveBoardX = g.ActiveBoardX, ActiveBoardY = g.ActiveBoardY,
                PlayerX = g.PlayerX, PlayerO = g.PlayerO, IsXTurn = g.IsXTurn, Status = g.Status
            }
        });
    }

    private async Task<UltimateGameLogic?> Load(string id)
    {
        var res = await Orm.LoadAsync(new lab4_bd_server.LoadRequest { GameId = id });
        if (res.Success && res.Game != null) return new UltimateGameLogic {
            Cells = res.Game.Cells.ToCharArray(), SmallWinners = res.Game.SmallWinners.ToCharArray(),
            ActiveBoardX = res.Game.ActiveBoardX, ActiveBoardY = res.Game.ActiveBoardY,
            PlayerX = res.Game.PlayerX, PlayerO = res.Game.PlayerO, IsXTurn = res.Game.IsXTurn, Status = res.Game.Status
        };
        return null;
    }

    private GameResponse Map(string id, UltimateGameLogic g) => new GameResponse {
        GameId = id, FullBoard = new string(g.Cells), SmallBoardWinners = new string(g.SmallWinners),
        CurrentPlayerId = g.IsXTurn ? g.PlayerX : g.PlayerO, Status = g.Status,
        ActiveBoardX = g.ActiveBoardX, ActiveBoardY = g.ActiveBoardY,
        PlayerX = g.PlayerX, PlayerO = g.PlayerO
    };
}