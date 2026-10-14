using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TcpTerminal;

public class MainForm : Form
{
    // ---- connection bar ----
    private readonly ComboBox cmbMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly TextBox txtIp = new() { Text = "127.0.0.1", Width = 130 };
    private readonly TextBox txtPort = new() { Text = "5000", Width = 60 };
    private readonly Button btnConnect = new() { Text = "Connect", Width = 85 };
    private readonly Button btnStop = new() { Text = "Disconnect", Width = 85, Enabled = false };
    private readonly Button btnClear = new() { Text = "Clear", Width = 60 };

    // ---- options bar ----
    private readonly CheckBox chkHex = new() { Text = "Hex view", AutoSize = true, Margin = new Padding(6, 6, 12, 0) };
    private readonly NumericUpDown numMaxLines = new() { Minimum = 100, Maximum = 100000, Increment = 500, Value = 5000, Width = 70, Margin = new Padding(3, 3, 12, 0) };
    private readonly CheckBox chkReconnect = new() { Text = "Auto-reconnect", AutoSize = true, Margin = new Padding(6, 6, 12, 0) };
    private readonly CheckBox chkLogFile = new() { Text = "Log to file", AutoSize = true, Margin = new Padding(6, 6, 12, 0) };
    private readonly Button btnSaveLog = new() { Text = "Save Log...", Width = 90 };

    // ---- file bar: send file / watch folder ----
    private readonly Button btnSendFile = new() { Text = "Send File...", Width = 95 };
    private readonly NumericUpDown numLineDelay = new() { Minimum = 0, Maximum = 5000, Value = 10, Width = 60, Margin = new Padding(3, 3, 12, 0) };
    private readonly Button btnWatch = new() { Text = "Watch Folder...", Width = 120 };
    private readonly TextBox txtWatchFilter = new() { Text = "*.txt", Width = 70 };
    private readonly Label lblWatch = new() { Text = "", AutoSize = true, Margin = new Padding(10, 5, 2, 0) };

    // ---- console + send bar ----
    private readonly RichTextBox console = new();
    private readonly TextBox txtSend = new();
    private readonly CheckBox chkCrlf = new() { Text = "Add \\r\\n", Checked = true, AutoSize = true };
    private readonly Button btnSend = new() { Text = "Send", Width = 70 };
    private readonly Label lblStatus = new() { Text = "Disconnected", Dock = DockStyle.Bottom, Padding = new Padding(6, 3, 0, 3), Height = 24 };

    // ---- networking state ----
    private TcpClient? client;
    private TcpListener? listener;
    private NetworkStream? stream;
    private CancellationTokenSource? cts;

    // ---- receive buffering (line-by-line) ----
    // All of this runs on the UI thread (async continuations + WinForms timer), so no locks needed.
    private readonly List<byte> rxBuf = new();
    private long lastRxTick;
    private readonly System.Windows.Forms.Timer idleTimer = new() { Interval = 150 };
    private const int IdleFlushMs = 300;     // show a partial line if no new data for this long
    private const int MaxLineBytes = 8192;   // show a line anyway if it grows this big without a newline
    private const int ReconnectDelayMs = 3000;

    // ---- console / log state ----
    private int lineCount;
    private StreamWriter? logWriter;

    // ---- watch folder state ----
    private readonly System.Windows.Forms.Timer watchTimer = new() { Interval = 1000 };
    private string? watchFolder;
    private readonly Dictionary<string, long> watchOffsets = new();   // bytes already sent per file
    private readonly Dictionary<string, long> watchPrevLen = new();   // file length seen at previous poll
    private bool watchBusy;
    private bool notConnectedLogged;
    private readonly SemaphoreSlim sendLock = new(1, 1);              // one write at a time on the socket

    public MainForm()
    {
        Text = "TCP Terminal";
        Width = 900;
        Height = 620;
        MinimumSize = new Size(760, 440);

        cmbMode.Items.AddRange(new object[] { "Client", "Server" });
        cmbMode.SelectedIndex = 0;
        cmbMode.SelectedIndexChanged += (_, _) => ModeChanged();

        // top bar: mode / ip / port / buttons
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(6, 8, 6, 0), WrapContents = false };
        top.Controls.AddRange(new Control[]
        {
            Lbl("Mode"), cmbMode, Lbl("IP Address"), txtIp, Lbl("Port"), txtPort, btnConnect, btnStop, btnClear
        });

