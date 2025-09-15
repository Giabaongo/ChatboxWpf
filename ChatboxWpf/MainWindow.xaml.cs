using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace ChatboxWpf
{
    public partial class MainWindow : Window
    {
        private TcpClient client;
        private TcpListener server;
        private NetworkStream stream;
        private bool isServer = false;

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
                ChatList.Items.Add("Waiting for client...");
                client = await server.AcceptTcpClientAsync();
                ChatList.Items.Add("Client connected.");
            }
            else
            {
                client = new TcpClient();
                var ip = Microsoft.VisualBasic.Interaction.InputBox("Enter server IP:", "Connect to Server", "127.0.0.1");
                await client.ConnectAsync(ip, 9000);
                ChatList.Items.Add("Connected to server.");
            }
            stream = client.GetStream();
            _ = Task.Run(ReceiveMessages);
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (stream == null || !stream.CanWrite) return;
            var message = MessageBox.Text;
            if (string.IsNullOrWhiteSpace(message)) return;
            var data = Encoding.UTF8.GetBytes(message);
            await stream.WriteAsync(data, 0, data.Length);
            ChatList.Items.Add($"Me: {message}");
            MessageBox.Clear();
        }

        private async Task ReceiveMessages()
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
    }
}
