using gspro_r10.OpenConnect;
using InTheHand.Bluetooth;
using LaunchMonitor.Proto;
using gspro_r10.bluetooth;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;


namespace gspro_r10
{
  public class BluetoothConnection : IDisposable
  {
    private static readonly double METERS_PER_S_TO_MILES_PER_HOUR = 2.2369;
    private static readonly float FEET_TO_METERS = 1 / 3.281f;
    private static readonly HttpClient _httpClient = new HttpClient();
    private bool disposedValue;

    public ConnectionManager ConnectionManager { get; }
    public IConfigurationSection Configuration { get; }
    public int ReconnectInterval { get; }
    public LaunchMonitorDevice? LaunchMonitor { get; private set; }
    public BluetoothDevice? Device { get; private set; }

    public BluetoothConnection(ConnectionManager connectionManager, IConfigurationSection configuration)
    {
      ConnectionManager = connectionManager;
      Configuration = configuration;
      ReconnectInterval = int.Parse(configuration["reconnectInterval"] ?? "5");
      Task.Run(ConnectToDevice);

    }

    private void ConnectToDevice()
    {
      string deviceName = Configuration["bluetoothDeviceName"] ?? "Approach R10";
      Device = FindDevice(deviceName);
      if (Device == null)
      {
        BluetoothLogger.Error($"Could not find '{deviceName}' in list of paired devices.");
        BluetoothLogger.Error("Device must be paired through computer bluetooth settings before running");
        BluetoothLogger.Error("If device is paired, make sure name matches exactly what is set in 'bluetoothDeviceName' in settings.json");
        return;
      }

      do
      {
        BluetoothLogger.Info($"Connecting to {Device.Name}: {Device.Id}");
        Device.Gatt.ConnectAsync().Wait();

        if (!Device.Gatt.IsConnected)
        {
          BluetoothLogger.Info($"Could not connect to bluetooth device. Waiting {ReconnectInterval} seconds before trying again");
          Thread.Sleep(TimeSpan.FromSeconds(ReconnectInterval));
        }
      }
      while (!Device.Gatt.IsConnected);

      Device.Gatt.AutoConnect = true;

      BluetoothLogger.Info($"Connected to Launch Monitor");
      LaunchMonitor = SetupLaunchMonitor(Device);
      Device.GattServerDisconnected += OnDeviceDisconnected;
    }

    private void OnDeviceDisconnected(object? sender, EventArgs args)
    {
      BluetoothLogger.Error("Lost bluetooth connection");
      if (Device != null)
        Device.GattServerDisconnected -= OnDeviceDisconnected;
      LaunchMonitor?.Dispose();

      Task.Run(ConnectToDevice);
    }

    private LaunchMonitorDevice? SetupLaunchMonitor(BluetoothDevice device)
    {
      LaunchMonitorDevice lm = new LaunchMonitorDevice(device);
      lm.AutoWake = bool.Parse(Configuration["autoWake"] ?? "false");
      lm.CalibrateTiltOnConnect = bool.Parse(Configuration["calibrateTiltOnConnect"] ?? "false");

      lm.DebugLogging = bool.Parse(Configuration["debugLogging"] ?? "false");

      lm.MessageRecieved += (o, e) => BluetoothLogger.Incoming(e.Message?.ToString() ?? string.Empty);
      lm.MessageSent += (o, e) => BluetoothLogger.Outgoing(e.Message?.ToString() ?? string.Empty);
      lm.BatteryLifeUpdated += (o, e) => BluetoothLogger.Info($"Battery Life Updated: {e.Battery}%");
      lm.Error += (o, e) => BluetoothLogger.Error($"{e.Severity}: {e.Message}");

      if (bool.Parse(Configuration["sendStatusChangesToGSP"] ?? "false"))
      {
        lm.ReadinessChanged += (o, e) =>
        {
          ConnectionManager.SendLaunchMonitorReadyUpdate(e.Ready);
        };
      }

      lm.ShotMetrics += (o, e) =>
      {
        LogMetrics(e.Metrics);
        ConnectionManager.SendShot(
          BallDataFromLaunchMonitorMetrics(e.Metrics?.BallMetrics),
          ClubDataFromLaunchMonitorMetrics(e.Metrics?.ClubMetrics)
        );
      };

      if (!lm.Setup())
      {
        BluetoothLogger.Error("Failed Device Setup");
        return null;
      }

      float temperature = float.Parse(Configuration["temperature"] ?? "60");
      float humidity = float.Parse(Configuration["humidity"] ?? "1");
      float altitude = float.Parse(Configuration["altitude"] ?? "0");
      float airDensity = float.Parse(Configuration["airDensity"] ?? "1");
      float teeDistanceInFeet = float.Parse(Configuration["teeDistanceInFeet"] ?? "7");
      float teeRange = teeDistanceInFeet * FEET_TO_METERS;
      string apiKey = Configuration["X-Api-Key"] ?? "";
      string csrfToken = Configuration["X-CsrfToken"] ?? "";
      BluetoothLogger.Info("API Key: " + apiKey);
      BluetoothLogger.Info("CsrfToken: " + csrfToken);

      lm.ShotConfig(temperature, humidity, altitude, airDensity, teeRange);

      BluetoothLogger.Info($"Device Setup Complete: ");
      BluetoothLogger.Info($"   Model: {lm.Model}");
      BluetoothLogger.Info($"   Firmware: {lm.Firmware}");
      BluetoothLogger.Info($"   Bluetooth ID: {lm.Device.Id}");
      BluetoothLogger.Info($"   Battery: {lm.Battery}%");
      BluetoothLogger.Info($"   Current State: {lm.CurrentState}");
      BluetoothLogger.Info($"   Tilt: {lm.DeviceTilt}");

      return lm;
    }

