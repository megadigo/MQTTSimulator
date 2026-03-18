
using IoTTestOrchestrator;
using MQTT;
using MQTT.Plugin;
using ScenarioBuilder.Implementations.Configuration;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace MQTTSimulator
{
    internal record SimulatorConfig(
        bool DebugMode = false,
        double WarmupSeconds = 10.0,
        double CuttingTemperature = 80.0,
        double TemperatureNoise = 1.5,
        double AnomalyRiseRate = 5.0,
        double AnomalyShutdownThreshold = 120.0,
        double StepIntervalSeconds = 0.5);  // seconds between MQTT publishes (higher = slower)
    /// <summary>Filters out [Debug] lines from console output when debug mode is off.</summary>
    internal sealed class DebugFilterWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _buffer = new();
        public DebugFilterWriter(TextWriter inner) => _inner = inner;
        public override Encoding Encoding => _inner.Encoding;
        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = _buffer.ToString();
                _buffer.Clear();
                if (!line.Contains("[Debug]"))
                    _inner.WriteLine(line);
            }
            else if (value != '\r')
            {
                _buffer.Append(value);
            }
        }
        public override void WriteLine(string? value)
        {
            if (value != null && value.Contains("[Debug]")) return;
            _inner.WriteLine(value);
        }
        public override void Flush() => _inner.Flush();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); }
    }
    public class ScenarioRunner
    {
        private TestScenario? _scenario;
        private CancellationTokenSource? cts;
        private static bool startModeEnabled = false;
        private static readonly object modeLock = new object();
        // CNC simulation parameters
        private const double DefaultRapidRate = 3000.0; // mm/min for G0 moves
        private const double DefaultFeedRate = 300.0;   // mm/min fallback when F not yet specified
        private const double SafeZ = 5.0;               // safe Z fallback
        private double StepInterval => _config.StepIntervalSeconds;
        private readonly Random _rng = new Random();
        private string _ncFilePath = "gcode.nc";
        private SimulatorConfig _config = new SimulatorConfig();
        private bool _anomalyTriggered = false;
        private DateTime _anomalyStart = DateTime.MaxValue;
        private string _lastState = string.Empty;
        private static SimulatorConfig LoadConfig()
        {
            const string configFile = "simulator.config.json";
            if (!File.Exists(configFile)) return new SimulatorConfig();
            try
            {
                var json = File.ReadAllText(configFile);
                return JsonSerializer.Deserialize<SimulatorConfig>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new SimulatorConfig();
            }
            catch { return new SimulatorConfig(); }
        }
        public ScenarioRunner()
        {
        }
        public async System.Threading.Tasks.Task RunAsync(string[]? args = null)
        {
            cts = new CancellationTokenSource();
            var config = LoadConfig();
            _config = config;
            if (!config.DebugMode)
            {
                Console.SetOut(new DebugFilterWriter(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }));
                Console.WriteLine("[CNC] Debug mode: OFF (simulator.config.json)");
            }
            else
            {
                Console.WriteLine("[CNC] Debug mode: ON");
            }
            if (args?.Length > 0)
            {
                _ncFilePath = args[0];
            }
            else
            {
                var selected = PromptSelectNcFile();
                if (selected == null)
                {
                    Console.WriteLine("[CNC] No file selected. Exiting.");
                    return;
                }
                _ncFilePath = selected;
            }
            if (!File.Exists(_ncFilePath))
            {
                Console.WriteLine($"[ERROR] G-code file not found: {_ncFilePath}");
                return;
            }
            Console.WriteLine($"[CNC] G-code file: {Path.GetFullPath(_ncFilePath)}");
            var inputTask = System.Threading.Tasks.Task.Run(() =>
            {
                Console.WriteLine("Press 's' to start CNC simulation;");
                Console.WriteLine("Press 'a' to trigger thermal anomaly;");
                Console.WriteLine("Press 'q' to quit.");
                while (!cts.IsCancellationRequested)
                {
                    if (!Console.KeyAvailable) { Thread.Sleep(50); continue; }
                    var key = Console.ReadKey(intercept: true);
                    if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        Console.WriteLine("\nShutdown initiated...");
                        cts.Cancel();
                        break;
                    }
                    if (key.KeyChar == 's' || key.KeyChar == 'S')
                    {
                        lock (modeLock)
                        {
                            startModeEnabled = !startModeEnabled;
                            Console.WriteLine(startModeEnabled
                                ? "\n[CNC] Simulation STARTED"
                                : "\n[CNC] Simulation STOPPED");
                        }
                    }
                    if (key.KeyChar == 'a' || key.KeyChar == 'A')
                    {
                        lock (modeLock)
                        {
                            if (!_anomalyTriggered)
                            {
                                _anomalyTriggered = true;
                                _anomalyStart = DateTime.UtcNow;
                                Console.WriteLine("\n[CNC] *** ANOMALY TRIGGERED: thermal runaway! ***");
                            }
                        }
                    }
                }
            });
            var scenario = new ScenarioConfiguration()
                .WriteLogsTo("c:/temp/MQTT-Simulator.log")
                .ManagerId("MQTTManager")
                .ConfigPath("C:\\IOT\\AutomationManager\\AMCNC\\config.full.json")
                .AddSimulatorPlugin<MQTT.PluginMain>(new SettingsBuilder()
                    .Address("localhost", 1883)
                    .StartBroker(true)
                    .Build());
            _scenario = new TestScenario(scenario);
            var context = _scenario.Context();
            MQTT.PluginMain? mqttSimulator = context.Simulators["MQTT"] as MQTT.PluginMain;
            try
            {
                _scenario.Start();
                _scenario.StartSimulators();
                Console.WriteLine("[CNC] Waiting for MQTT broker...");
                await WaitForMqttReadyAsync(mqttSimulator!, cts.Token);
                Console.WriteLine("[CNC] MQTT ready.");
                while (!cts.Token.IsCancellationRequested)
                {
                    bool shouldRun;
                    lock (modeLock) { shouldRun = startModeEnabled; }
                    if (shouldRun)
                    {
                        await RunCncSimulationAsync(mqttSimulator!);
                        // After one full job completes, stop and wait for next 's'
                        lock (modeLock) { startModeEnabled = false; }
                        Console.WriteLine("[CNC] Job complete. Press 's' to run again.");
                    }
                    else
                    {
                        await Task.Delay(100, cts.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
            }
            finally
            {
                _scenario.ShutdownSimulators();
                _scenario.Shutdown();
            }
            await inputTask;
        }
        private string? PromptSelectNcFile()
        {
            var files = Directory.GetFiles(AppContext.BaseDirectory, "*.nc")
                .Concat(Directory.GetFiles(Directory.GetCurrentDirectory(), "*.nc"))
                .Select(Path.GetFullPath)
                .Distinct()
                .OrderBy(f => f)
                .ToList();
            if (files.Count == 0)
            {
                Console.WriteLine("[CNC] No .nc files found in current directory.");
                Console.Write("Enter full path to .nc file: ");
                return Console.ReadLine()?.Trim();
            }
            Console.WriteLine("\nAvailable G-code files:");
            for (int i = 0; i < files.Count; i++)
                Console.WriteLine($"  [{i + 1}] {Path.GetFileName(files[i])}");
            Console.WriteLine($"  [0] Enter custom path");
            Console.Write($"Select file [1-{files.Count}]: ");
            var input = Console.ReadLine()?.Trim();
            if (!int.TryParse(input, out int choice)) return null;
            if (choice == 0)
            {
                Console.Write("Enter full path to .nc file: ");
                return Console.ReadLine()?.Trim();
            }
            if (choice < 1 || choice > files.Count) return null;
            return files[choice - 1];
        }
        private async Task RunCncSimulationAsync(PluginMain mqtt)
        {
            // Reset anomaly state at the start of each run
            lock (modeLock) { _anomalyTriggered = false; _anomalyStart = DateTime.MaxValue; }
            _lastState = string.Empty;
            var jobStart = DateTime.UtcNow;
            double warmupSeconds = _config.WarmupSeconds;
            Console.WriteLine($"[CNC] Warming up for {warmupSeconds}s...");
            // --- Warmup phase: time-only, position stays at home ---
            while (!cts.Token.IsCancellationRequested)
            {
                bool shouldRun;
                lock (modeLock) { shouldRun = startModeEnabled; }
                if (!shouldRun) return;
                double elapsedSeconds = (DateTime.UtcNow - jobStart).TotalSeconds;
                double warmupProgress = Math.Min(elapsedSeconds / warmupSeconds * 100.0, 100.0);
                SafePublish(mqtt, "cnc/position", JsonSerializer.Serialize(new
                {
                    x = 0.0, y = 0.0, z = SafeZ, feedrate = 0.0, mode = "home"
                }));
                SafePublish(mqtt, "cnc/head/temperature", JsonSerializer.Serialize(new
                {
                    temperature = Math.Round(ComputeHeadTemperature(elapsedSeconds, warmupSeconds), 2),
                    setpoint = _config.CuttingTemperature,
                    state = "warming_up"
                }));
                PublishState(mqtt, "warming_up");
                SafePublish(mqtt, "cnc/status", JsonSerializer.Serialize(new
                {
                    progress = Math.Round(warmupProgress, 1),
                    elapsed_seconds = Math.Round(elapsedSeconds, 1)
                }));
                if (elapsedSeconds % 5 < StepInterval)
                    Console.WriteLine($"[CNC] Warming up... {elapsedSeconds:F0}s / {warmupSeconds}s");
                if (elapsedSeconds >= warmupSeconds)
                {
                    Console.WriteLine("[CNC] Warmup complete. Starting cut.");
                    break;
                }
                await Task.Delay((int)(StepInterval * 1000), cts.Token);
            }
            if (cts.Token.IsCancellationRequested) return;
            // --- Cutting phase ---
            var cutStart = DateTime.UtcNow;
            var toolpath = LoadToolpathFromGCode(_ncFilePath);
            int totalPoints = toolpath.Count;
            int pointIndex = 0;
            foreach (var point in toolpath)
            {
                if (cts.Token.IsCancellationRequested) break;
                bool shouldRun;
                lock (modeLock) { shouldRun = startModeEnabled; }
                if (!shouldRun) break;
                double totalElapsed = (DateTime.UtcNow - jobStart).TotalSeconds;
                double cutElapsed = (DateTime.UtcNow - cutStart).TotalSeconds;
                double temperature = ComputeHeadTemperature(totalElapsed, _config.WarmupSeconds);
                double progress = (double)pointIndex / totalPoints;
                // Check for anomaly shutdown
                bool anomaly;
                lock (modeLock) { anomaly = _anomalyTriggered; }
                if (anomaly && temperature >= _config.AnomalyShutdownThreshold)
                {
                    Console.WriteLine($"[CNC] *** FAULT: temperature {temperature:F1}°C exceeded limit {_config.AnomalyShutdownThreshold}°C — MACHINE STOP ***");
                    SafePublish(mqtt, "cnc/position", JsonSerializer.Serialize(new { x = Math.Round(point.X, 3), y = Math.Round(point.Y, 3), z = Math.Round(point.Z, 3), feedrate = 0.0, mode = "fault" }));
                    SafePublish(mqtt, "cnc/head/temperature", JsonSerializer.Serialize(new { temperature = Math.Round(temperature, 2), setpoint = _config.CuttingTemperature, state = "fault" }));
                    PublishState(mqtt, "fault");
                    SafePublish(mqtt, "cnc/status", JsonSerializer.Serialize(new { progress = Math.Round(progress * 100, 1), elapsed_seconds = Math.Round(cutElapsed, 1) }));
                    lock (modeLock) { startModeEnabled = false; }
                    return;
                }
                SafePublish(mqtt, "cnc/position", JsonSerializer.Serialize(new
                {
                    x = Math.Round(point.X, 3),
                    y = Math.Round(point.Y, 3),
                    z = Math.Round(point.Z, 3),
                    feedrate = point.Feedrate,
                    mode = point.IsRapid ? "rapid" : "cut"
                }));
                SafePublish(mqtt, "cnc/head/temperature", JsonSerializer.Serialize(new
                {
                    temperature = Math.Round(temperature, 2),
                    setpoint = _config.CuttingTemperature,
                    state = anomaly ? "anomaly" : "at_temperature"
                }));
                PublishState(mqtt, "running");
                SafePublish(mqtt, "cnc/status", JsonSerializer.Serialize(new
                {
                    progress = Math.Round(progress * 100, 1),
                    elapsed_seconds = Math.Round(cutElapsed, 1),
                    point = pointIndex,
                    total_points = totalPoints
                }));
                if (pointIndex % 20 == 0)
                {
                    Console.WriteLine($"[CNC] X={point.X:F1} Y={point.Y:F1} Z={point.Z:F1} | " +
                                      $"Temp={temperature:F1}°C | Progress={progress * 100:F0}%");
                }
                pointIndex++;
                await Task.Delay((int)(StepInterval * 1000), cts.Token);
            }
            if (!cts.Token.IsCancellationRequested)
            {
                SafePublish(mqtt, "cnc/position", JsonSerializer.Serialize(new { x = 0.0, y = 0.0, z = SafeZ, feedrate = DefaultRapidRate, mode = "home" }));
                PublishState(mqtt, "idle");
                SafePublish(mqtt, "cnc/status", JsonSerializer.Serialize(new { progress = 100.0, elapsed_seconds = (DateTime.UtcNow - cutStart).TotalSeconds }));
                Console.WriteLine("[CNC] Job complete. Returned to home.");
            }
        }
        private double ComputeHeadTemperature(double totalElapsedSeconds, double warmupSeconds)
        {
            double noise = (_rng.NextDouble() - 0.5) * 2.0 * _config.TemperatureNoise;
            double target = _config.CuttingTemperature;
            bool anomaly;
            DateTime anomalyStart;
            lock (modeLock) { anomaly = _anomalyTriggered; anomalyStart = _anomalyStart; }
            double baseTemp;
            if (totalElapsedSeconds < warmupSeconds)
            {
                double t = totalElapsedSeconds / warmupSeconds;
                baseTemp = 25.0 + (target - 25.0) * t;
            }
            else
            {
                baseTemp = target;
            }
            if (anomaly && anomalyStart != DateTime.MaxValue)
            {
                double anomalySeconds = (DateTime.UtcNow - anomalyStart).TotalSeconds;
                baseTemp += anomalySeconds * _config.AnomalyRiseRate;
            }
            return baseTemp + noise;
        }
        private List<ToolpathPoint> LoadToolpathFromGCode(string filePath)
        {
            var points = new List<ToolpathPoint>();
            var wordRe = new Regex(@"([A-Za-z])([+-]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            double curX = 0, curY = 0, curZ = 0;
            double curFeedrate = DefaultFeedRate;
            bool isRapid = false;
            bool absoluteMode = true;
            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                // Strip parenthesis comments and semicolon comments
                var line = Regex.Replace(rawLine, @"\(.*?\)", "");
                var semi = line.IndexOf(';');
                if (semi >= 0) line = line[..semi];
                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                double? newX = null, newY = null, newZ = null;
                bool programEnd = false;
                foreach (Match m in wordRe.Matches(line))
                {
                    char letter = char.ToUpper(m.Groups[1].Value[0]);
                    double value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    switch (letter)
                    {
                        case 'G':
                            switch ((int)value)
                            {
                                case 0:  isRapid = true;  break;
                                case 1:  isRapid = false; break;
                                case 90: absoluteMode = true;  break;
                                case 91: absoluteMode = false; break;
                                // G17, G20, G21, G94 etc. — ignore
                            }
                            break;
                        case 'X': newX = absoluteMode ? value : curX + value; break;
                        case 'Y': newY = absoluteMode ? value : curY + value; break;
                        case 'Z': newZ = absoluteMode ? value : curZ + value; break;
                        case 'F': curFeedrate = value; break;
                        case 'M':
                            if ((int)value is 2 or 30) programEnd = true;
                            break;
                    }
                }
                if (programEnd) break;
                if (newX.HasValue || newY.HasValue || newZ.HasValue)
                {
                    double tx = newX ?? curX;
                    double ty = newY ?? curY;
                    double tz = newZ ?? curZ;
                    double feedrate = isRapid ? DefaultRapidRate : curFeedrate;
                    InterpolateLine(points, curX, curY, curZ, tx, ty, tz, feedrate, isRapid);
                    curX = tx; curY = ty; curZ = tz;
                }
            }
            Console.WriteLine($"[CNC] Parsed {points.Count} interpolated points from G-code.");
            return points;
        }
        private void InterpolateLine(List<ToolpathPoint> points,
            double x0, double y0, double z0, double x1, double y1, double z1,
            double feedrate, bool isRapid)
        {
            double dist = Math.Sqrt(Math.Pow(x1 - x0, 2) + Math.Pow(y1 - y0, 2) + Math.Pow(z1 - z0, 2));
            if (dist < 0.001) return;
            double stepDist = Math.Max(0.01, feedrate / 60.0 * StepInterval);
            int steps = Math.Max(1, (int)(dist / stepDist));
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                points.Add(new ToolpathPoint(
                    x0 + (x1 - x0) * t,
                    y0 + (y1 - y0) * t,
                    z0 + (z1 - z0) * t,
                    isRapid,
                    feedrate));
            }
        }
        private static async Task WaitForMqttReadyAsync(PluginMain mqtt, CancellationToken ct)
        {
            // Probe TCP port 1883 until the embedded broker is actually accepting connections,
            // then allow a short stabilisation window for the MQTT client to authenticate.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    await tcp.ConnectAsync("localhost", 1883, ct);
                    // Broker is listening — give the MQTT client time to finish its handshake.
                    await Task.Delay(800, ct);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    await Task.Delay(200, ct);
                }
            }
        }
        private void PublishState(PluginMain mqtt, string state)
        {
            if (state == _lastState) return;
            _lastState = state;
            SafePublish(mqtt, "cnc/state", state);
        }
        private static void SafePublish(PluginMain mqtt, string topic, string value)
        {
            try
            {
                mqtt.Publish(topic, value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] MQTT publish skipped ({ex.GetType().Name}): {topic}");
            }
        }
        private record ToolpathPoint(double X, double Y, double Z, bool IsRapid, double Feedrate);
    }
}
