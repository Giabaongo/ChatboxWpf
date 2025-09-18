using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        private Dictionary<TcpClient, string> clientNames = new();
        private string myUsername = "Me";

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Start as server?", "Chatbox", MessageBoxButton.YesNo);
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
                try
                {
                    myUsername = Microsoft.VisualBasic.Interaction.InputBox("Enter your username:", "Username", "Guest");
                    if (string.IsNullOrWhiteSpace(myUsername))
                        throw new Exception("Username is required.");

                    client = new TcpClient();
                    var ip = Microsoft.VisualBasic.Interaction.InputBox("Enter server IP:", "Connect to Server", "127.0.0.1");
                    if (string.IsNullOrWhiteSpace(ip))
                        throw new Exception("Server IP is required.");

                    await client.ConnectAsync(ip, 9000);
                    ChatList.Items.Add("Connected to server.");
                    stream = client.GetStream();

                    await stream.WriteAsync(Encoding.UTF8.GetBytes("USER|" + myUsername + "\n"));

                    _ = Task.Run(ReceiveMessagesFromServer);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message + "\nApplication will exit.");
                    Application.Current.Shutdown();
                }
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var message = MessageInput.Text;
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
                var data = Encoding.UTF8.GetBytes($"{myUsername}: {message}");
                await stream.WriteAsync(data, 0, data.Length);
                ChatList.Items.Add($"{myUsername}: {message}");
            }
            MessageInput.Clear();
        }

        private void MessageInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                Send_Click(sender, e);
                e.Handled = true;
            }
        }

        private Task ReceiveMessagesFromClient(TcpClient senderClient)
        {
            var senderStream = senderClient.GetStream();
            return HandleIncomingData(senderStream, senderClient);
        }

        private Task ReceiveMessagesFromServer()
        {
            return HandleIncomingData(stream);
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

        //Emoji
        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            EmojiPopup.IsOpen = true;
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string emoji)
            {
                MessageInput.Text += emoji;
                EmojiPopup.IsOpen = false;
                MessageInput.Focus();
                MessageInput.CaretIndex = MessageInput.Text.Length;
            }
        }

        //Upload files
        private void SendFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog();
            if (dialog.ShowDialog() == true)
            {
                string filePath = dialog.FileName;
                string fileName = Path.GetFileName(filePath);
                long fileSize = new FileInfo(filePath).Length;

                _ = Task.Run(() => SendFileAsync(filePath, fileName, fileSize));
            }
        }
        private async Task SendFileAsync(string filePath, string fileName, long fileSize)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);

                await SendFileHeaderAsync(fileName, fileSize);
                await SendFileChunksAsync(fs, fileSize, fileName);

                Dispatcher.Invoke(() =>
                {
                    ChatList.Items.Add($"Me: [File sent: {fileName}]");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ChatList.Items.Add($"Error sending file: {ex.Message}"));
            }
        }

        private async Task SendFileHeaderAsync(string fileName, long fileSize)
        {
            string senderName = isServer ? "Server" : myUsername;
            string header = $"FILE|{senderName}|{fileName}|{fileSize}\n";
            byte[] headerBytes = Encoding.UTF8.GetBytes(header);

            if (isServer)
            {
                lock (clients)
                {
                    foreach (var c in clients)
                    {
                        var s = c.GetStream();
                        s.Write(headerBytes, 0, headerBytes.Length);
                    }
                }
            }
            else
            {
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            }
        }

        private async Task SendFileChunksAsync(FileStream fs, long fileSize, string fileName)
        {
            byte[] buffer = new byte[81920];
            long totalSent = 0;
            int bytesRead;

            while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if (isServer)
                {
                    List<TcpClient> snapshot;
                    lock (clients)
                    {
                        snapshot = new List<TcpClient>(clients);
                    }

                    foreach (var c in snapshot)
                    {
                        try
                        {
                            var s = c.GetStream();
                            await s.WriteAsync(buffer, 0, bytesRead);
                        }
                        catch { }
                    }
                }
                else
                {
                    await stream.WriteAsync(buffer, 0, bytesRead);
                }
            }
        }
        private async Task HandleIncomingData(NetworkStream ns, TcpClient sender = null)
        {
            var buffer = new byte[81920];

            while (true)
            {
                try
                {
                    int bytesRead = await ns.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    if (chunk.StartsWith("USER|"))
                    {
                        string name = chunk.Substring(5).Trim();
                        if (sender != null)
                        {
                            lock (clientNames) clientNames[sender] = name;
                        }
                        Dispatcher.Invoke(() => ChatList.Items.Add($"{name} joined."));
                    } else if (chunk.StartsWith("FILE|"))
                    {
                        await ReceiveFileAsync(chunk, ns, buffer);
                    }
                    else
                    {
                        Dispatcher.Invoke(() => ChatList.Items.Add(chunk));
                        if (isServer && sender != null) BroadcastMessage(chunk, sender);
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task ReceiveFileAsync(string header, NetworkStream ns, byte[] buffer)
        {
            var parts = header.Split('|');
            string senderName = parts[1];
            string fileName = parts[2];
            long fileSize = long.Parse(parts[3]);

            string tempPath = Path.Combine(Path.GetTempPath(), $"Received_{fileName}");
            using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write);

            long totalReceived = 0;

            while (totalReceived < fileSize)
            {
                int read = await ns.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break;
                await fs.WriteAsync(buffer, 0, read);
                totalReceived += read;
            }

            Dispatcher.Invoke(() =>
            {
                var item = new ListBoxItem
                {
                    Content = $"{senderName}: [Sent file: {fileName}] (Click to save)",
                    Tag = tempPath
                };
                item.MouseDoubleClick += FileItem_DoubleClick;
                ChatList.Items.Add(item);
            });
        }

        private void FileItem_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.Tag is string tempPath)
            {
                var dialog = new SaveFileDialog
                {
                    FileName = Path.GetFileName(tempPath),
                    Filter = "All Files|*.*"
                };

                if (dialog.ShowDialog() == true)
                {
                    File.Copy(tempPath, dialog.FileName, true);
                    MessageBox.Show("File saved to: " + dialog.FileName);
                }
            }
        }
    }
}