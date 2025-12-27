using Grpc.Core;
using System.Collections.Concurrent;
using TicTacToe.App.Protos;

namespace TicTacToeServer
{
    public class GameLogicService : GameService.GameServiceBase
    {
        private class GameSession
        {
            public string GameId { get; init; } = default!;
            public string PlayerX { get; set; } = "";
            public string? PlayerO { get; set; }
            public int PlayerXScore { get; set; }
            public int PlayerOScore { get; set; }
            public string Board { get; set; } = ".........";
            public string Status { get; set; } = "Waiting";
            public string CurrentPlayerId { get; set; } = "";
            public string WinnerId { get; set; } = "";
            public List<int> WinningLine { get; } = new();

            public ConcurrentDictionary<Guid, IServerStreamWriter<GameResponse>> Subscribers { get; } = new();
            public object SyncRoot { get; } = new();

            public GameResponse CreateSnapshot()
            {
                lock (SyncRoot)
                {
                    var resp = new GameResponse
                    {
                        GameId = GameId,
                        Board = Board,
                        CurrentPlayerId = CurrentPlayerId ?? "",
                        Status = Status,
                        WinnerId = WinnerId ?? "",
                        PlayerXId = PlayerX,
                        PlayerOId = PlayerO ?? "",
                        PlayerXScore = PlayerXScore,
                        PlayerOScore = PlayerOScore
                    };
                    resp.WinningLine.AddRange(WinningLine);
                    return resp;
                }
            }

            public static GameSession FromResponse(GameResponse res)
            {
                var session = new GameSession
                {
                    GameId = res.GameId,
                    PlayerX = res.PlayerXId,
                    PlayerO = string.IsNullOrEmpty(res.PlayerOId) ? null : res.PlayerOId,
                    PlayerXScore = res.PlayerXScore,
                    PlayerOScore = res.PlayerOScore,
                    Board = res.Board,
                    Status = res.Status,
                    CurrentPlayerId = res.CurrentPlayerId,
                    WinnerId = res.WinnerId
                };
                session.WinningLine.AddRange(res.WinningLine);
                return session;
            }
        }

        private static readonly string _dbConn = Environment.GetEnvironmentVariable("DB_CONNECTION")
            ?? "Host=localhost;Port=5433;Username=postgres;Password=mysecretpassword;Database=postgres";

        private static readonly GameRepository _repository = new(_dbConn);
        private static readonly ConcurrentDictionary<string, GameSession> _sessions = new();

        private async Task<GameSession> EnsureSessionAsync(string gameId)
        {
            if (_sessions.TryGetValue(gameId, out var session)) return session;

            var dbGame = await _repository.GetGame(gameId);
            if (dbGame != null)
            {
                return _sessions.GetOrAdd(gameId, _ => GameSession.FromResponse(dbGame));
            }
            return _sessions.GetOrAdd(gameId, id => new GameSession { GameId = id });
        }

        public override async Task<GameResponse> CreateGame(CreateRequest request, ServerCallContext context)
        {
            var gameId = string.IsNullOrWhiteSpace(request.GameId) ? "room_1" : request.GameId;
            var session = await EnsureSessionAsync(gameId);

            lock (session.SyncRoot)
            {
                if (string.IsNullOrEmpty(session.PlayerX))
                {
                    session.PlayerX = request.PlayerId;
                    session.CurrentPlayerId = session.PlayerX;
                }
                else if (session.PlayerX != request.PlayerId && session.PlayerO == null)
                {
                    session.PlayerO = request.PlayerId;
                    session.Status = "Playing";
                    // Не меняем CurrentPlayerId, X начинает первым
                }
            }

            await _repository.UpsertGame(session.CreateSnapshot());
            BroadcastSessionUpdate(gameId);
            return session.CreateSnapshot();
        }

