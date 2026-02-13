// MavMessage.cs
namespace arayuz_deneme_1
{
    public record struct MavMessage(byte Version, byte SysId, byte CompId, uint MsgId, byte[] Payload);
}
