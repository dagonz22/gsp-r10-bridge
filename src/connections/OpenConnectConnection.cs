using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using gspro_r10.OpenConnect;
using Microsoft.Extensions.Configuration;
using TcpClient = NetCoreServer.TcpClient;

namespace gspro_r10
{
  // OpenConnectClient no longer initializes via IP/port settings
  class OpenConnectClient : TcpClient
  {
    public ConnectionManager ConnectionManager { get; set; }
    private bool _stop;

    // Removed configuration-based host/port; using default base ctor
    public OpenConnectClient(ConnectionManager connectionManager)
      : base("127.0.0.1", 0) // heartbeat removed, no IP/port
    {
      ConnectionManager = connectionManager;
    }

    public void DisconnectAndStop()
    {
      _stop = true;
      DisconnectAsync();
      while (IsConnected)
        Thread.Yield();
    }

    protected override void OnConnected()
    {
      OpenConnectLogger.LogGSPInfo($"TCP client connected a new session with Id {Id}");
    }

    // Stub for SetDeviceReady (called by ConnectionManager) - no-op now
    public void SetDeviceReady(bool deviceReady)
    {
      // Intentionally left blank
    }

    public override bool ConnectAsync()
    {
      // Skipped GSPro/OpenConnect connection entirely
      // return base.ConnectAsync();
      return true;
    }

    public override bool SendAsync(string message)
    {
      OpenConnectLogger.LogGSPOutgoing(message);
      return base.SendAsync(message);
    }

    protected override void OnDisconnected()
    {
      Thread.Sleep(5000);
      if (!_stop)
        ConnectAsync();
    }

    protected override void OnReceived(byte[] buffer, long offset, long size)
    {
      string received = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
      OpenConnectLogger.LogGSPIncoming(received);

      string listReceived = $"[{received.Replace("}{", "},{")} ]";
      try
      {
        var responses = JsonSerializer.Deserialize<List<OpenConnectApiResponse>>(listReceived)
                         ?? new List<OpenConnectApiResponse>();
        foreach (var resp in responses)
          HandleResponse(resp);
      }
      catch
      {
        OpenConnectLogger.LogGSPError("Error parsing response");
      }
    }

    private void HandleResponse(OpenConnectApiResponse response)
    {
      if (response.Player != null && response.Player.Club != null)
        ConnectionManager.ClubUpdate(response.Player.Club.Value);
    }

    protected override void OnError(SocketError error)
    {
      if (error != SocketError.TimedOut)
        OpenConnectLogger.LogGSPError($"TCP client caught an error with code {error}");
    }
  }

  public static class OpenConnectLogger
  {
    public static void LogGSPInfo(string message)    
      => LogGSPMessage(message, LogMessageType.Informational);
    public static void LogGSPError(string message)   
      => LogGSPMessage(message, LogMessageType.Error);
    public static void LogGSPOutgoing(string message)
      => LogGSPMessage(message, LogMessageType.Outgoing);
    public static void LogGSPIncoming(string message)
      => LogGSPMessage(message, LogMessageType.Incoming);
    public static void LogGSPMessage(string message, LogMessageType type)
      => BaseLogger.LogMessage(message, "GSPro", type, ConsoleColor.Green);
  }
}