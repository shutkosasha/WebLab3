using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Backend.Services
{
    public class BinanceService : BackgroundService
    {
        private readonly WebSocketSessionManager _sessionManager;

        public BinanceService(WebSocketSessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectToBinanceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Binance WebSocket error:");
                    Console.WriteLine(ex.Message);

                    await Task.Delay(5000, stoppingToken);
                }
            }
        }

        private async Task ConnectToBinanceAsync(CancellationToken stoppingToken)
        {
            var url =
                "wss://stream.binance.com:9443/stream?streams=" +
                "btcusdt@trade/" +
                "ethusdt@trade/" +
                "solusdt@trade/" +
                "adausdt@trade";

            using var socket = new ClientWebSocket();

            Console.WriteLine("Connecting to Binance WebSocket...");
            Console.WriteLine(url);

            await socket.ConnectAsync(new Uri(url), stoppingToken);

            Console.WriteLine("Connected to Binance WebSocket.");

            while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(socket, stoppingToken);

                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;

                    if (!root.TryGetProperty("data", out var data))
                    {
                        continue;
                    }

                    var symbol = data.GetProperty("s").GetString();
                    var price = data.GetProperty("p").GetString();
                    var eventTime = data.GetProperty("E").GetInt64();

                    if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(price))
                    {
                        continue;
                    }

                    Console.WriteLine($"Binance update: {symbol} = {price}");

                    await _sessionManager.BroadcastAsync(
                        symbol.ToLower(),
                        price,
                        eventTime
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Binance message parse error:");
                    Console.WriteLine(ex.Message);
                }
            }
        }

        private static async Task<string> ReceiveTextAsync(
            ClientWebSocket socket,
            CancellationToken token)
        {
            var buffer = new byte[8192];

            using var memoryStream = new MemoryStream();

            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    token
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return "";
                }

                memoryStream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(memoryStream.ToArray());
        }
    }
}