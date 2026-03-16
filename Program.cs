namespace MQTTSimulator
{
    internal class Program
    {
        private static async System.Threading.Tasks.Task Main(string[] args)
        {
            // Suppress unhandled exceptions from fire-and-forget MQTT publish tasks.
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Console.Error.WriteLine($"[WARN] Suppressed unobserved task exception: {e.Exception.GetBaseException().Message}");
                e.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (ex?.GetType().Name.Contains("NotConnected") == true ||
                    ex?.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Console.Error.WriteLine($"[WARN] MQTT not connected (unhandled): {ex.Message}");
                }
            };
            Console.WriteLine("Starting Scenario Run");
            await new ScenarioRunner().RunAsync(args);
            Console.WriteLine("Finished Scenario Run");
        }
    }
}