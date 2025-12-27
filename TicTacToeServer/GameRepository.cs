using Npgsql;
using TicTacToe.App.Protos;

namespace TicTacToeServer
{
    public class GameRepository
    {
        private readonly string _connectionString;

        public GameRepository(string connectionString) => _connectionString = connectionString;

        public async Task UpsertGame(GameResponse game)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                INSERT INTO games (game_id, player_x, player_o, player_x_score, player_o_score, board, status, current_player_id, winner_id, winning_line)
                VALUES (@id, @px, @po, @pxs, @pos, @board, @status, @curr, @win, @line)
                ON CONFLICT (game_id) DO UPDATE SET
                    player_x = EXCLUDED.player_x, 
                    player_o = EXCLUDED.player_o,
                    player_x_score = EXCLUDED.player_x_score, 
                    player_o_score = EXCLUDED.player_o_score,
                    board = EXCLUDED.board, 
                    status = EXCLUDED.status,
                    current_player_id = EXCLUDED.current_player_id, 
                    winner_id = EXCLUDED.winner_id,
                    winning_line = EXCLUDED.winning_line;";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", game.GameId);
            cmd.Parameters.AddWithValue("px", (object?)game.PlayerXId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("po", (object?)game.PlayerOId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("pxs", game.PlayerXScore);
            cmd.Parameters.AddWithValue("pos", game.PlayerOScore);
            cmd.Parameters.AddWithValue("board", game.Board);
            cmd.Parameters.AddWithValue("status", game.Status);
            cmd.Parameters.AddWithValue("curr", (object?)game.CurrentPlayerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("win", (object?)game.WinnerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("line", game.WinningLine.ToArray());

            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<GameResponse?> GetGame(string gameId)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand("SELECT game_id, player_x, player_o, player_x_score, player_o_score, board, status, current_player_id, winner_id, winning_line FROM games WHERE game_id = @id", conn);
            cmd.Parameters.AddWithValue("id", gameId);

            using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var resp = new GameResponse
                {
                    GameId = reader.GetString(0),
                    PlayerXId = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    PlayerOId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    PlayerXScore = reader.GetInt32(3),
                    PlayerOScore = reader.GetInt32(4),
                    Board = reader.GetString(5),
                    Status = reader.GetString(6),
                    CurrentPlayerId = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    WinnerId = reader.IsDBNull(8) ? "" : reader.GetString(8)
                };

                if (!await reader.IsDBNullAsync(9))
                {
                    var line = await reader.GetFieldValueAsync<int[]>(9);
                    resp.WinningLine.AddRange(line);
                }
                return resp;
            }
            return null;
        }
    }
}