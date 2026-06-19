namespace Nova3DVisualiser.Network;

public interface INetworkPacket
{
    void Serialize(BinaryWriter writer);
    
    void Deserialize(BinaryReader reader);
}