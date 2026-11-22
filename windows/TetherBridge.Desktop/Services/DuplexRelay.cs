using System.Net.Sockets;
using TetherBridge.Desktop.Transports;

namespace TetherBridge.Desktop.Services;

public static class DuplexRelay
{
    public static async Task RunAsync(Stream client, Socket clientSocket, Stream phone, Action<int> countUp, Action<int> countDown, CancellationToken cancellationToken)
    {
        if (phone is not IWriteHalfClose phoneSocket)
            throw new NotSupportedException("The phone transport must support TCP write half-close.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Pump(Stream input, Stream output, Func<Task> shutdownWrite, Action<int> count)
        {
            var buffer = new byte[32 * 1024];
            try
            {
                while (true)
                {
                    var n = await input.ReadAsync(buffer, cts.Token);
                    if (n == 0) { await shutdownWrite(); return; }
                    await output.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                    count(n);
                }
            }
            catch
            {
                cts.Cancel();
                throw;
            }
        }
        await Task.WhenAll(
            Pump(client, phone, phoneSocket.ShutdownWriteAsync, countUp),
            Pump(phone, client, () => { clientSocket.Shutdown(SocketShutdown.Send); return Task.CompletedTask; }, countDown));
    }
}
