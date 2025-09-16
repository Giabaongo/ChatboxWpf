using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ChatboxWpf
{
    public partial class MainWindow : Window
    {
        private TcpListener server;
        private List<TcpClient> clients = new();
        private TcpClient client;
        private NetworkStream stream;
        private bool isServer = false;
        private const int MaxClients = 5;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show("Start as server?", "Chatbox", MessageBoxButton.YesNo);
            if (result == MessageBoxResult.Yes)
            {
                isServer = true;
                server = new TcpListener(IPAddress.Any, 9000);
                server.Start();
                ChatList.Items.Add("Waiting for clients...");

                _ = Task.Run(async () =>
                {
                    while (clients.Count < MaxClients)
                    {
                        var newClient = await server.AcceptTcpClientAsync();
                        lock (clients)
                        {
                            clients.Add(newClient);
                        }
                        Dispatcher.Invoke(() => ChatList.Items.Add($"Client {clients.Count} connected."));
                        _ = Task.Run(() => ReceiveMessagesFromClient(newClient));
                    }
                });
            }
            else
            {
                client = new TcpClient();
                var ip = Microsoft.VisualBasic.Interaction.InputBox("Enter server IP:", "Connect to Server", "127.0.0.1");
                await client.ConnectAsync(ip, 9000);
                ChatList.Items.Add("Connected to server.");
                stream = client.GetStream();
                _ = Task.Run(ReceiveMessagesFromServer);
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var message = MessageBox.Text;
            if (string.IsNullOrWhiteSpace(message)) return;

            if (isServer)
            {
                var data = Encoding.UTF8.GetBytes($"Server: {message}");
                lock (clients)
                {
                    foreach (var c in clients)
                    {
                        try
                        {
                            var s = c.GetStream();
                            if (s.CanWrite)
                                s.WriteAsync(data, 0, data.Length);
                        }
                        catch { }
                    }
                }
                ChatList.Items.Add($"Me: {message}");
            }
            else
            {
                if (stream == null || !stream.CanWrite) return;
                var data = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(data, 0, data.Length);
                ChatList.Items.Add($"Me: {message}");
            }
            MessageBox.Clear();
        }

        private async Task ReceiveMessagesFromClient(TcpClient senderClient)
        {
            var buffer = new byte[1024];
            var senderStream = senderClient.GetStream();
            while (true)
            {
                try
                {
                    int bytesRead = await senderStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Dispatcher.Invoke(() => ChatList.Items.Add(message));
                    BroadcastMessage(message, senderClient);
                }
                catch
                {
                    break;
                }
            }
            lock (clients)
            {
                clients.Remove(senderClient);
            }
            Dispatcher.Invoke(() => ChatList.Items.Add("A client disconnected."));
        }

        private void BroadcastMessage(string message, TcpClient sender)
        {
            var data = Encoding.UTF8.GetBytes(message);
            lock (clients)
            {
                foreach (var c in clients)
                {
                    if (c == sender) continue;
                    try
                    {
                        var s = c.GetStream();
                        if (s.CanWrite)
                            s.WriteAsync(data, 0, data.Length);
                    }
                    catch { }
                }
            }
        }

        private async Task ReceiveMessagesFromServer()
        {
            var buffer = new byte[1024];
            while (true)
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;
                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Dispatcher.Invoke(() => ChatList.Items.Add($"Peer: {message}"));
                }
                catch
                {
                    break;
                }
            }
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            EmojiPopup.IsOpen = true;
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string emoji)
            {
                MessageBox.Text += emoji;
                EmojiPopup.IsOpen = false;
                MessageBox.Focus();
                MessageBox.CaretIndex = MessageBox.Text.Length;
            }
        }
    }
}