    private BluetoothDevice? FindDevice(string deviceName)
    {
      foreach (BluetoothDevice pairedDev in Bluetooth.GetPairedDevicesAsync().Result)
        if (pairedDev.Name == deviceName)
          return pairedDev;
      return null;
    }

    public static BallData? BallDataFromLaunchMonitorMetrics(BallMetrics? ballMetrics)
    {
      if (ballMetrics == null) return null;
      return new BallData()
      {
        HLA = ballMetrics.LaunchDirection,
        VLA = ballMetrics.LaunchAngle,
        Speed = ballMetrics.BallSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        SpinAxis = ballMetrics.SpinAxis * -1,
        TotalSpin = ballMetrics.TotalSpin,
        SideSpin = ballMetrics.TotalSpin * Math.Sin(-1 * ballMetrics.SpinAxis * Math.PI / 180),
        BackSpin = ballMetrics.TotalSpin * Math.Cos(-1 * ballMetrics.SpinAxis * Math.PI / 180)
      };
    }

    public static ClubData? ClubDataFromLaunchMonitorMetrics(ClubMetrics? clubMetrics)
    {
      if (clubMetrics == null) return null;
      return new ClubData()
      {
        Speed = clubMetrics.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        SpeedAtImpact = clubMetrics.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,
        AngleOfAttack = clubMetrics.AttackAngle,
        FaceToTarget = clubMetrics.ClubAngleFace,
        Path = clubMetrics.ClubAnglePath
      };
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!disposedValue)
      {
        if (disposing)
        {
          if (Device != null)
            Device.GattServerDisconnected -= OnDeviceDisconnected;
          LaunchMonitor?.Dispose();
        }

        disposedValue = true;
      }
    }