        public override async Task<GameResponse> MakeMove(MoveRequest request, ServerCallContext context)
        {
            var session = await EnsureSessionAsync(request.GameId);

            lock (session.SyncRoot)
            {
                if (session.Status != "Playing") throw new RpcException(new Status(StatusCode.FailedPrecondition, "Игра завершена или не начата"));
                if (session.CurrentPlayerId != request.PlayerId) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Сейчас не ваш ход"));

                int index = request.X * 3 + request.Y;
                if (session.Board[index] != '.') throw new RpcException(new Status(StatusCode.InvalidArgument, "Ячейка уже занята"));

                char symbol = (request.PlayerId == session.PlayerX) ? 'X' : 'O';
                char[] board = session.Board.ToCharArray();
                board[index] = symbol;
                session.Board = new string(board);

                if (CheckWin(session.Board, out var winningLine))
                {
                    session.Status = "Won";
                    session.WinnerId = request.PlayerId;
                    session.WinningLine.Clear();
                    session.WinningLine.AddRange(winningLine);
                    if (symbol == 'X') session.PlayerXScore++; else session.PlayerOScore++;
                }
                else if (!session.Board.Contains('.'))
                {
                    session.Status = "Draw";
                }
                else
                {
                    // Переключаем ход. Если PlayerO еще не в сессии (редкий кейс), ход остается у X
                    session.CurrentPlayerId = (symbol == 'X') ? (session.PlayerO ?? session.PlayerX) : session.PlayerX;
                }
            }

            await _repository.UpsertGame(session.CreateSnapshot());
            BroadcastSessionUpdate(request.GameId);
            return session.CreateSnapshot();
        }

        public override async Task SubscribeGameEvents(StateRequest request, IServerStreamWriter<GameResponse> responseStream, ServerCallContext context)
        {
            var session = await EnsureSessionAsync(request.GameId);
            var subId = Guid.NewGuid();

            // При подписке сразу отправляем актуальный снимок из БД/памяти
            await responseStream.WriteAsync(session.CreateSnapshot());

            session.Subscribers[subId] = responseStream;
            Console.WriteLine($"[Server] Новый подписчик {subId} в комнате {request.GameId}");

            try
            {
                await Task.Delay(-1, context.CancellationToken);
            }
            catch (OperationCanceledException) { }
            finally
            {
                session.Subscribers.TryRemove(subId, out _);
            }
        }

        public override async Task<GameResponse> ResetRound(StateRequest request, ServerCallContext context)
        {
            var session = await EnsureSessionAsync(request.GameId);
            lock (session.SyncRoot)
            {
                session.Board = ".........";
                session.WinnerId = "";
                session.WinningLine.Clear();
                session.Status = (session.PlayerO != null) ? "Playing" : "Waiting";
                session.CurrentPlayerId = session.PlayerX;
            }
            await _repository.UpsertGame(session.CreateSnapshot());
            BroadcastSessionUpdate(request.GameId);
            return session.CreateSnapshot();
        }

        public override async Task<GameResponse> GetState(StateRequest request, ServerCallContext context)
        {
            var session = await EnsureSessionAsync(request.GameId);
            return session.CreateSnapshot();
        }

        private void BroadcastSessionUpdate(string gameId)
        {
            if (!_sessions.TryGetValue(gameId, out var session)) return;
            var snapshot = session.CreateSnapshot();

            foreach (var sub in session.Subscribers)
            {
                _ = Task.Run(async () => {
                    try { await sub.Value.WriteAsync(snapshot); }
                    catch { session.Subscribers.TryRemove(sub.Key, out _); }
                });
            }
        }

        private bool CheckWin(string b, out int[] winningLine)
        {
            int[][] patterns = { new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 }, new[] { 0, 3, 6 }, new[] { 1, 4, 7 }, new[] { 2, 5, 8 }, new[] { 0, 4, 8 }, new[] { 2, 4, 6 } };
            foreach (var p in patterns)
            {
                if (b[p[0]] != '.' && b[p[0]] == b[p[1]] && b[p[0]] == b[p[2]])
                {
                    winningLine = p; return true;
                }
            }
            winningLine = Array.Empty<int>(); return false;
        }
    }
}