        // options bar: hex / max lines / auto-reconnect / log
        var options = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 2, 6, 0), WrapContents = false };
        options.Controls.AddRange(new Control[]
        {
            chkHex, Lbl("Max lines"), numMaxLines, chkReconnect, chkLogFile, btnSaveLog
        });

        // file bar: send file / watch folder
        var files = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 2, 6, 0), WrapContents = false };
        files.Controls.AddRange(new Control[]
        {
            btnSendFile, Lbl("Line delay (ms)"), numLineDelay, btnWatch, Lbl("Filter"), txtWatchFilter, lblWatch
        });

        // console (cmd-prompt look)
        console.Dock = DockStyle.Fill;
        console.BackColor = Color.FromArgb(12, 12, 12);
        console.ForeColor = Color.LightGray;
        console.Font = new Font("Consolas", 11);
        console.ReadOnly = true;
        console.BorderStyle = BorderStyle.None;

        // bottom send bar
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6) };
        btnSend.Dock = DockStyle.Right;
        chkCrlf.Dock = DockStyle.Right;
        txtSend.Dock = DockStyle.Fill;
        bottom.Controls.Add(txtSend);
        bottom.Controls.Add(chkCrlf);
        bottom.Controls.Add(btnSend);

        // later-added docked controls sit further out, so: top is outermost, options below it
        Controls.Add(console);
        Controls.Add(bottom);
        Controls.Add(files);
        Controls.Add(options);
        Controls.Add(top);
        Controls.Add(lblStatus);

        btnConnect.Click += async (_, _) => await StartAsync();
        btnStop.Click += (_, _) => Stop();
        btnClear.Click += (_, _) => { console.Clear(); lineCount = 0; };
        btnSend.Click += async (_, _) => await SendAsync();
        btnSaveLog.Click += (_, _) => SaveLog();
        btnSendFile.Click += async (_, _) => await SendFileAsync();
        btnWatch.Click += (_, _) => ToggleWatch();
        watchTimer.Tick += async (_, _) => await PollWatchAsync();
        chkLogFile.CheckedChanged += (_, _) => ToggleFileLog();
        txtSend.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendAsync(); }
        };

        // if data stops without a trailing newline, show what we have after a short idle time
        idleTimer.Tick += (_, _) =>
        {
            if (rxBuf.Count > 0 && Environment.TickCount64 - lastRxTick > IdleFlushMs) EmitRxLine();
        };
        idleTimer.Start();

        FormClosing += (_, _) =>
        {
            Stop(log: false);
            idleTimer.Stop();
            StopWatch(log: false);
            StopFileLog();
        };
    }

    private static Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(6, 5, 2, 0) };

    private void ModeChanged()
    {
        if (cmbMode.Text == "Server") { txtIp.Text = "0.0.0.0"; btnConnect.Text = "Listen"; }
        else { txtIp.Text = "127.0.0.1"; btnConnect.Text = "Connect"; }
    }

    // ================= console helpers =================

    private void Log(string text, Color color)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Log(text, color)); return; }

        console.SelectionStart = console.TextLength;
        console.SelectionColor = color;
        console.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
        console.ScrollToCaret();
        lineCount++;

        // live log file (keeps everything, even lines trimmed from the console)
        if (logWriter != null)
        {
            try { logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}"); }
            catch { StopFileLog(); chkLogFile.Checked = false; }
        }

        // max-lines limit: trim in small batches so we are not trimming on every line
        int max = (int)numMaxLines.Value;
        if (lineCount > max + Math.Max(50, max / 10)) TrimConsole(lineCount - max);
    }

    private void TrimConsole(int removeLines)
    {
        string text = console.Text;
        int idx = -1;
        for (int i = 0; i < removeLines; i++)
        {
            idx = text.IndexOf('\n', idx + 1);
            if (idx < 0) return;
        }
        console.Select(0, idx + 1);
        console.SelectedText = string.Empty;
        lineCount -= removeLines;
        console.SelectionStart = console.TextLength;
        console.ScrollToCaret();
    }

    private void SetStatus(string s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetStatus(s)); return; }
        lblStatus.Text = s;
    }

    private void SetButtons(bool running)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetButtons(running)); return; }
        btnConnect.Enabled = !running;
        btnStop.Enabled = running;
    }

    // ================= log to file / save log =================

    private void ToggleFileLog()
    {
        if (chkLogFile.Checked)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Text log (*.txt)|*.txt|All files (*.*)|*.*",
                FileName = $"tcp-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) { chkLogFile.Checked = false; return; }
            try
            {
                logWriter = new StreamWriter(dlg.FileName, append: true, Encoding.UTF8) { AutoFlush = true };
                Log($"Logging to {dlg.FileName}", Color.Gold);
            }
            catch (Exception ex)
            {
                Log($"Cannot open log file: {ex.Message}", Color.Tomato);
                chkLogFile.Checked = false;
            }
        }
        else
        {
            StopFileLog();
        }
    }

    private void StopFileLog()
    {
        try { logWriter?.Dispose(); } catch { }
        logWriter = null;
    }

    private void SaveLog()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Text log (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"tcp-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, console.Text.Replace("\n", Environment.NewLine), Encoding.UTF8);
            Log($"Saved console to {dlg.FileName}", Color.Gold);
        }
        catch (Exception ex)
        {
            Log($"Save failed: {ex.Message}", Color.Tomato);
        }
    }

    // ================= connection control =================

    private async Task StartAsync()
    {
        if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
        {
            Log("Invalid port (use 1-65535)", Color.Tomato);
            return;
        }
        string ip = txtIp.Text.Trim();
        bool isClient = cmbMode.Text == "Client";
        cts = new CancellationTokenSource();
        var token = cts.Token;
        SetButtons(true);

        try
        {
            if (isClient) await RunClientAsync(ip, port, token);
            else await RunServerAsync(ip, port, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Log($"Error: {ex.Message}", Color.Tomato);
        }
        finally
        {
            if (!token.IsCancellationRequested) Stop();
        }
    }

    private async Task RunClientAsync(string ip, int port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                SetStatus($"Connecting to {ip}:{port} ...");
                client = new TcpClient();

                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await client.ConnectAsync(ip, port, connectCts.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("Connection timed out (5 s)");
                    }
                }

                stream = client.GetStream();
                rxBuf.Clear();
                Log($"Connected to {ip}:{port}", Color.Gold);
                SetStatus($"Connected to {ip}:{port}");

                await ReceiveLoopAsync(stream, token);
                EmitRxLine();
                if (!token.IsCancellationRequested) Log("Connection closed by remote", Color.Gold);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) Log($"Connection failed: {ex.Message}", Color.Tomato);
            }
            finally
            {
                try { stream?.Close(); } catch { }
                try { client?.Close(); } catch { }
                stream = null; client = null;
            }

            if (token.IsCancellationRequested || !chkReconnect.Checked) break;

            Log($"Reconnecting in {ReconnectDelayMs / 1000} s ...", Color.Gold);
            SetStatus("Waiting to reconnect ...");
            await Task.Delay(ReconnectDelayMs, token);
        }
    }

    private async Task RunServerAsync(string ip, int port, CancellationToken token)
    {
        listener = new TcpListener(IPAddress.Parse(ip), port);
        listener.Start();
        Log($"Listening on {ip}:{port} ...", Color.Gold);
        SetStatus($"Listening on {ip}:{port}");

        while (!token.IsCancellationRequested)
        {
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
                stream = client.GetStream();
                rxBuf.Clear();
                var ep = client.Client.RemoteEndPoint;
                Log($"Client connected: {ep}", Color.Gold);
                SetStatus($"Client {ep} connected");

                await ReceiveLoopAsync(stream, token);
                EmitRxLine();
                if (!token.IsCancellationRequested)
                {
                    Log("Client disconnected, waiting for next ...", Color.Gold);
                    SetStatus($"Listening on {ip}:{port}");
                }
            }
            finally
            {
                try { stream?.Close(); } catch { }
                try { client?.Close(); } catch { }
                stream = null; client = null;
            }
        }
    }

    private void Stop(bool log = true)
    {
        try { cts?.Cancel(); } catch { }
        try { stream?.Close(); } catch { }
        try { client?.Close(); } catch { }
        try { listener?.Stop(); } catch { }
        stream = null; client = null; listener = null;
        SetButtons(false);
        SetStatus("Disconnected");
        if (log) Log("Disconnected", Color.Gold);
    }

    // ================= receive =================

    private async Task ReceiveLoopAsync(NetworkStream s, CancellationToken token)
    {
        var buf = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested)
            {
                int n = await s.ReadAsync(buf, token);
                if (n == 0) break;
                ProcessRx(buf, n);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // TCP is a byte stream (no "messages"), so we split on '\n' ourselves
    // and show one RX line per complete line.
    private void ProcessRx(byte[] data, int count)
    {
        int start = 0;
        for (int i = 0; i < count; i++)
        {
            if (data[i] == 0x0A)
            {
                rxBuf.AddRange(new ArraySegment<byte>(data, start, i - start + 1));
                EmitRxLine();
                start = i + 1;
            }
        }
        if (start < count) rxBuf.AddRange(new ArraySegment<byte>(data, start, count - start));

        lastRxTick = Environment.TickCount64;
        if (rxBuf.Count >= MaxLineBytes) EmitRxLine();
    }

    private void EmitRxLine()
    {
        if (rxBuf.Count == 0) return;
        byte[] bytes = rxBuf.ToArray();
        rxBuf.Clear();

        string shown = chkHex.Checked
            ? ToHex(bytes)
            : Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
        Log("RX: " + shown, Color.SpringGreen);
    }

    // ================= send =================

    private async Task SendAsync()
    {
        var s = stream;
        if (s == null) { Log("Not connected", Color.Tomato); return; }

        string text = txtSend.Text;
        if (text.Length == 0) return;

        byte[] data;
        if (chkHex.Checked)
        {
            if (!TryParseHex(text, out var hexBytes))
            {
                Log("Invalid hex. Example: 48 65 6C 6C 6F", Color.Tomato);
                return;
            }
            data = chkCrlf.Checked ? hexBytes.Concat(new byte[] { 0x0D, 0x0A }).ToArray() : hexBytes;
        }
        else
        {
            data = Encoding.UTF8.GetBytes(text + (chkCrlf.Checked ? "\r\n" : ""));
        }

        if (!await SendBytesAsync(data)) return;
        Log("TX: " + (chkHex.Checked ? ToHex(data) : text), Color.DeepSkyBlue);
        txtSend.Clear();
    }

    // Single place that writes to the socket (one write at a time, so typing,
    // Send File and Watch Folder never mix their bytes together).
    private async Task<bool> SendBytesAsync(byte[] data)
    {
        var s = stream;
        if (s == null) return false;
        await sendLock.WaitAsync();
        try
        {
            await s.WriteAsync(data);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Send failed: {ex.Message}", Color.Tomato);
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }

    // ================= send file =================

    private async Task SendFileAsync()
    {
        if (stream == null) { Log("Not connected", Color.Tomato); return; }

        using var dlg = new OpenFileDialog
        {
            Filter = "Text files (*.txt;*.log;*.csv)|*.txt;*.log;*.csv|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        btnSendFile.Enabled = false;
        try
        {
            Log($"Sending file: {Path.GetFileName(dlg.FileName)}", Color.Gold);
            int sent = 0;
            using var fs = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (stream == null) { Log("Connection lost, file send stopped", Color.Tomato); return; }
                if (!await SendBytesAsync(Encoding.UTF8.GetBytes(line + "\r\n"))) return;
                Log("TX: " + line, Color.DeepSkyBlue);
                sent++;
                int delay = (int)numLineDelay.Value;
                if (delay > 0) await Task.Delay(delay);
            }
            Log($"File sent: {Path.GetFileName(dlg.FileName)} ({sent} lines)", Color.Gold);
        }
        catch (Exception ex)
        {
            Log($"File send failed: {ex.Message}", Color.Tomato);
        }
        finally
        {
            btnSendFile.Enabled = true;
        }
    }

    // ================= watch folder =================
    // Polls the folder every second (works on network drives too, where
    // FileSystemWatcher is unreliable). New files are sent from the start,
    // lines appended to existing files are sent as they appear.

    private string WatchFilter()
    {
        string f = txtWatchFilter.Text.Trim();
        return f.Length == 0 ? "*.txt" : f;
    }

    private void ToggleWatch()
    {
        if (watchFolder != null) { StopWatch(); return; }

        using var dlg = new FolderBrowserDialog { Description = "Select the folder to watch" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        watchFolder = dlg.SelectedPath;
        watchOffsets.Clear();
        watchPrevLen.Clear();
        notConnectedLogged = false;

        // files already in the folder: start from their current end, so old content is not re-sent
        try
        {
            foreach (var f in Directory.EnumerateFiles(watchFolder, WatchFilter()))
            {
                long len = new FileInfo(f).Length;
                watchOffsets[f] = len;
                watchPrevLen[f] = len;
            }
        }
        catch (Exception ex)
        {
            Log($"Cannot read folder: {ex.Message}", Color.Tomato);
            watchFolder = null;
            return;
        }

        btnWatch.Text = "Stop Watching";
        txtWatchFilter.Enabled = false;
        lblWatch.Text = watchFolder;
        Log($"Watching {watchFolder} ({WatchFilter()}) - new files / new lines will be sent", Color.Gold);
        watchTimer.Start();
    }

    private void StopWatch(bool log = true)
    {
        watchTimer.Stop();
        bool wasWatching = watchFolder != null;
        watchFolder = null;
        btnWatch.Text = "Watch Folder...";
        txtWatchFilter.Enabled = true;
        lblWatch.Text = "";
        if (log && wasWatching) Log("Stopped watching folder", Color.Gold);
    }

    private async Task PollWatchAsync()
    {
        if (watchFolder == null || watchBusy) return;
        watchBusy = true;
        try
        {
            if (stream == null)
            {
                if (!notConnectedLogged)
                {
                    Log("Watching: not connected yet, waiting ...", Color.Gold);
                    notConnectedLogged = true;
                }
                return;
            }
            notConnectedLogged = false;

            foreach (var f in Directory.EnumerateFiles(watchFolder, WatchFilter()).ToList())
            {
                if (watchFolder == null || stream == null) break;
                await ProcessWatchedFileAsync(f);
            }
        }
        catch (Exception ex)
        {
            Log($"Watch error: {ex.Message}", Color.Tomato);
        }
        finally
        {
            watchBusy = false;
        }
    }

    private async Task ProcessWatchedFileAsync(string path)
    {
        long len;
        try { len = new FileInfo(path).Length; }
        catch { return; }

        watchOffsets.TryGetValue(path, out long offset);   // not seen before = new file = start at 0
        if (len < offset) offset = 0;                      // file was replaced / emptied

        bool stable = watchPrevLen.TryGetValue(path, out long prev) && prev == len;
        watchPrevLen[path] = len;

        if (len == offset) { watchOffsets[path] = offset; return; }

        byte[] chunk;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(offset, SeekOrigin.Begin);
            chunk = new byte[len - offset];
            int read = 0;
            while (read < chunk.Length)
            {
                int n = await fs.ReadAsync(chunk, read, chunk.Length - read);
                if (n == 0) break;
                read += n;
            }
            if (read < chunk.Length) Array.Resize(ref chunk, read);
        }
        catch (IOException) { return; }   // file busy (still being copied) - try again next second

        // only send complete lines; a last line without newline is sent once the file stops growing
        int limit = chunk.Length;
        if (!stable)
        {
            int lastNl = Array.LastIndexOf(chunk, (byte)0x0A);
            limit = lastNl + 1;
        }
        if (limit <= 0) return;

        int pos = 0;
        while (pos < limit)
        {
            int nl = Array.IndexOf(chunk, (byte)0x0A, pos, limit - pos);
            int end = nl >= 0 ? nl + 1 : limit;

            int lineStart = pos;
            if (offset == 0 && pos == 0 && end >= 3 && chunk[0] == 0xEF && chunk[1] == 0xBB && chunk[2] == 0xBF)
                lineStart = 3;   // skip UTF-8 BOM at the start of a file

            int contentEnd = end;
            while (contentEnd > lineStart && (chunk[contentEnd - 1] == 0x0A || chunk[contentEnd - 1] == 0x0D)) contentEnd--;

            var payload = new byte[contentEnd - lineStart + 2];
            Buffer.BlockCopy(chunk, lineStart, payload, 0, contentEnd - lineStart);
            payload[^2] = 0x0D;
            payload[^1] = 0x0A;

            if (!await SendBytesAsync(payload)) return;   // offset not advanced, will retry

            string shown = chkHex.Checked ? ToHex(payload) : Encoding.UTF8.GetString(payload, 0, payload.Length - 2);
            Log($"TX [{Path.GetFileName(path)}]: {shown}", Color.DeepSkyBlue);

            offset += end - pos;
            watchOffsets[path] = offset;
            pos = end;

            int delay = (int)numLineDelay.Value;
            if (delay > 0) await Task.Delay(delay);
        }
    }

    // ================= hex helpers =================

    private static string ToHex(byte[] b) => BitConverter.ToString(b).Replace('-', ' ');

    private static bool TryParseHex(string s, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        string clean = s.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(" ", "").Replace("-", "").Replace(",", "");
        if (clean.Length == 0 || clean.Length % 2 != 0) return false;
        try { bytes = Convert.FromHexString(clean); return true; }
        catch (FormatException) { return false; }
    }
}
