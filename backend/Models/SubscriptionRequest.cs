using ProtoBuf;

namespace Backend.Models
{
    [ProtoContract]
    public class SubscriptionRequest
    {
        [ProtoMember(1)]
        public List<string> Symbols { get; set; } = new();
    }
}