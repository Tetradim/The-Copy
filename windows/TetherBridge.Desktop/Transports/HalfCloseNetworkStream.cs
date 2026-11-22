using System.Net.Sockets;

namespace TetherBridge.Desktop.Transports;

public interface IWriteHalfClose
{
    Task ShutdownWriteAsync();
}

public sealed class HalfCloseNetworkStream : NetworkStream, IWriteHalfClose
{
    private readonly Socket _socket;
    public HalfCloseNetworkStream(Socket socket) : base(socket, ownsSocket: true) => _socket = socket;
    public Task ShutdownWriteAsync() { _socket.Shutdown(SocketShutdown.Send); return Task.CompletedTask; }
}
