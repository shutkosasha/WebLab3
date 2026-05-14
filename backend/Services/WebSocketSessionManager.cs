using System.Collections.Concurrent;
using System.Net.WebSockets;
using Backend.Models;
using ProtoBuf;

namespace Backend.Services
{
    public class WebSocketSessionManager
    {
        private readonly ConcurrentDictionary<Guid, WebSocketSession> _sessions = new();

        public Guid Add(WebSocket socket)
        {
            var sessionId = Guid.NewGuid();

            var session = new WebSocketSession
            {
                Id = sessionId,
                Socket = socket
            };

            _sessions.TryAdd(sessionId, session);

            Console.WriteLine($"WebSocket session created: {sessionId}");

            return sessionId;
        }

        public void Remove(Guid sessionId)
        {
            if (_sessions.TryRemove(sessionId, out _))
            {
                Console.WriteLine($"WebSocket session removed: {sessionId}");
            }
        }

        public void Subscribe(Guid sessionId, List<string> symbols)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return;
            }

            session.Symbols.Clear();

            foreach (var symbol in symbols)
            {
                var normalizedSymbol = symbol.Trim().ToLower();

                if (!string.IsNullOrWhiteSpace(normalizedSymbol))
                {
                    session.Symbols.Add(normalizedSymbol);
                }
            }

            Console.WriteLine($"Session {sessionId} subscribed to: {string.Join(", ", session.Symbols)}");
        }

        public async Task BroadcastAsync(string symbol, string price, long eventTime)
        {
            var normalizedSymbol = symbol.Trim().ToLower();

            var update = new MarketUpdate
            {
                Symbol = normalizedSymbol.ToUpper(),
                Price = price,
                EventTime = eventTime
            };

            byte[] bytes;

            using (var stream = new MemoryStream())
            {
                Serializer.Serialize(stream, update);
                bytes = stream.ToArray();
            }

            foreach (var session in _sessions.Values)
            {
                if (session.Socket.State != WebSocketState.Open)
                {
                    continue;
                }

                if (!session.Symbols.Contains(normalizedSymbol))
                {
                    continue;
                }

                await session.Socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None
                );
            }
        }

        private class WebSocketSession
        {
            public Guid Id { get; set; }
            public WebSocket Socket { get; set; } = default!;
            public HashSet<string> Symbols { get; set; } = new();
        }
    }
}