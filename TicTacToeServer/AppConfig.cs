using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TicTacToeServer
{
    public class AppConfig
    {
        public GrpcConfig gRPC { get; set; } = null!;

        public static AppConfig Load(string path)
        {
            string json = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;
        }
    }

    public class GrpcConfig
    {
        public string Host { get; set; } = null!;
        public int Port { get; set; }
    }
}
