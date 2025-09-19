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
        private TcpListener messageServer;
        private TcpListener fileServer;
        private List<TcpClient> messageClients = new();
        private List<TcpClient> fileClients = new();
        private TcpClient messageClient;
        private TcpClient fileClient;
        private NetworkStream messageStream;
        private NetworkStream fileStream;
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
                await StartServers();
                ChatList.Items.Add("Server started. Waiting for clients...");
            }
            else
            {
                try
                {
                    myUsername = Microsoft.VisualBasic.Interaction.InputBox("Enter your username:", "Username", "Guest");
                    if (string.IsNullOrWhiteSpace(myUsername))
                        throw new Exception("Username is required.");

                    var ip = Microsoft.VisualBasic.Interaction.InputBox("Enter server IP:", "Connect to Server", "127.0.0.1");
                    if (string.IsNullOrWhiteSpace(ip))
                        throw new Exception("Server IP is required.");

                    // Connect for messages
                    messageClient = new TcpClient();
                    await messageClient.ConnectAsync(ip, 9000);
                    messageStream = messageClient.GetStream();

                    // Connect for files
                    fileClient = new TcpClient();
                    await fileClient.ConnectAsync(ip, 9001);
                    fileStream = fileClient.GetStream();

                    ChatList.Items.Add("Connected to server.");

                    // Send username
                    var userMsg = $"USER|{myUsername}\n";
                    await messageStream.WriteAsync(Encoding.UTF8.GetBytes(userMsg));

                    _ = Task.Run(ReceiveMessagesFromServer);
                    _ = Task.Run(ReceiveFilesFromServer);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message + "\nApplication will exit.");
                    Application.Current.Shutdown();
                }
            }
        }

        private async Task StartServers()
        {
            // Message server
            messageServer = new TcpListener(IPAddress.Any, 9000);
            messageServer.Start();

            // File server
            fileServer = new TcpListener(IPAddress.Any, 9001);
            fileServer.Start();

            _ = Task.Run(AcceptMessageClients);
            _ = Task.Run(AcceptFileClients);
        }

        private async Task AcceptMessageClients()
        {
            while (messageClients.Count < MaxClients)
            {
                try
                {
                    var client = await messageServer.AcceptTcpClientAsync();
                    lock (messageClients) { messageClients.Add(client); }
                    Dispatcher.Invoke(() => ChatList.Items.Add($"Message client {messageClients.Count} connected."));
                    _ = Task.Run(() => ReceiveMessagesFromClient(client));
                }
                catch { break; }
            }
        }

        private async Task AcceptFileClients()
        {
            while (fileClients.Count < MaxClients)
            {
                try
                {
                    var fileClient = await fileServer.AcceptTcpClientAsync();
                    lock (fileClients) { fileClients.Add(fileClient); }
                    Dispatcher.Invoke(() => ChatList.Items.Add($"File client {fileClients.Count} connected."));
                    _ = Task.Run(() => ReceiveFilesFromClient(fileClient));
                }
                catch { break; }
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var message = MessageInput.Text;
            if (string.IsNullOrWhiteSpace(message)) return;

            if (isServer)
            {
                var fullMessage = $"Server: {message}";
                var data = Encoding.UTF8.GetBytes(fullMessage + "\n");

                List<TcpClient> snapshot;
                lock (messageClients)
                {
                    snapshot = new List<TcpClient>(messageClients);
                }

                foreach (var c in snapshot)
                {
                    try
                    {
                        var s = c.GetStream();
                        if (s.CanWrite)
                            await s.WriteAsync(data, 0, data.Length);
                    }
                    catch
                    {
                        lock (messageClients)
                        {
                            messageClients.Remove(c);
                            clientNames.Remove(c);
                        }
                    }
                }
                Dispatcher.Invoke(() => ChatList.Items.Add($"Me: {message}"));
            }
            else
            {
                if (messageStream == null || !messageStream.CanWrite) return;
                var fullMessage = $"MSG|{myUsername}: {message}";
                var data = Encoding.UTF8.GetBytes(fullMessage + "\n");
                await messageStream.WriteAsync(data, 0, data.Length);
                Dispatcher.Invoke(() => ChatList.Items.Add($"Me: {message}"));
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
            return HandleIncomingMessages(senderStream, senderClient);
        }

        private Task ReceiveMessagesFromServer()
        {
            return HandleIncomingMessages(messageStream);
        }

        private Task ReceiveFilesFromClient(TcpClient fileClient)
        {
            var fileStream = fileClient.GetStream();
            return HandleIncomingFiles(fileStream, fileClient);
        }

        private Task ReceiveFilesFromServer()
        {
            return HandleIncomingFiles(fileStream);
        }

        private async void BroadcastMessage(string message, TcpClient sender)
        {
            var data = Encoding.UTF8.GetBytes(message + "\n");
            List<TcpClient> snapshot;
            lock (messageClients)
            {
                snapshot = new List<TcpClient>(messageClients);
            }

            foreach (var c in snapshot)
            {
                if (c == sender) continue;
                try
                {
                    var s = c.GetStream();
                    if (s.CanWrite)
                        await s.WriteAsync(data, 0, data.Length);
                }
                catch
                {
                    lock (messageClients)
                    {
                        messageClients.Remove(c);
                        clientNames.Remove(c);
                    }
                }
            }
        }

        private async Task HandleIncomingMessages(NetworkStream ns, TcpClient sender = null)
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            while (true)
            {
                try
                {
                    int bytesRead = await ns.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(chunk);

                    string data = messageBuilder.ToString();
                    string[] lines = data.Split('\n');

                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        string line = lines[i].Trim();
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        await ProcessMessage(line, sender);
                    }

                    messageBuilder.Clear();
                    if (lines.Length > 0 && !data.EndsWith('\n'))
                    {
                        messageBuilder.Append(lines[lines.Length - 1]);
                    }
                }
                catch
                {
                    break;
                }
            }

            if (sender != null)
            {
                lock (messageClients)
                {
                    messageClients.Remove(sender);
                    if (clientNames.ContainsKey(sender))
                    {
                        var name = clientNames[sender];
                        clientNames.Remove(sender);
                        Dispatcher.Invoke(() => ChatList.Items.Add($"{name} disconnected."));
                    }
                }
            }
        }

        private async Task ProcessMessage(string message, TcpClient sender)
        {
            if (message.StartsWith("USER|"))
            {
                var parts = message.Split('|', 2);
                if (parts.Length >= 2)
                {
                    string name = parts[1];
                    if (sender != null)
                    {
                        lock (clientNames)
                        {
                            clientNames[sender] = name;
                        }
                    }
                    Dispatcher.Invoke(() => ChatList.Items.Add($"{name} joined."));

                    if (isServer && sender != null)
                    {
                        BroadcastMessage($"{name} joined.", sender);
                    }
                }
            }
            else if (message.StartsWith("MSG|"))
            {
                var parts = message.Split('|', 2);
                if (parts.Length >= 2)
                {
                    string chatMessage = parts[1];
                    Dispatcher.Invoke(() => ChatList.Items.Add(chatMessage));

                    if (isServer && sender != null)
                    {
                        BroadcastMessage($"MSG|{chatMessage}", sender);
                    }
                }
            }
            else
            {
                Dispatcher.Invoke(() => ChatList.Items.Add(message));
            }
        }

        private async Task HandleIncomingFiles(NetworkStream ns, TcpClient sender = null)
        {
            var buffer = new byte[32 * 1024];

            while (true)
            {
                try
                {
                    // Read file header first
                    var headerBuffer = new byte[1024];
                    int headerBytes = await ns.ReadAsync(headerBuffer, 0, headerBuffer.Length);
                    if (headerBytes == 0) break;

                    string headerData = Encoding.UTF8.GetString(headerBuffer, 0, headerBytes);
                    var lines = headerData.Split('\n');
                    string header = lines[0];

                    if (header.StartsWith("FILE|"))
                    {
                        await HandleFileTransfer(header, ns, sender);
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task HandleFileTransfer(string header, NetworkStream ns, TcpClient sender)
        {
            var parts = header.Split('|');
            if (parts.Length < 3) return;

            string fileName = parts[1];
            if (!long.TryParse(parts[2], out long fileSize)) return;

            string senderName = "Unknown";
            if (parts.Length >= 4)
            {
                senderName = parts[3];
            }
            else if (sender != null && clientNames.ContainsKey(sender))
            {
                senderName = clientNames[sender];
            }

            Dispatcher.Invoke(() => ChatList.Items.Add($"{senderName}: [Sending file: {fileName}]"));

            if (isServer && sender != null)
            {
                await ReceiveAndBroadcastFile(fileName, fileSize, ns, sender, senderName);
            }
            else
            {
                await ReceiveAndSaveFile(fileName, fileSize, ns, senderName);
            }
        }

        private async Task ReceiveAndBroadcastFile(string fileName, long fileSize, NetworkStream senderStream, TcpClient sender, string senderName)
        {
            try
            {
                List<TcpClient> targetClients;
                lock (fileClients)
                {
                    targetClients = fileClients.Where(c => c != sender).ToList();
                }

                if (targetClients.Count > 0)
                {
                    string header = $"FILE|{fileName}|{fileSize}|{senderName}\n";
                    var headerBytes = Encoding.UTF8.GetBytes(header);

                    foreach (var client in targetClients)
                    {
                        try
                        {
                            await client.GetStream().WriteAsync(headerBytes, 0, headerBytes.Length);
                        }
                        catch { }
                    }
                }

                var buffer = new byte[32 * 1024];
                long totalReceived = 0;

                while (totalReceived < fileSize)
                {
                    int toRead = (int)Math.Min(buffer.Length, fileSize - totalReceived);
                    int bytesRead = await senderStream.ReadAsync(buffer, 0, toRead);
                    if (bytesRead == 0) break;

                    totalReceived += bytesRead;

                    foreach (var client in targetClients)
                    {
                        try
                        {
                            await client.GetStream().WriteAsync(buffer, 0, bytesRead);
                        }
                        catch
                        {
                            lock (fileClients)
                            {
                                fileClients.Remove(client);
                            }
                        }
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    ChatList.Items.Add($"Server: Relayed file [{fileName}] from {senderName}");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ChatList.Items.Add($"Error relaying file: {ex.Message}"));
            }
        }

        private async Task ReceiveAndSaveFile(string fileName, long fileSize, NetworkStream ns, string senderName)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"Received_{DateTime.Now.Ticks}_{fileName}");

            try
            {
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[32 * 1024];
                    long totalReceived = 0;

                    while (totalReceived < fileSize)
                    {
                        int toRead = (int)Math.Min(buffer.Length, fileSize - totalReceived);
                        int bytesRead = await ns.ReadAsync(buffer, 0, toRead);
                        if (bytesRead == 0) break;

                        await fs.WriteAsync(buffer, 0, bytesRead);
                        totalReceived += bytesRead;
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    var item = new ListBoxItem
                    {
                        Content = $"{senderName}: [File received: {fileName}] (Double-click to save)",
                        Tag = tempPath
                    };
                    item.MouseDoubleClick += FileItem_DoubleClick;
                    ChatList.Items.Add(item);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ChatList.Items.Add($"Error receiving file: {ex.Message}"));

                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
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
                string header = $"FILE|{fileName}|{fileSize}|{(isServer ? "Server" : myUsername)}";
                var headerBytes = Encoding.UTF8.GetBytes(header + "\n");

                if (isServer)
                {
                    List<TcpClient> snapshot;
                    lock (fileClients) { snapshot = new List<TcpClient>(fileClients); }
                    foreach (var c in snapshot)
                    {
                        try
                        {
                            await c.GetStream().WriteAsync(headerBytes, 0, headerBytes.Length);
                        }
                        catch { }
                    }
                }
                else
                {
                    await fileStream.WriteAsync(headerBytes, 0, headerBytes.Length);
                }

                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    var buffer = new byte[32 * 1024];
                    int bytesRead;

                    while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        if (isServer)
                        {
                            List<TcpClient> snapshot;
                            lock (fileClients) { snapshot = new List<TcpClient>(fileClients); }
                            foreach (var c in snapshot)
                            {
                                try
                                {
                                    await c.GetStream().WriteAsync(buffer, 0, bytesRead);
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                        }
                    }
                }

                Dispatcher.Invoke(() => ChatList.Items.Add($"Me: [File sent: {fileName}]"));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ChatList.Items.Add($"Error sending file: {ex.Message}"));
            }
        }

        private void FileItem_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem item && item.Tag is string tempPath)
            {
                try
                {
                    var dialog = new SaveFileDialog
                    {
                        FileName = Path.GetFileName(tempPath).Replace($"Received_{Path.GetFileName(tempPath).Split('_')[1]}_", ""),
                        Filter = "All Files|*.*"
                    };

                    if (dialog.ShowDialog() == true)
                    {
                        File.Copy(tempPath, dialog.FileName, true);
                        MessageBox.Show("File saved to: " + dialog.FileName);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving file: {ex.Message}");
                }
            }
        }
    }
}