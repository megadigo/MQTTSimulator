namespace MQTTSimulator
{
    internal class Program
    {
        private static async System.Threading.Tasks.Task Main(string[] args)
        {
            Console.WriteLine("Starting Scenario Run");
            await new ScenarioRunner().RunAsync(args);
            Console.WriteLine("Finished Scenario Run");
        }
    }
}