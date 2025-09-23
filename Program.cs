using System;
using System.Threading.Tasks;

namespace ChatServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== WhatsApp95 Chat Server ===");
            Console.WriteLine("Starting server...");
            Console.WriteLine("Commands: /list, /stop, /help");
            Console.WriteLine();

            var chatServer = new ChatServer();
            
            // Subscribe to server messages for logging
            chatServer.ServerMessage += (sender, message) => {
                // Messages are already printed in ChatServer class
                // This can be used for additional logging if needed
            };

            try
            {
                await chatServer.StartAsync(8888);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Server error: {ex.Message}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }
    }
}