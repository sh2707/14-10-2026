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
        Controls.Add(options);
        Controls.Add(top);
        Controls.Add(lblStatus);

        btnConnect.Click += async (_, _) => await StartAsync();
        btnStop.Click += (_, _) => Stop();
        btnClear.Click += (_, _) => { console.Clear(); lineCount = 0; };
        btnSend.Click += async (_, _) => await SendAsync();
        btnSaveLog.Click += (_, _) => SaveLog();
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

        try
        {
            await s.WriteAsync(data);
            Log("TX: " + (chkHex.Checked ? ToHex(data) : text), Color.DeepSkyBlue);
            txtSend.Clear();
        }
        catch (Exception ex)
        {
            Log($"Send failed: {ex.Message}", Color.Tomato);
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