    public async Task LogMetrics(Metrics? metrics)
    {
      if (metrics == null)
      {
        return;
      }
      try
      {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"===== Shot {metrics.ShotId} =====");
        sb.AppendLine($"{"Ball Metrics",-40}│ {"Club Metrics",-40}│ {"Swing Metrics",-40}");
        sb.AppendLine($"{new string('─', 40)}┼─{new string('─', 40)}┼─{new string('─', 40)}");
        sb.Append($" {"BallSpeed:",-15} {metrics.BallMetrics?.BallSpeed * METERS_PER_S_TO_MILES_PER_HOUR,-22} │");
        sb.Append($" {"Club Speed:",-20} {metrics.ClubMetrics?.ClubHeadSpeed * METERS_PER_S_TO_MILES_PER_HOUR,-18} │");
        sb.AppendLine($" {"Backswing Start:",-20} {metrics.SwingMetrics?.BackSwingStartTime,-17}");

        sb.Append($" {"VLA:",-15} {metrics.BallMetrics?.LaunchAngle,-22} │");
        sb.Append($" {"Club Path:",-20} {metrics.ClubMetrics?.ClubAnglePath,-18} │");
        sb.AppendLine($" {"Downswing Start:",-20} {metrics.SwingMetrics?.DownSwingStartTime,-17}");

        sb.Append($" {"HLA:",-15} {metrics.BallMetrics?.LaunchDirection,-22} │");
        sb.Append($" {"Club Face:",-20} {metrics.ClubMetrics?.ClubAngleFace,-18} │");
        sb.AppendLine($" {"Impact time:",-20} {metrics.SwingMetrics?.ImpactTime,-17}");

        uint? backswingDuration = metrics.SwingMetrics?.DownSwingStartTime - metrics.SwingMetrics?.BackSwingStartTime;
        sb.Append($" {"Spin Axis:",-15} {metrics.BallMetrics?.SpinAxis * -1,-22} │");
        sb.Append($" {"Attack Angle:",-20} {metrics.ClubMetrics?.AttackAngle,-18} │");
        sb.AppendLine($" {"Backswing duration:",-20} {backswingDuration,-17}");

        uint? downswingDuration = metrics.SwingMetrics?.ImpactTime - metrics.SwingMetrics?.DownSwingStartTime;
        sb.Append($" {"Total Spin:",-15} {metrics.BallMetrics?.TotalSpin,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Downswing duration:",-20} {downswingDuration,-17}");

        sb.Append($" {"Ball Type:",-15} {metrics.BallMetrics?.GolfBallType,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Tempo:",-20} {(float)(backswingDuration ?? 0) / downswingDuration,-17}");

        sb.Append($" {"Spin Calc:",-15} {metrics.BallMetrics?.SpinCalculationType,-22} │");
        sb.Append($" {"",-20} {"",-18} │");
        sb.AppendLine($" {"Normal/Practice:",-20} {metrics.ShotType,-17}");
        BluetoothLogger.Info(sb.ToString());
        
        StringBuilder jsonSb = new StringBuilder();
        jsonSb.AppendLine("{");
        jsonSb.AppendLine("  \"sensor_data\": {");
        jsonSb.AppendLine($"    \"Club Face\": {metrics.ClubMetrics?.ClubAngleFace ?? 0},");
        jsonSb.AppendLine($"    \"HLA\": {metrics.BallMetrics?.LaunchDirection ?? 0},");

        double spinAxisRad = (metrics.BallMetrics?.SpinAxis ?? 0) * Math.PI / 180;
        double totalSpin = metrics.BallMetrics?.TotalSpin ?? 0;
        double sideSpin = totalSpin * Math.Sin(spinAxisRad);
        double backSpin = totalSpin * Math.Cos(spinAxisRad);
        double smashFactor = (metrics.BallMetrics?.BallSpeed ?? 0) / (metrics.ClubMetrics?.ClubHeadSpeed ?? 0);

        jsonSb.AppendLine($"    \"Sidespin\": {sideSpin},");
        // If you have a SmashFactor calculation, insert it here, otherwise default to 0
        jsonSb.AppendLine($"    \"Smash Factor\": {smashFactor},");
        jsonSb.AppendLine($"    \"VLA\": {metrics.BallMetrics?.LaunchAngle ?? 0},");
        jsonSb.AppendLine($"    \"Attack Angle\": {metrics.ClubMetrics?.AttackAngle ?? 0},");
        jsonSb.AppendLine($"    \"Ball Speed\": {(metrics.BallMetrics?.BallSpeed ?? 0) * METERS_PER_S_TO_MILES_PER_HOUR},");
        jsonSb.AppendLine($"    \"Face to Path\": {(metrics.ClubMetrics != null ? metrics.ClubMetrics.ClubAngleFace - metrics.ClubMetrics.ClubAnglePath : 0)},");
        jsonSb.AppendLine($"    \"Club Speed\": {(metrics.ClubMetrics?.ClubHeadSpeed ?? 0) * METERS_PER_S_TO_MILES_PER_HOUR},");
        jsonSb.AppendLine($"    \"Spin Axis\": {(metrics.BallMetrics?.SpinAxis ?? 0)},");
        jsonSb.AppendLine($"    \"Backspin\": {backSpin}");
        jsonSb.AppendLine("  },");
        // Replace with your actual club identifier
        jsonSb.AppendLine("  \"club\": \"string\"");
        jsonSb.AppendLine("}");
        BluetoothLogger.Info(jsonSb.ToString());
        await PostShotJsonAsync(jsonSb.ToString(), metrics);

      }
      catch (Exception e)
      {
        Console.WriteLine(e);
      }

    }
    private async Task PostShotJsonAsync(string json, Metrics metrics)
    {
      try
      {
        // 1) Wrap JSON
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // 2) Build request
        var request = new HttpRequestMessage(HttpMethod.Post,
          "https://dev-api-app.fairwaytec.com/api/shots/create/")
        {
          Content = content
        };

        // 3) Required headers
        request.Headers.Accept.Add(
          new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-API-Key", Configuration["X-Api-Key"]);
        request.Headers.Add("X-CSRFTOKEN", Configuration["X-CsrfToken"]);

        // 4) Send & log
        var resp = await _httpClient.SendAsync(request);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode)
          BluetoothLogger.Info($"Posted shot #{metrics.ShotId}: {resp.StatusCode}");
        else
          BluetoothLogger.Error($"POST failed {resp.StatusCode}: {body}");
      }
      catch (Exception ex)
      {
        BluetoothLogger.Error($"Error posting shot: {ex.Message}");
      }
    }


    public void Dispose()
    {
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }
  }

  public static class BluetoothLogger
  {
    public static void Info(string message) => LogBluetoothMessage(message, LogMessageType.Informational);
    public static void Error(string message) => LogBluetoothMessage(message, LogMessageType.Error);
    public static void Outgoing(string message) => LogBluetoothMessage(message, LogMessageType.Outgoing);
    public static void Incoming(string message) => LogBluetoothMessage(message, LogMessageType.Incoming);
    public static void LogBluetoothMessage(string message, LogMessageType type) => BaseLogger.LogMessage(message, "R10-BT", type, ConsoleColor.Magenta);
  }
}