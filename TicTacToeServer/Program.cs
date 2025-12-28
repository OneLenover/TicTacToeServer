using Grpc.Core;
using TicTacToe.App.Protos;
using TicTacToeServer.Logic;
using Npgsql;
using Consul;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration.GetValue<int>("port", 5001);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
        listenOptions.UseHttps(); 
    });
});

builder.Services.AddGrpc();

var app = builder.Build();
app.MapGrpcService<GameServiceImpl>();

app.Lifetime.ApplicationStarted.Register(async () =>
{
    try
    {
        var consulAddr = Environment.GetEnvironmentVariable("CONSUL_HTTP_ADDR") ?? "http://localhost:8500";
        var consul = new ConsulClient(c => c.Address = new Uri(consulAddr));
        var hostIp = GetLocalIPAddress();

        var serviceId = $"tictactoe-{port}";

        Console.WriteLine($"[Consul] Регистрация сервиса {serviceId} на {hostIp}:{port}");

        await consul.Agent.ServiceRegister(new AgentServiceRegistration {
            ID = $"tictactoe-{port}", 
            Name = "tictactoe-service", 
            Address = hostIp, 
            Port = port,
            Check = new AgentServiceCheck { 
                TCP = $"{hostIp}:{port}", 
                Interval = TimeSpan.FromSeconds(5) 
            }
        });
    } catch { }
});

app.Run();

static string GetLocalIPAddress() {
    var host = Dns.GetHostEntry(Dns.GetHostName());
    foreach (var ip in host.AddressList) if (ip.AddressFamily == AddressFamily.InterNetwork && !ip.ToString().StartsWith("127.")) return ip.ToString();
    return "127.0.0.1";
}

public class GameServiceImpl : GameService.GameServiceBase
{
    private const string ConnStr = "Host=localhost;Port=5433;Username=postgres;Password=mysecretpassword;Database=postgres";

    public override async Task<CheckResponse> CheckSession(CheckRequest r, ServerCallContext c)
    {
        using var conn = new NpgsqlConnection(ConnStr);
        await conn.OpenAsync();
        using var cmd = new NpgsqlCommand("SELECT game_id FROM games WHERE player_x = @p OR player_o = @p LIMIT 1", conn);
        cmd.Parameters.AddWithValue("p", r.PlayerId);
        var result = await cmd.ExecuteScalarAsync();
        
        return new CheckResponse { 
            Exists = result != null, 
            GameId = result?.ToString() ?? "" 
        };
    }

    public override async Task<GameResponse> CreateGame(CreateRequest r, ServerCallContext c)
    {
        var parts = r.PlayerId.Split('|');
        if (parts.Length < 2) return new GameResponse { Error = "Invalid PlayerId format" };
        var nick = parts[0];
        var roomId = parts[1];
        
        var g = await Load(roomId);
        
        if (g == null) {
            g = new UltimateGameLogic { PlayerX = nick, PlayerO = "" };
        } else {
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
        
        // Создаем абсолютно новый объект логики, сохраняя ники игроков
        var newLogic = new UltimateGameLogic { 
            PlayerX = g.PlayerX, 
            PlayerO = g.PlayerO,
            Status = "Playing" 
        };
        
        await Save(r.GameId, newLogic);
        return Map(r.GameId, newLogic);
    }

    public override async Task<ExitResponse> ExitGame(ExitRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        if (g != null)
        {
            if (g.PlayerX == r.PlayerId) g.PlayerX = "";
            else if (g.PlayerO == r.PlayerId) g.PlayerO = "";

            if (string.IsNullOrEmpty(g.PlayerX) && string.IsNullOrEmpty(g.PlayerO))
            {
                using var conn = new NpgsqlConnection(ConnStr);
                await conn.OpenAsync();
                using var cmd = new NpgsqlCommand("DELETE FROM games WHERE game_id = @id", conn);
                cmd.Parameters.AddWithValue("id", r.GameId);
                await cmd.ExecuteNonQueryAsync();
            }
            else await Save(r.GameId, g);
        }
        return new ExitResponse { Success = true };
    }

    public override async Task<GameResponse> MakeMove(MoveRequest r, ServerCallContext c)
    {
        var g = await Load(r.GameId);
        if (g == null) return new GameResponse { Error = "Комната не найдена" };
        lock (g) {
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
        using var conn = new NpgsqlConnection(ConnStr);
        await conn.OpenAsync();
        var sql = @"INSERT INTO games (game_id, cells, small_winners, active_board_x, active_board_y, player_x, player_o, is_x_turn, status)
                    VALUES (@id, @c, @sw, @ax, @ay, @px, @po, @t, @s)
                    ON CONFLICT (game_id) DO UPDATE SET cells=@c, small_winners=@sw, active_board_x=@ax, active_board_y=@ay, player_x=@px, player_o=@po, is_x_turn=@t, status=@s";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("c", new string(g.Cells));
        cmd.Parameters.AddWithValue("sw", new string(g.SmallWinners));
        cmd.Parameters.AddWithValue("ax", g.ActiveBoardX);
        cmd.Parameters.AddWithValue("ay", g.ActiveBoardY);
        cmd.Parameters.AddWithValue("px", g.PlayerX ?? "");
        cmd.Parameters.AddWithValue("po", g.PlayerO ?? "");
        cmd.Parameters.AddWithValue("t", g.IsXTurn);
        cmd.Parameters.AddWithValue("s", g.Status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<UltimateGameLogic?> Load(string id)
    {
        try {
            using var conn = new NpgsqlConnection(ConnStr);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand("SELECT cells, small_winners, active_board_x, active_board_y, player_x, player_o, is_x_turn, status FROM games WHERE game_id=@id", conn);
            cmd.Parameters.AddWithValue("id", id);
            using var dr = await cmd.ExecuteReaderAsync();
            if (await dr.ReadAsync()) return new UltimateGameLogic {
                Cells = dr.GetString(0).ToCharArray(), SmallWinners = dr.GetString(1).ToCharArray(),
                ActiveBoardX = dr.GetInt32(2), ActiveBoardY = dr.GetInt32(3),
                PlayerX = dr.GetString(4), PlayerO = dr.GetString(5), IsXTurn = dr.GetBoolean(6), Status = dr.GetString(7)
            };
        } catch { }
        return null;
    }

    private GameResponse Map(string id, UltimateGameLogic g, string err = "") => new GameResponse {
        GameId = id, FullBoard = new string(g.Cells), SmallBoardWinners = new string(g.SmallWinners),
        CurrentPlayerId = (g.IsXTurn ? g.PlayerX : g.PlayerO), 
        Status = g.Status, ActiveBoardX = g.ActiveBoardX, ActiveBoardY = g.ActiveBoardY, Error = err,
        PlayerX = g.PlayerX ?? "", PlayerO = g.PlayerO ?? ""
    };
}