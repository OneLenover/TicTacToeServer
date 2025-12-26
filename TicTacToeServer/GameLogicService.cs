using Grpc.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using TicTacToe.App.Protos;

namespace TicTacToeServer
{
    public class GameLogicService : GameService.GameServiceBase
    {
        private class GameSession
        {
            public GameResponse Data { get; set; } = new();
            public string? PlayerX { get; set; }
            public string? PlayerO { get; set; }
        }

        private static readonly ConcurrentDictionary<string, GameSession> _sessions = new();

        public override Task<GameResponse> CreateGame(CreateRequest request, ServerCallContext context)
        {
            var gameId = "room_1";

            var session = _sessions.GetOrAdd(gameId, _ => new GameSession
            {
                Data = new GameResponse
                {
                    GameId = gameId,
                    Board = ".........",
                    Status = "Waiting",
                    CurrentPlayerId = request.PlayerId
                },
                PlayerX = request.PlayerId
            });

            if (session.PlayerX != request.PlayerId && session.PlayerO == null)
            {
                session.PlayerO = request.PlayerId;
                session.Data.Status = "Playing";
                Console.WriteLine($"[Server] Игрок {request.PlayerId} зашел как 'O'. Игра началась!");
            }

            return Task.FromResult(session.Data);
        }

        public override Task<GameResponse> GetState(StateRequest request, ServerCallContext context)
        {
            if (_sessions.TryGetValue(request.GameId, out var session))
                return Task.FromResult(session.Data);

            throw new RpcException(new Status(StatusCode.NotFound, "Игра не найдена"));
        }

        public override Task<GameResponse> MakeMove(MoveRequest request, ServerCallContext context)
        {
            if (!_sessions.TryGetValue(request.GameId, out var session))
                throw new RpcException(new Status(StatusCode.NotFound, "Игра не найдена"));

            var game = session.Data;

            // 1. Валидация состояния
            if (game.Status != "Playing")
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Игра еще не началась или уже окончена"));

            if (game.CurrentPlayerId != request.PlayerId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Сейчас не ваш ход!"));

            // 2. Обновляем поле
            char[] board = game.Board.ToCharArray();
            int index = request.X * 3 + request.Y;

            if (index < 0 || index > 8 || board[index] != '.')
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Некорректный ход"));

            char symbol = (request.PlayerId == session.PlayerX) ? 'X' : 'O';
            board[index] = symbol;
            game.Board = new string(board);

            if (CheckWin(game.Board, out var winningSymbol))
            {
                game.Status = "Won";
                game.WinnerId = request.PlayerId;
            }
            else if (!game.Board.Contains('.'))
            {
                game.Status = "Draw";
            }
            else
            {
                // Передача хода
                game.CurrentPlayerId = (symbol == 'X') ? session.PlayerO : session.PlayerX;
            }

            return Task.FromResult(game);

        }

        private bool CheckWin(string board, out char symbol)
        {
            symbol = ' ';
            int[][] winners = {
                new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 }, // Ряды
                new[] { 0, 3, 6 }, new[] { 1, 4, 7 }, new[] { 2, 5, 8 }, // Колонки
                new[] { 0, 4, 8 }, new[] { 2, 4, 6 }             // Диагонали
            };

            foreach (var w in winners)
            {
                if (board[w[0]] != '.' && board[w[0]] == board[w[1]] && board[w[0]] == board[w[2]])
                {
                    symbol = board[w[0]];
                    return true;
                }
            }

            return false;
        }
    }
}
