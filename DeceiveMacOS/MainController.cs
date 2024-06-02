using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Deceive.Properties;

namespace Deceive;

internal class MainController
{
    internal MainController()
    {
		UpdateTrayAsync();
    }

    public bool Enabled = true;
    public string Status = "offline";
	private string StatusFile { get; } = Path.Combine(Persistence.DataDir, "status");
    public bool ConnectToMuc { get; set; } = true;
    private bool SentIntroductionText { get; set; } = false;
    private CancellationTokenSource? ShutdownToken { get; set; } = null;

    private List<ProxiedConnection> Connections { get; } = new();

    public void StartServingClients(TcpListener server, string chatHost, int chatPort)
    {
        Task.Run(() => ServeClientsAsync(server, chatHost, chatPort));
    }

    private async Task ServeClientsAsync(TcpListener server, string chatHost, int chatPort)
    {
        var cert = new X509Certificate2(Resources.Certificate);

        while (true)
        {
            try
            {
                // no need to shutdown, we received a new request
                ShutdownToken?.Cancel();
                ShutdownToken = null;

                var incoming = await server.AcceptTcpClientAsync();
                var sslIncoming = new SslStream(incoming.GetStream());
                await sslIncoming.AuthenticateAsServerAsync(cert);

                TcpClient outgoing;
                while (true)
                {
                    try
                    {
                        outgoing = new TcpClient(chatHost, chatPort);
                        break;
                    }
                    catch (SocketException e)
                    {
                        Trace.WriteLine(e);
                    }
                }

                var sslOutgoing = new SslStream(outgoing.GetStream());
                await sslOutgoing.AuthenticateAsClientAsync(chatHost);

                var proxiedConnection = new ProxiedConnection(this, sslIncoming, sslOutgoing);
                proxiedConnection.Start();
                proxiedConnection.ConnectionErrored += (_, _) =>
                {
                    Trace.WriteLine("Disconnected incoming connection.");
                    Connections.Remove(proxiedConnection);

                    if (Connections.Count == 0)
                    {
                        Task.Run(ShutdownIfNoReconnect);
                    }
                };
                Connections.Add(proxiedConnection);

                if (!SentIntroductionText)
                {
                    SentIntroductionText = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10_000);
                        await SendIntroductionTextAsync();
                    });
                }
            } catch (Exception e)
            {
                Trace.WriteLine("Failed to handle incoming connection.");
                Trace.WriteLine(e);
            }
        }
    }

    private async Task UpdateTrayAsync()
    {
        await UpdateStatusAsync(Status = "offline");
        Enabled = true;
		Task.Delay(5000);
		UpdateTrayAsync();
    }

    private async Task SendIntroductionTextAsync()
    {
        SentIntroductionText = true;
        await SendMessageFromFakePlayerAsync("Venturiol is online.");
    }

    private async Task SendMessageFromFakePlayerAsync(string message)
    {
        foreach (ProxiedConnection connection in Connections)
            await connection.SendMessageFromFakePlayerAsync(message);
    }

    private async Task UpdateStatusAsync(string newStatus)
    {
        foreach (ProxiedConnection connection in Connections)
            await connection.UpdateStatusAsync(newStatus);

        if (newStatus == "chat")
            await SendMessageFromFakePlayerAsync("You are now appearing online.");
        else
            await SendMessageFromFakePlayerAsync("You are now appearing " + newStatus + ".");
    }

    private async Task ShutdownIfNoReconnect()
    {
        if (ShutdownToken == null)
            ShutdownToken = new CancellationTokenSource();
        await Task.Delay(60_000, ShutdownToken.Token);

        Trace.WriteLine("Received no new connections after 60s, shutting down.");
        Environment.Exit(0);
    }

    private void SaveStatus() => File.WriteAllText(StatusFile, Status);
}
