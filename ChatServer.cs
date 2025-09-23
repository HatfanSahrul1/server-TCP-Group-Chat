using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatServer
{
    public class ClientInfo
    {
        public TcpClient Client { get; set; }
        public string Username { get; set; }
        public NetworkStream Stream { get; set; }
        public string ClientId { get; set; }
    }

    public class ChatMessage
    {
        public string Type { get; set; }          // "msg", "join", "leave", "pm", "sys"
        public string From { get; set; }
        public string To { get; set; }
        public string Text { get; set; }
        public long Ts { get; set; }             // timestamp
    }

    public class ChatServer
    {
        private TcpListener _server;
        private List<ClientInfo> _clients = new List<ClientInfo>();
        private readonly object _lock = new object();
        private bool _isRunning = false;

        public event EventHandler<string> ServerMessage;

        public async Task StartAsync(int port = 8888)
        {
            try
            {
                _server = new TcpListener(IPAddress.Any, port);
                _server.Start();
                _isRunning = true;

                OnServerMessage($"Server started on port {port}");
                OnServerMessage("Waiting for connections...");

                // Handle server commands in background
                _ = Task.Run(HandleServerCommands);

                while (_isRunning)
                {
                    try
                    {
                        var client = await _server.AcceptTcpClientAsync();
                        _ = Task.Run(() => HandleClient(client));
                    }
                    catch (ObjectDisposedException)
                    {
                        // Server was stopped
                        break;
                    }
                    catch (Exception ex)
                    {
                        OnServerMessage($"Error accepting client: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                OnServerMessage($"Failed to start server: {ex.Message}");
            }
        }

        public void Stop()
        {
            _isRunning = false;
            _server?.Stop();
            
            // Disconnect all clients
            lock (_lock)
            {
                foreach (var client in _clients.ToArray())
                {
                    try
                    {
                        client.Client?.Close();
                    }
                    catch { }
                }
                _clients.Clear();
            }
            
            OnServerMessage("Server stopped");
        }

        private async Task HandleClient(TcpClient client)
        {
            ClientInfo clientInfo = null;
            string clientId = Guid.NewGuid().ToString()[..8];

            try
            {
                NetworkStream stream = client.GetStream();

                // Send welcome message
                var welcomeMsg = new ChatMessage
                {
                    Type = "sys",
                    From = "Server",
                    Text = "Welcome to WhatsApp95 Chat Server! Please enter your username:",
                    Ts = GetTimestamp()
                };
                await SendMessage(welcomeMsg, stream);

                // Wait for username (join message)
                var joinMessage = await ReceiveMessage(stream);
                if (joinMessage == null)
                {
                    client.Close();
                    return;
                }

                if (joinMessage.Type != "join")
                {
                    await SendMessage(new ChatMessage
                    {
                        Type = "sys",
                        From = "Server",
                        Text = "Invalid connection attempt. Disconnecting...",
                        Ts = GetTimestamp()
                    }, stream);
                    client.Close();
                    return;
                }

                string username = joinMessage.From;

                // Check if username already exists
                lock (_lock)
                {
                    if (_clients.Exists(c => c.Username == username))
                    {
                        username = $"{username}_{clientId}";
                    }

                    clientInfo = new ClientInfo
                    {
                        Client = client,
                        Username = username,
                        Stream = stream,
                        ClientId = clientId
                    };
                    _clients.Add(clientInfo);
                }

                // Notify all clients about new user
                var joinNotification = new ChatMessage
                {
                    Type = "join",
                    From = username,
                    Text = $"{username} bergabung dalam percakapan",
                    Ts = GetTimestamp()
                };
                await BroadcastMessage(joinNotification);

                OnServerMessage($"[{DateTime.Now}] {username} connected from {client.Client.RemoteEndPoint}");

                // Send connection success message to the new client
                await SendMessage(new ChatMessage
                {
                    Type = "sys",
                    From = "Server",
                    Text = $"Connected as {username}. Type /w username message for private messages.",
                    Ts = GetTimestamp()
                }, stream);

                // Send current member list to new client
                await SendMemberListToClient(clientInfo);

                // Main message handling loop
                while (client.Connected && _isRunning)
                {
                    var message = await ReceiveMessage(stream);
                    if (message == null) break;

                    await ProcessMessage(message, clientInfo);
                }
            }
            catch (Exception ex)
            {
                OnServerMessage($"Error with client {clientId}: {ex.Message}");
            }
            finally
            {
                if (clientInfo != null)
                {
                    await HandleClientDisconnect(clientInfo);
                }
                client?.Close();
            }
        }

        private async Task ProcessMessage(ChatMessage message, ClientInfo clientInfo)
        {
            switch (message.Type)
            {
                case "msg": // Public message
                    message.From = clientInfo.Username;
                    message.Ts = GetTimestamp();
                    await BroadcastMessage(message);
                    OnServerMessage($"[{DateTime.Now}] {clientInfo.Username}: {message.Text}");
                    break;

                case "pm": // Private message
                    message.From = clientInfo.Username;
                    message.Ts = GetTimestamp();
                    await SendPrivateMessage(message);
                    OnServerMessage($"[{DateTime.Now}] {clientInfo.Username} -> {message.To}: {message.Text}");
                    break;

                case "leave": // Client leaving
                    await HandleClientDisconnect(clientInfo);
                    break;

                default:
                    await SendMessage(new ChatMessage
                    {
                        Type = "sys",
                        From = "Server",
                        Text = "Unknown message type",
                        Ts = GetTimestamp()
                    }, clientInfo.Stream);
                    break;
            }
        }

        private async Task SendMemberListToClient(ClientInfo clientInfo)
        {
            List<string> members;
            lock (_lock)
            {
                members = _clients.Select(c => c.Username).ToList();
            }

            foreach (string member in members)
            {
                if (member != clientInfo.Username)
                {
                    var memberJoinMsg = new ChatMessage
                    {
                        Type = "join",
                        From = member,
                        Text = $"{member} is in the chat",
                        Ts = GetTimestamp()
                    };
                    await SendMessage(memberJoinMsg, clientInfo.Stream);
                }
            }
        }

        private async Task SendPrivateMessage(ChatMessage message)
        {
            List<ClientInfo> targets = new List<ClientInfo>();

            lock (_lock)
            {
                // Find target user
                var targetClient = _clients.Find(c => c.Username == message.To);
                if (targetClient != null)
                {
                    targets.Add(targetClient);
                }
                // Also send to sender for their own chat window
                var senderClient = _clients.Find(c => c.Username == message.From);
                if (senderClient != null)
                {
                    targets.Add(senderClient);
                }
            }

            if (targets.Count == 0)
            {
                // Target not found, send error to sender
                var senderClient = _clients.Find(c => c.Username == message.From);
                if (senderClient != null)
                {
                    await SendMessage(new ChatMessage
                    {
                        Type = "sys",
                        From = "Server",
                        Text = $"User '{message.To}' not found",
                        Ts = GetTimestamp()
                    }, senderClient.Stream);
                }
                return;
            }

            var tasks = new List<Task>();
            foreach (var target in targets)
            {
                tasks.Add(SendMessage(message, target.Stream));
            }
            await Task.WhenAll(tasks);
        }

        private async Task BroadcastMessage(ChatMessage message, ClientInfo excludeClient = null)
        {
            List<ClientInfo> clientsToSend;

            lock (_lock)
            {
                clientsToSend = new List<ClientInfo>(_clients);
            }

            var tasks = new List<Task>();
            foreach (var client in clientsToSend)
            {
                if (excludeClient == null || client != excludeClient)
                {
                    tasks.Add(SendMessage(message, client.Stream));
                }
            }
            await Task.WhenAll(tasks);
        }

        private async Task SendMessage(ChatMessage message, NetworkStream stream)
        {
            try
            {
                string json = JsonSerializer.Serialize(message);
                byte[] messageBytes = Encoding.UTF8.GetBytes(json);
                byte[] lengthBytes = BitConverter.GetBytes(messageBytes.Length);
                
                // Send length first, then message
                await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
            }
            catch (Exception ex)
            {
                OnServerMessage($"Error sending message: {ex.Message}");
            }
        }

        private async Task<ChatMessage> ReceiveMessage(NetworkStream stream)
        {
            try
            {
                // Read message length first (4 bytes)
                byte[] lengthBytes = new byte[4];
                int bytesRead = 0;
                while (bytesRead < 4)
                {
                    int read = await stream.ReadAsync(lengthBytes, bytesRead, 4 - bytesRead);
                    if (read == 0) return null; // Connection closed
                    bytesRead += read;
                }

                int messageLength = BitConverter.ToInt32(lengthBytes, 0);
                if (messageLength <= 0 || messageLength > 65536) // Sanity check
                {
                    OnServerMessage("Invalid message length received");
                    return null;
                }

                // Read the actual message
                byte[] messageBytes = new byte[messageLength];
                bytesRead = 0;
                while (bytesRead < messageLength)
                {
                    int read = await stream.ReadAsync(messageBytes, bytesRead, messageLength - bytesRead);
                    if (read == 0) return null; // Connection closed
                    bytesRead += read;
                }

                string jsonData = Encoding.UTF8.GetString(messageBytes);
                return JsonSerializer.Deserialize<ChatMessage>(jsonData);
            }
            catch (Exception ex)
            {
                OnServerMessage($"Error receiving message: {ex.Message}");
                return null;
            }
        }

        private async Task HandleClientDisconnect(ClientInfo clientInfo)
        {
            lock (_lock)
            {
                _clients.Remove(clientInfo);
            }

            var leaveMessage = new ChatMessage
            {
                Type = "leave",
                From = clientInfo.Username,
                Text = $"{clientInfo.Username} meninggalkan percakapan",
                Ts = GetTimestamp()
            };
            await BroadcastMessage(leaveMessage);

            OnServerMessage($"[{DateTime.Now}] {clientInfo.Username} disconnected");
        }

        private async Task HandleServerCommands()
        {
            while (_isRunning)
            {
                try
                {
                    string command = await Task.Run(() => Console.ReadLine());
                    
                    if (command == "/list")
                    {
                        lock (_lock)
                        {
                            OnServerMessage($"Connected clients ({_clients.Count}):");
                            foreach (var client in _clients)
                            {
                                OnServerMessage($"- {client.Username} ({client.ClientId})");
                            }
                        }
                    }
                    else if (command == "/stop")
                    {
                        OnServerMessage("Shutting down server...");
                        Stop();
                        Environment.Exit(0);
                    }
                    else if (command == "/help")
                    {
                        OnServerMessage("Server commands:");
                        OnServerMessage("/list - Show connected clients");
                        OnServerMessage("/stop - Shutdown server");
                        OnServerMessage("/help - Show this help");
                    }
                    else if (!string.IsNullOrWhiteSpace(command))
                    {
                        OnServerMessage("Unknown command. Type /help for available commands.");
                    }
                }
                catch (Exception ex)
                {
                    OnServerMessage($"Error in command handler: {ex.Message}");
                }
                
                await Task.Delay(100);
            }
        }

        private static long GetTimestamp()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private void OnServerMessage(string message)
        {
            Console.WriteLine(message);
            ServerMessage?.Invoke(this, message);
        }
    }
}