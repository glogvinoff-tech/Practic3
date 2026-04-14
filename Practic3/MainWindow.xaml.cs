using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Practic3;

public partial class MainWindow : Window
{
    // ─── Сервер ──────────────────────────────────────────────────────────────
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly DateTime _appStart = DateTime.Now;

    // ─── Клиент ──────────────────────────────────────────────────────────────
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ─── Статистика ──────────────────────────────────────────────────────────
    private int _getCount;
    private int _postCount;
    private long _totalMs;
    private int _totalRequests;

    private readonly Dictionary<string, string> _messages = new();
    private readonly List<LogEntry> _allLogs = new();
    private readonly Dictionary<DateTime, int> _perMinute = new();

    // ─────────────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        TxtBody.Text = "{\n  \"message\": \"Hello World\"\n}";
    }

    // ════════════════════════════════════════════════════════════════════════
    // СЕРВЕР
    // ════════════════════════════════════════════════════════════════════════

    private void BtnServer_Click(object sender, RoutedEventArgs e)
    {
        if (_listener?.IsListening == true)
        {
            StopServer();
            BtnServer.Content = "Запустить";
            TxtPort.IsEnabled = true;
        }
        else
        {
            if (!int.TryParse(TxtPort.Text, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Введите корректный порт (1–65535).", "Ошибка");
                return;
            }
            if (StartServer(port))
            {
                BtnServer.Content = "Остановить";
                TxtPort.IsEnabled = false;
            }
        }
    }

    private bool StartServer(int port)
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            AppendLog($"[SERVER] Запущен → http://localhost:{port}/", null);
            _ = AcceptLoopAsync(_cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось запустить сервер:\n{ex.Message}", "Ошибка");
            return false;
        }
    }

    private void StopServer()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        AppendLog("[SERVER] Остановлен.", null);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener!.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx, ct), ct);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { AppendLog($"[SERVER ERROR] {ex.Message}", null); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var req = ctx.Request;
        var res = ctx.Response;

        // Читаем тело
        string body = "";
        if (req.HasEntityBody)
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                body = await reader.ReadToEndAsync(ct);

        string responseJson;
        int status = 200;

        if (req.HttpMethod == "GET")
        {
            Interlocked.Increment(ref _getCount);
            var uptime = DateTime.Now - _appStart;
            responseJson = JsonSerializer.Serialize(new
            {
                status = "running",
                getRequests = _getCount,
                postRequests = _postCount,
                uptime = uptime.ToString(@"hh\:mm\:ss")
            });
        }
        else if (req.HttpMethod == "POST")
        {
            Interlocked.Increment(ref _postCount);
            try
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                var msg = data?["message"].GetString() ?? "";
                var id = Guid.NewGuid().ToString("N")[..8];
                lock (_messages) _messages[id] = msg;
                responseJson = JsonSerializer.Serialize(new { id, message = msg });
            }
            catch
            {
                status = 400;
                responseJson = "{\"error\":\"Invalid JSON\"}";
            }
        }
        else
        {
            status = 405;
            responseJson = "{\"error\":\"Method not allowed\"}";
        }

        sw.Stop();
        Interlocked.Add(ref _totalMs, sw.ElapsedMilliseconds);
        Interlocked.Increment(ref _totalRequests);

        // Учёт по минутам
        var minute = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day,
                                  DateTime.Now.Hour, DateTime.Now.Minute, 0);
        lock (_perMinute)
        {
            _perMinute.TryGetValue(minute, out int c);
            _perMinute[minute] = c + 1;
        }

        // Отправляем ответ
        var buf = Encoding.UTF8.GetBytes(responseJson);
        res.ContentType = "application/json";
        res.ContentLength64 = buf.Length;
        res.StatusCode = status;
        await res.OutputStream.WriteAsync(buf, ct);
        res.OutputStream.Close();

        // Сохраняем лог
        var entry = new LogEntry
        {
            Time = DateTime.Now,
            Method = req.HttpMethod,
            Url = req.Url?.AbsoluteUri ?? "",
            Body = body,
            StatusCode = status,
            ElapsedMs = sw.ElapsedMilliseconds
        };
        lock (_allLogs) _allLogs.Add(entry);

        Dispatcher.Invoke(() =>
        {
            var bodyLine = body.Length > 0 ? $"\n  Body: {body}" : "";
            AppendLog($"[{req.HttpMethod}] {req.Url?.AbsoluteUri} → {status} ({sw.ElapsedMilliseconds} мс){bodyLine}",
                      req.HttpMethod);
            RefreshStats();
            DrawChart();
        });
    }

    // ════════════════════════════════════════════════════════════════════════
    // КЛИЕНТ
    // ════════════════════════════════════════════════════════════════════════

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtUrl.Text.Trim();
        var method = ((CmbMethod.SelectedItem as ComboBoxItem)?.Content.ToString()) ?? "GET";

        if (string.IsNullOrEmpty(url)) { MessageBox.Show("Введите URL."); return; }

        TxtResponse.Text = "Отправка…";
        try
        {
            var sw = Stopwatch.StartNew();
            HttpResponseMessage resp;

            if (method == "GET")
                resp = await _http.GetAsync(url);
            else
            {
                var content = new StringContent(TxtBody.Text, Encoding.UTF8, "application/json");
                resp = await _http.PostAsync(url, content);
            }

            sw.Stop();
            var raw = await resp.Content.ReadAsStringAsync();

            // Красиво форматируем JSON
            try
            {
                raw = JsonSerializer.Serialize(
                    JsonDocument.Parse(raw).RootElement,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            catch { }

            TxtResponse.Text =
                $"HTTP {(int)resp.StatusCode} {resp.StatusCode}  ({sw.ElapsedMilliseconds} мс)\n\n{raw}";

            AppendLog($"[CLIENT {method}] {url} → {(int)resp.StatusCode} ({sw.ElapsedMilliseconds} мс)", null);
        }
        catch (Exception ex)
        {
            TxtResponse.Text = $"Ошибка: {ex.Message}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ЛОГИ И ФИЛЬТРАЦИЯ
    // ════════════════════════════════════════════════════════════════════════

    private void AppendLog(string message, string? method)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AppendLog(message, method)); return; }

        var filter = ((CmbFilter.SelectedItem as ComboBoxItem)?.Content.ToString()) ?? "Все";
        if (method == null || filter == "Все" || filter == method)
        {
            TxtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            TxtLogs.ScrollToEnd();
        }
    }

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtLogs == null) return;
        var filter = ((CmbFilter.SelectedItem as ComboBoxItem)?.Content.ToString()) ?? "Все";
        TxtLogs.Clear();
        lock (_allLogs)
        {
            foreach (var l in _allLogs)
            {
                if (filter == "Все" || filter == l.Method)
                    TxtLogs.AppendText(
                        $"[{l.Time:HH:mm:ss}] [{l.Method}] {l.Url} → {l.StatusCode} ({l.ElapsedMs} мс)\n");
            }
        }
        TxtLogs.ScrollToEnd();
    }

    private void BtnSaveLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText("logs.txt", TxtLogs.Text);
            MessageBox.Show("Логи сохранены в logs.txt", "Готово");
        }
        catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}"); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // СТАТИСТИКА И ГРАФИК
    // ════════════════════════════════════════════════════════════════════════

    private void RefreshStats()
    {
        double avg = _totalRequests > 0 ? (double)_totalMs / _totalRequests : 0;
        TxtStats.Text =
            $"GET: {_getCount}  |  POST: {_postCount}  |  Всего: {_totalRequests}  |  Среднее время: {avg:0.0} мс";
    }

    private void DrawChart()
    {
        ChartCanvas.Children.Clear();

        List<KeyValuePair<DateTime, int>> data;
        lock (_perMinute)
            data = _perMinute.OrderBy(x => x.Key).TakeLast(20).ToList();

        if (data.Count == 0) return;

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        int max = Math.Max(1, data.Max(x => x.Value));
        double bw = w / data.Count;

        for (int i = 0; i < data.Count; i++)
        {
            double bh = (double)data[i].Value / max * (h - 18);

            var rect = new Rectangle
            {
                Width = Math.Max(bw - 2, 1),
                Height = Math.Max(bh, 1),
                Fill = Brushes.SteelBlue,
                ToolTip = $"{data[i].Key:HH:mm} — {data[i].Value} запр."
            };
            Canvas.SetLeft(rect, i * bw + 1);
            Canvas.SetTop(rect, h - bh);
            ChartCanvas.Children.Add(rect);

            var lbl = new TextBlock
            {
                Text = data[i].Value.ToString(),
                FontSize = 9,
                Foreground = Brushes.Black
            };
            Canvas.SetLeft(lbl, i * bw + 1);
            Canvas.SetTop(lbl, h - bh - 13);
            ChartCanvas.Children.Add(lbl);
        }
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    // ════════════════════════════════════════════════════════════════════════

    protected override void OnClosed(EventArgs e)
    {
        StopServer();
        _http.Dispose();
        base.OnClosed(e);
    }
}

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string Body { get; set; } = "";
    public int StatusCode { get; set; }
    public long ElapsedMs { get; set; }
}
