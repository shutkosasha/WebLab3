using ProtoBuf;

namespace Backend.Models
{
    [ProtoContract]
    public class MarketUpdate
    {
        [ProtoMember(1)]
        public string Symbol { get; set; } = "";

        [ProtoMember(2)]
        public string Price { get; set; } = "";

        [ProtoMember(3)]
        public long EventTime { get; set; }
    }
}