public interface INetcodeTransport
{
	bool Connected { get; }

	void Send(byte type, byte[] payload);
}
