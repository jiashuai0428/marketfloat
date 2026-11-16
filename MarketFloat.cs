using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MarketFloat
{
    sealed class Instrument
    {
        public readonly string Code, Name, Category;
        public readonly bool Metal, Default;
        // Price decimals from the contract's tickSize; -1 when unknown (bundled catalog).
        public int Decimals = -1;
        public string Symbol { get { return Code + "USDT"; } }
        public Instrument(string code, string name, bool metal = false, bool selected = false, string category = null)
        {
            Code = code; Name = name; Metal = metal; Default = selected;
            Category = category ?? (metal ? "贵金属" : "加密货币");
        }
    }

    sealed class Quote
    {
        public readonly decimal Price, Open;
        public readonly DateTimeOffset EventTime;
        public Quote(decimal price, long timestamp, decimal open = 0)
        { Price = price; Open = open; EventTime = DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
        // 24h change as a fraction (0.0231 = +2.31%), or null while the 24h open is unknown.
        public decimal? Change { get { return Open > 0 ? (Price - Open) / Open : (decimal?)null; } }
    }

    enum FeedSource { Price, Day, Stream }

    // WebSocket push (!miniTicker@arr) is the primary source; REST polling takes over when the
    // stream is down, and the 24h ticker is fetched once a minute so quiet symbols still have an open.
    sealed class MarketFeed
    {
        const string StreamUrl = "wss://fstream.binance.com/market/ws/!miniTicker@arr";
        const string PriceUrl = "https://fapi.binance.com/fapi/v2/ticker/price";
        const string DayUrl = "https://fapi.binance.com/fapi/v1/ticker/24hr";
        readonly HashSet<string> symbols;
        readonly Dictionary<string, Quote> quotes = new Dictionary<string, Quote>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        volatile bool restConnected, streamConnected, blocked;
        volatile string restStatus = "等待币安接口", streamStatus = "正在连接";
        long lastStreamTicks;
        public bool Connected { get { return StreamFresh || restConnected; } }
        bool StreamFresh
        {
            get { return streamConnected && DateTime.UtcNow.Ticks - Volatile.Read(ref lastStreamTicks) < TimeSpan.TicksPerSecond * 5; }
        }
        public string Status { get { return StreamFresh ? "币安推送已连接" :
            restConnected ? "币安 REST 已连接" : blocked ? restStatus : streamStatus; } }
        public string Details { get { return "WebSocket 推送（主）: " + streamStatus +
            "\r\nREST 轮询（备用）: " + restStatus + "\r\n逐笔成交（价格提醒）: " + tradeStatus; } }

        // ---- Per-trade stream for symbols that have price alerts, so brief spikes are not missed. ----
        const string TradeUrl = "wss://fstream.binance.com/market/stream?streams=";
        readonly object tradeLock = new object();
        HashSet<string> tradeSymbols = new HashSet<string>();
        Dictionary<string, decimal[]> extremes = new Dictionary<string, decimal[]>(); // symbol -> {min, max}
        CancellationTokenSource tradeCycle;
        volatile string tradeStatus = "未启用（没有价格提醒）";

        public void WatchTrades(IEnumerable<string> watch)
        {
            var next = new HashSet<string>(watch.Where(symbols.Contains));
            lock (tradeLock)
            {
                if (next.SetEquals(tradeSymbols)) return;
                tradeSymbols = next;
                if (tradeCycle != null) tradeCycle.Cancel();
            }
        }

        // Lowest and highest traded price per symbol since the previous call.
        public Dictionary<string, decimal[]> TakeExtremes()
        {
            lock (tradeLock)
            {
                var result = extremes;
                extremes = new Dictionary<string, decimal[]>();
                return result;
            }
        }

        void RecordTrade(string symbol, decimal price)
        {
            lock (tradeLock)
            {
                decimal[] range;
                if (!extremes.TryGetValue(symbol, out range)) extremes[symbol] = new[] { price, price };
                else { range[0] = Math.Min(range[0], price); range[1] = Math.Max(range[1], price); }
            }
        }

        public bool AcceptTrades(string message)
        {
            try
            {
                Dictionary<string, object> root;
                lock (json) root = json.DeserializeObject(message) as Dictionary<string, object>;
                object data, code, priceText, kind;
                if (root == null || !root.TryGetValue("data", out data)) return false;
                var trade = data as Dictionary<string, object>;
                decimal price;
                if (trade == null || !trade.TryGetValue("e", out kind) || !Equals(kind, "aggTrade") ||
                    !trade.TryGetValue("s", out code) || !(code is string) || !symbols.Contains((string)code) ||
                    !trade.TryGetValue("p", out priceText) || !TryPrice(priceText, out price)) return false;
                RecordTrade((string)code, price);
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        async Task RunTrades(CancellationToken stop)
        {
            int failures = 0;
            while (!stop.IsCancellationRequested)
            {
                string[] list;
                CancellationTokenSource cycle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                lock (tradeLock) { list = tradeSymbols.ToArray(); tradeCycle = cycle; }
                bool ok = true;
                try
                {
                    if (list.Length == 0)
                    {
                        tradeStatus = "未启用（没有价格提醒）";
                        await Task.Delay(Timeout.Infinite, cycle.Token).ConfigureAwait(false);
                    }
                    else ok = await TradesOnce(list, cycle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                finally
                {
                    lock (tradeLock) { if (tradeCycle == cycle) tradeCycle = null; }
                    cycle.Dispose();
                }
                if (stop.IsCancellationRequested) break;
                failures = ok ? 0 : Math.Min(failures + 1, 5);
                if (ok) continue;
                try { await Task.Delay(1000 * (1 << failures), stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        async Task<bool> TradesOnce(string[] list, CancellationToken token)
        {
            bool received = false;
            tradeStatus = "正在连接（" + list.Length + " 个品种）";
            try
            {
                using (var socket = new ClientWebSocket())
                {
                    string url = TradeUrl + string.Join("/", list.Select(s => s.ToLowerInvariant() + "@aggTrade"));
                    using (var dial = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        dial.CancelAfter(TimeSpan.FromSeconds(8));
                        await socket.ConnectAsync(new Uri(url), dial.Token).ConfigureAwait(false);
                    }
                    tradeStatus = "已连接（" + list.Length + " 个品种）";
                    var buffer = new byte[16384];
                    using (var message = new MemoryStream())
                    {
                        while (!token.IsCancellationRequested)
                        {
                            message.SetLength(0);
                            WebSocketReceiveResult part;
                            using (var read = CancellationTokenSource.CreateLinkedTokenSource(token))
                            {
                                read.CancelAfter(TimeSpan.FromSeconds(90)); // quiet contracts can go a while without trades
                                do
                                {
                                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), read.Token).ConfigureAwait(false);
                                    if (part.MessageType == WebSocketMessageType.Close) { tradeStatus = "连接已关闭，正在重连"; return received; }
                                    message.Write(buffer, 0, part.Count);
                                    if (message.Length > 1024 * 1024) return received;
                                } while (!part.EndOfMessage);
                            }
                            if (part.MessageType == WebSocketMessageType.Text &&
                                AcceptTrades(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)))
                            {
                                received = true;
                                tradeStatus = "实时成交中（" + list.Length + " 个品种）";
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) { tradeStatus = "接收超时，正在重连"; return received; } throw; }
            catch (Exception ex)
            {
                if (!(ex is WebSocketException || ex is SocketException ||
                    ex is HttpRequestException || ex is IOException || ex is WebException ||
                    ex is InvalidOperationException || ex is PlatformNotSupportedException)) throw;
                tradeStatus = NetworkError(ex);
            }
            return received;
        }

        public MarketFeed(IEnumerable<Instrument> instruments)
        {
            symbols = new HashSet<string>(instruments.Select(x => x.Symbol));
        }

        public Quote Get(string symbol)
        {
            lock (quotes)
            {
                Quote result;
                quotes.TryGetValue(symbol, out result);
                return result;
            }
        }

        public async Task Run(CancellationToken stop)
        {
            if (symbols.Count == 0) return;
            Task stream = RunStream(stop);
            Task trades = RunTrades(stop);
            int failures = 0;
            DateTime nextDay = DateTime.MinValue;
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    http.MaxResponseContentBufferSize = 4 * 1024 * 1024;
                    while (!stop.IsCancellationRequested)
                    {
                        bool day = DateTime.UtcNow >= nextDay;
                        if (!day && StreamFresh)
                        {
                            restConnected = false;
                            restStatus = "待命（推送正常）";
                            failures = 0;
                            await Task.Delay(1000, stop).ConfigureAwait(false);
                            continue;
                        }
                        var elapsed = Stopwatch.StartNew();
                        int retryAfter = 0;
                        bool ok = false;
                        try
                        {
                            using (var response = await http.GetAsync(day ? DayUrl : PriceUrl, stop).ConfigureAwait(false))
                            {
                                int status = (int)response.StatusCode;
                                blocked = status == 451 || status == 403;
                                if (status == 429 || status == 418)
                                {
                                    retryAfter = status == 418 ? 120000 : 60000;
                                    var retry = response.Headers.RetryAfter;
                                    if (retry != null)
                                    {
                                        TimeSpan? wait = retry.Delta ?? (retry.Date - DateTimeOffset.UtcNow);
                                        if (wait.HasValue)
                                            retryAfter = (int)Math.Max(retryAfter, Math.Min(int.MaxValue, wait.Value.TotalMilliseconds));
                                    }
                                }
                                ok = response.IsSuccessStatusCode &&
                                    Accept(await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                                        day ? FeedSource.Day : FeedSource.Price);
                                restStatus = ok ? "已收到报价" : response.IsSuccessStatusCode
                                    ? "接口未返回所选合约" : HttpStatus(status);
                            }
                        }
                        catch (HttpRequestException ex) { restStatus = NetworkError(ex); }
                        catch (OperationCanceledException) { if (stop.IsCancellationRequested) break; restStatus = "连接超时"; }

                        if (stop.IsCancellationRequested) break;
                        if (ok && day) nextDay = DateTime.UtcNow.AddSeconds(60);
                        restConnected = ok && !StreamFresh;
                        failures = ok ? 0 : Math.Min(failures + 1, 5);
                        int delay = 1000 * (1 << failures);
                        if (ok) delay = Math.Max(1, delay - (int)elapsed.ElapsedMilliseconds);
                        await Task.Delay(Math.Max(delay, retryAfter), stop).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { restConnected = false; }
            await stream.ConfigureAwait(false);
            await trades.ConfigureAwait(false);
        }

        async Task RunStream(CancellationToken stop)
        {
            int failures = 0;
            while (!stop.IsCancellationRequested)
            {
                bool received = await StreamOnce(stop).ConfigureAwait(false);
                failures = received ? 0 : Math.Min(failures + 1, 5);
                try { await Task.Delay(1000 * (1 << failures), stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        async Task<bool> StreamOnce(CancellationToken stop)
        {
            bool received = false;
            streamStatus = "正在连接";
            try
            {
                using (var socket = new ClientWebSocket())
                {
                    using (var dial = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        dial.CancelAfter(TimeSpan.FromSeconds(8));
                        await socket.ConnectAsync(new Uri(StreamUrl), dial.Token).ConfigureAwait(false);
                    }
                    var buffer = new byte[16384];
                    using (var message = new MemoryStream())
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            message.SetLength(0);
                            WebSocketReceiveResult part;
                            using (var read = CancellationTokenSource.CreateLinkedTokenSource(stop))
                            {
                                read.CancelAfter(TimeSpan.FromSeconds(15));
                                do
                                {
                                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), read.Token).ConfigureAwait(false);
                                    if (part.MessageType == WebSocketMessageType.Close) { streamStatus = "连接已关闭，正在重连"; return received; }
                                    message.Write(buffer, 0, part.Count);
                                    if (message.Length > 4 * 1024 * 1024) { streamStatus = "行情消息过大"; return received; }
                                } while (!part.EndOfMessage);
                            }
                            if (part.MessageType != WebSocketMessageType.Text) continue;
                            if (Accept(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), FeedSource.Stream))
                            {
                                received = true;
                                streamConnected = true;
                                streamStatus = "实时推送中";
                            }
                            else if (received)
                                Volatile.Write(ref lastStreamTicks, DateTime.UtcNow.Ticks); // alive, just no selected symbol moved
                        }
                    }
                }
            }
            catch (OperationCanceledException) { if (!stop.IsCancellationRequested) streamStatus = "连接或接收超时，正在重连"; }
            catch (Exception ex)
            {
                if (!(ex is WebSocketException || ex is SocketException ||
                    ex is HttpRequestException || ex is IOException || ex is WebException ||
                    ex is InvalidOperationException || ex is PlatformNotSupportedException)) throw;
                streamStatus = NetworkError(ex);
            }
            finally { streamConnected = false; }
            return received;
        }

        static string HttpStatus(int status)
        {
            if (status == 451) return "币安限制当前网络地区 (HTTP 451)";
            if (status == 403) return "币安拒绝访问 (HTTP 403)";
            if (status == 429 || status == 418) return "币安接口限流 (HTTP " + status + ")";
            return "币安接口错误 (HTTP " + status + ")";
        }

        static string NetworkError(Exception error)
        {
            for (var cause = error; cause != null; cause = cause.InnerException)
            {
                var web = cause as WebException;
                var socket = cause as SocketException;
                if ((web != null && (web.Status == WebExceptionStatus.NameResolutionFailure ||
                    web.Status == WebExceptionStatus.ProxyNameResolutionFailure)) ||
                    (socket != null && (socket.SocketErrorCode == SocketError.HostNotFound ||
                    socket.SocketErrorCode == SocketError.NoData || socket.SocketErrorCode == SocketError.TryAgain)))
                    return "域名解析失败 (DNS)";
                if (web != null && web.Status == WebExceptionStatus.TrustFailure)
                    return "服务器证书校验失败 · " + web.GetBaseException().Message;
                if (web != null && web.Status == WebExceptionStatus.SecureChannelFailure)
                    return "TLS 握手失败 · " + web.GetBaseException().Message;
            }
            return "连接失败 · " + error.GetBaseException().Message;
        }

        public bool Accept(string message, bool stream = false)
        {
            return Accept(message, stream ? FeedSource.Stream : FeedSource.Price);
        }

        static bool TryPrice(object value, out decimal result)
        {
            return decimal.TryParse(value as string, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result) && result > 0;
        }

        public bool Accept(string message, FeedSource source)
        {
            try
            {
                object[] rows;
                lock (json) rows = json.DeserializeObject(message) as object[];
                if (rows == null) return false;
                bool stream = source == FeedSource.Stream, day = source == FeedSource.Day;
                string priceKey = stream ? "c" : day ? "lastPrice" : "price";
                string timeKey = stream ? "E" : day ? "closeTime" : "time";
                string openKey = stream ? "o" : day ? "openPrice" : null;
                bool received = false;
                lock (quotes)
                {
                    foreach (var row in rows.OfType<Dictionary<string, object>>())
                    {
                        object code, priceText, timeText, kind, market, openText;
                        if (stream && (!row.TryGetValue("e", out kind) || !Equals(kind, "24hrMiniTicker") ||
                            (row.TryGetValue("st", out market) && !Equals(market, 1)))) continue;
                        if (!row.TryGetValue(stream ? "s" : "symbol", out code) || !(code is string) ||
                            !symbols.Contains((string)code) || !row.TryGetValue(priceKey, out priceText) ||
                            !row.TryGetValue(timeKey, out timeText)) continue;

                        decimal price, open;
                        long timestamp;
                        if (!TryPrice(priceText, out price) ||
                            !long.TryParse(Convert.ToString(timeText, CultureInfo.InvariantCulture), out timestamp) ||
                            timestamp <= 0 || timestamp > 253402300799999L) continue;

                        Quote last;
                        quotes.TryGetValue((string)code, out last);
                        if (openKey == null || !row.TryGetValue(openKey, out openText) || !TryPrice(openText, out open))
                            open = last != null ? last.Open : 0;
                        var next = new Quote(price, timestamp, open);
                        if (last == null || next.EventTime >= last.EventTime)
                            quotes[(string)code] = next;
                        else if (open > 0 && last.Open != open)
                            quotes[(string)code] = new Quote(last.Price, last.EventTime.ToUnixTimeMilliseconds(), open);
                        received = true;
                    }
                }
                if (received && stream) Volatile.Write(ref lastStreamTicks, DateTime.UtcNow.Ticks);
                return received;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }

    sealed class PriceAlert
    {
        public readonly string Code;
        public readonly bool Above;   // true: fire when price >= Target; false: when price <= Target
        public readonly decimal Target;
        public PriceAlert(string code, bool above, decimal target) { Code = code; Above = above; Target = target; }
        public string Symbol { get { return Code + "USDT"; } }
        public bool Hit(decimal price) { return Above ? price >= Target : price <= Target; }
        public string Describe(int decimals) { return (Above ? "涨到 ≥ " : "跌到 ≤ ") + Widget.PriceText(Target, decimals); }
        public string Serialize() { return Code + (Above ? ">" : "<") + Target.ToString(CultureInfo.InvariantCulture); }

        public static PriceAlert Parse(string text)
        {
            int at = text.IndexOfAny(new[] { '>', '<' });
            if (at <= 0) return null;
            string code = text.Substring(0, at);
            decimal target;
            if (!code.All(c => char.IsLetterOrDigit(c) || c == '_') || code.Length > 40 ||
                !decimal.TryParse(text.Substring(at + 1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out target) ||
                target <= 0) return null;
            return new PriceAlert(code, text[at] == '>', target);
        }

        // Accepts "70000", "70,000.5" or " 0.1234 ".
        public static bool TryReadPrice(string text, out decimal price)
        {
            return decimal.TryParse((text ?? "").Replace(",", "").Replace("，", "").Trim(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out price) && price > 0;
        }
    }

    enum Hit { None, Body, Menu, Compact, Close, Grip }

    sealed class Widget : Form
    {
        internal static readonly Instrument[] Universe = {
            new Instrument("BTC", "比特币", selected:true),
            new Instrument("ETH", "以太坊", selected:true),
            new Instrument("SOL", "Solana", selected:true),
            new Instrument("BNB", "BNB"),
            new Instrument("XRP", "XRP"),
            new Instrument("DOGE", "Dogecoin"),
            new Instrument("ADA", "Cardano"),
            new Instrument("AVAX", "Avalanche"),
            new Instrument("SUI", "Sui"),
            new Instrument("LINK", "Chainlink"),
            new Instrument("XAU", "黄金", true, true),
            new Instrument("XAG", "白银", true, true),
            new Instrument("XPT", "铂金", true),
            new Instrument("XPD", "钯金", true),
            new Instrument("TSLA", "特斯拉", category:"美股／ETF"),
            new Instrument("NVDA", "英伟达", category:"美股／ETF"),
            new Instrument("MSTR", "Strategy", category:"美股／ETF"),
            new Instrument("AAPL", "苹果", category:"美股／ETF"),
            new Instrument("SNDK", "闪迪 Sandisk", category:"美股／ETF"),
            new Instrument("MU", "美光 Micron", category:"美股／ETF"),
            new Instrument("SPY", "标普 500 ETF", category:"美股／ETF"),
            new Instrument("QQQ", "纳斯达克 100 ETF", category:"美股／ETF"),
            new Instrument("AMZN", "亚马逊", category:"美股／ETF"),
            new Instrument("GOOGL", "Alphabet", category:"美股／ETF")
        };

        internal static readonly Color Main = Color.FromArgb(236, 242, 250);
        internal static readonly Color Muted = Color.FromArgb(146, 160, 180);
        internal static readonly Color Green = Color.FromArgb(52, 216, 152);
        static readonly Color Gold = Color.FromArgb(246, 196, 96);
        static readonly Color Red = Color.FromArgb(255, 104, 124);
        static readonly Color GlassTop = Color.FromArgb(28, 34, 45);
        static readonly Color GlassBottom = Color.FromArgb(12, 15, 21);
        // Background opacity in percent; 55 is the default frosted look.
        internal static readonly int[] OpacityLevels = { 100, 85, 70, 55, 40, 25, 0 };
        const int DefaultOpacity = 55;
        static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketFloat", "widget.ini");
        readonly HashSet<string> selected = new HashSet<string>(Universe.Where(x => x.Default).Select(x => x.Code));
        readonly string catalogPath;
        List<Instrument> catalog = Universe.ToList();
        string catalogState = "内置目录 · 2026-11-16";
        Task catalogLoad;
        readonly Dictionary<string, Quote> displayed = new Dictionary<string, Quote>();
        readonly Dictionary<string, Color> direction = new Dictionary<string, Color>();
        readonly Dictionary<string, DateTime> changedAt = new Dictionary<string, DateTime>();
        readonly Dictionary<string, int> scales = new Dictionary<string, int>();
        readonly List<PriceAlert> alerts = new List<PriceAlert>();
        readonly Dictionary<string, DateTime> alertedAt = new Dictionary<string, DateTime>();
        readonly List<string> firedLog = new List<string>();
        NotifyIcon tray;
        Icon trayIcon;
        AlertToast toast;
        System.Media.SoundPlayer alarm;
        int menuRow = -1;
        readonly List<ToolStripItem> rowItems = new List<ToolStripItem>();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 100 };
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolTip tips = new ToolTip { InitialDelay = 500, ReshowDelay = 100 };
        readonly Font ui = MakeFont(9, FontStyle.Regular, "Microsoft YaHei UI");
        readonly Font title = MakeFont(10, FontStyle.Bold, "Microsoft YaHei UI");
        readonly Font caption = MakeFont(8, FontStyle.Regular, "Microsoft YaHei UI");
        readonly Font code = MakeFont(10.5f, FontStyle.Regular, "Segoe UI Semibold", "Microsoft YaHei UI");
        readonly Font miniCode = MakeFont(9, FontStyle.Regular, "Segoe UI Semibold", "Microsoft YaHei UI");
        readonly Font digits = MakeFont(15, FontStyle.Regular, "Segoe UI Semibold", "Consolas");
        readonly Font miniDigits = MakeFont(12, FontStyle.Regular, "Segoe UI Semibold", "Consolas");
        readonly Font change24 = MakeFont(8.5f, FontStyle.Regular, "Segoe UI Semibold", "Microsoft YaHei UI");
        readonly int[] layoutWidths = { 312, 174 };
        readonly double[] layoutRows = { 42, 24 };
        int opacity = DefaultOpacity;
        bool resizing, inside, ready;
        Point resizeOrigin;
        Size resizeStart;
        Hit hover = Hit.None, pressed = Hit.None;
        int hoverRow = -1;
        string tipText;
        int firstRow;
        int visibleRows = 1;
        readonly ToolStripMenuItem pin;
        readonly bool testing;
        int unit; // DPI of the monitor the widget is on; changes on WM_DPICHANGED
        MarketFeed feed;
        CancellationTokenSource stop;
        DateTime feedStarted = DateTime.UtcNow, lastFrame = DateTime.MinValue;
        bool compact;
        DateTimeOffset frameTime = DateTimeOffset.UtcNow;

        public Widget(bool testing)
        {
            this.testing = testing;
            Text = "币安浮动行情";
            AccessibleName = "币安浮动行情";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            BackColor = GlassBottom;
            ForeColor = Main;
            TopMost = true;
            KeyPreview = true;

            try { unit = (int)GetDpiForWindow(Handle); }
            catch (EntryPointNotFoundException) { }
            if (unit <= 0)
                using (var graphics = CreateGraphics())
                    unit = (int)Math.Round(graphics.DpiX);
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Px(336), area.Top + Px(24));
            catalogPath = Path.Combine(Path.GetDirectoryName(SettingsPath), "contracts.json");
            using (var resource = typeof(Widget).Assembly.GetManifestResourceStream("BinanceContracts.json"))
            using (var reader = new StreamReader(resource))
                SetCatalog(ParseCatalog(reader.ReadToEnd()), catalogState);
            if (!testing)
            {
                LoadSettings();
                try
                {
                    SetCatalog(ParseCatalog(File.ReadAllText(catalogPath)), "缓存目录");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                foreach (string item in selected.Where(c => catalog.All(i => i.Code != c)).ToList())
                    catalog.Add(new Instrument(item, item, category:"已自选"));
            }

            Resize += delegate { if (ready && WindowState == FormWindowState.Normal) LayoutChrome(); };
            MouseWheel += delegate(object sender, MouseEventArgs e) { ScrollQuotes(e.Delta > 0 ? -1 : 1); };

            menu.Renderer = new DarkMenuRenderer();
            menu.Font = ui;
            menu.Items.Add("自选品种…", null, delegate
            {
                if (catalogLoad == null || catalogLoad.IsCompleted) catalogLoad = RefreshCatalog();
                using (var picker = new WatchDialog(catalog, selected, catalogState))
                {
                    UpdatePicker(picker, catalogLoad);
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    selected.Clear();
                    selected.UnionWith(picker.Selection);
                    firstRow = 0;
                    ApplySelection();
                }
            });
            var alertItem = new ToolStripMenuItem("价格提醒…", null, delegate { OpenAlerts(null); });
            menu.Items.Add(alertItem);
            menu.Items.Add(new ToolStripSeparator());
            pin = new ToolStripMenuItem("窗口置顶") { Checked = TopMost, CheckOnClick = true };
            pin.Click += delegate { TopMost = pin.Checked; SaveSettings(); };
            menu.Items.Add(pin);
            var fade = new ToolStripMenuItem("背景透明度");
            foreach (int level in OpacityLevels)
            {
                int value = level;
                var choice = new ToolStripMenuItem(OpacityText(value)) { Tag = value };
                choice.Click += delegate { SetOpacity(value); SaveSettings(); };
                fade.DropDownItems.Add(choice);
            }
            fade.DropDown.Renderer = menu.Renderer;
            fade.DropDown.Font = ui;
            fade.DropDownOpening += delegate
            {
                foreach (ToolStripMenuItem choice in fade.DropDownItems)
                    choice.Checked = (int)choice.Tag == opacity;
            };
            menu.Items.Add(fade);
            menu.Items.Add(new ToolStripMenuItem("普通／极简模式", null, delegate { ToggleCompact(); })
                { ShortcutKeyDisplayString = "F2" });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("行情连接诊断…", null, delegate
            {
                MessageBox.Show(this, feed.Details + "\r\n\r\n合约目录：" + catalogState +
                    " · " + catalog.Count + " 个品种", "行情连接诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            menu.Items.Add("最小化到任务栏", null, delegate { WindowState = FormWindowState.Minimized; });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Close(); });
            menu.Opening += delegate
            {
                pin.Checked = TopMost;
                alertItem.Text = alerts.Count > 0 ? "价格提醒（" + alerts.Count + "）…" : "价格提醒…";
                foreach (var extra in rowItems) { menu.Items.Remove(extra); extra.Dispose(); }
                rowItems.Clear();
                // Right-clicking a quote row offers an alert for that instrument first.
                var row = menuRow >= 0 ? VisibleInstruments.Skip(firstRow + menuRow).FirstOrDefault() : null;
                menuRow = -1;
                if (row == null) return;
                string code = row.Code;
                rowItems.Add(new ToolStripMenuItem("为 " + code + " 设置价格提醒…", null, delegate { OpenAlerts(code); }));
                rowItems.Add(new ToolStripSeparator());
                for (int i = rowItems.Count - 1; i >= 0; i--) menu.Items.Insert(0, rowItems[i]);
            };
            menu.Closed += delegate { Redraw(); };
            ContextMenuStrip = menu;

            clock.Tick += delegate { RefreshQuotes(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F2) ToggleCompact();
                else if (e.KeyCode == Keys.PageDown || e.KeyCode == Keys.PageUp)
                    ScrollQuotes(e.KeyCode == Keys.PageDown ? visibleRows : -visibleRows);
                else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
                    ScrollQuotes(e.KeyCode == Keys.Down ? 1 : -1);
                else if (e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))
                    ShowMenu();
                else return;
                e.Handled = true;
            };
            ready = true;
            LayoutWidget();
            ResetFeeds();
            if (!testing)
            {
                clock.Start();
                Shown += delegate { if (catalogLoad == null) catalogLoad = RefreshCatalog(); };
            }
        }

        static Font MakeFont(float size, FontStyle style, params string[] families)
        {
            foreach (string name in families)
            {
                try
                {
                    var family = new FontFamily(name);
                    if (family.IsStyleAvailable(style)) return new Font(family, size, style);
                }
                catch (ArgumentException) { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        static string OpacityText(int value)
        {
            if (value >= 100) return "不透明";
            if (value <= 0) return "完全透明";
            return "透明 " + (100 - value) + "%" + (value == DefaultOpacity ? "（推荐）" : "");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x00080000; // WS_EX_LAYERED: per-pixel alpha via UpdateLayeredWindow
                return parameters;
            }
        }

        int Px(int value) { return (value * unit + 48) / 96; }
        float Pf(float value) { return value * unit / 96f; }
        IEnumerable<Instrument> VisibleInstruments
        {
            get { return catalog.Where(x => selected.Contains(x.Code)); }
        }

        async void UpdatePicker(WatchDialog picker, Task load)
        {
            await load;
            if (!picker.IsDisposed) picker.SetCatalog(catalog, catalogState);
        }

        async Task RefreshCatalog()
        {
            try
            {
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(10);
                    http.MaxResponseContentBufferSize = 8 * 1024 * 1024;
                    string json = await http.GetStringAsync("https://fapi.binance.com/fapi/v1/exchangeInfo");
                    var entries = ParseCatalog(json);
                    if (IsDisposed || Disposing) return;
                    SetCatalog(entries, "已连接币安");
                    Redraw();
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath));
                        File.WriteAllText(catalogPath, json);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is System.Net.Http.HttpRequestException || ex is OperationCanceledException ||
                    ex is ArgumentException || ex is InvalidOperationException)) throw;
                if (catalogState == "缓存目录") catalogState = "目录暂不可用 · 显示缓存品种";
            }
        }

        void SetCatalog(List<Instrument> entries, string status)
        {
            var actual = new HashSet<string>(entries.Select(item => item.Code));
            entries.AddRange(catalog.Where(item => selected.Contains(item.Code) && !actual.Contains(item.Code)));
            catalog = entries.OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal).ToList();
            catalogState = status;
        }

        internal static List<Instrument> ParseCatalog(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object payload;
            if (root == null || !root.TryGetValue("symbols", out payload) || !(payload is object[]))
                throw new InvalidOperationException("Binance did not return a contract catalog.");
            var result = new Dictionary<string, Instrument>(StringComparer.Ordinal);
            foreach (var item in ((object[])payload).OfType<Dictionary<string, object>>())
            {
                string type = Field(item, "contractType");
                if (Field(item, "status") != "TRADING" || Field(item, "quoteAsset") != "USDT" ||
                    (type != "PERPETUAL" && type != "TRADIFI_PERPETUAL")) continue;
                string symbol = Field(item, "symbol");
                if (!symbol.EndsWith("USDT", StringComparison.Ordinal)) continue;
                string code = symbol.Substring(0, symbol.Length - 4);
                if (!ValidCode(code)) continue;
                string kind = Field(item, "underlyingType");
                bool metal = kind == "COMMODITY" && new[] { "XAU", "XAG", "XPT", "XPD" }.Contains(code);
                string category = metal ? "贵金属" : kind == "EQUITY" ? "美股／ETF" :
                    kind == "COIN" ? "加密货币" : null;
                if (category == null) continue;
                var known = Universe.FirstOrDefault(i => i.Code == code && i.Category == category);
                result[code] = new Instrument(code, known != null ? known.Name : code, metal,
                    known != null && known.Default, category) { Decimals = TickDecimals(item) };
            }
            if (result.Count == 0) throw new InvalidOperationException("No eligible Binance contracts found.");
            return result.Values.ToList();
        }

        static int TickDecimals(Dictionary<string, object> item)
        {
            object filters;
            if (!item.TryGetValue("filters", out filters) || !(filters is object[])) return -1;
            foreach (var filter in ((object[])filters).OfType<Dictionary<string, object>>())
            {
                if (Field(filter, "filterType") != "PRICE_FILTER") continue;
                decimal tick;
                string text = Field(filter, "tickSize");
                if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out tick) || tick <= 0)
                    return -1;
                int dot = text.IndexOf('.');
                return dot < 0 ? 0 : Math.Min(10, text.TrimEnd('0').Length - dot - 1);
            }
            return -1;
        }

        // Decimals actually used, ignoring zero padding from the API (70.5500 -> 2).
        internal static int Scale(decimal value)
        {
            value /= 1.000000000000000000000000000000000m; // normalizes away trailing zeros
            return (decimal.GetBits(value)[3] >> 16) & 0xFF;
        }

        static bool ValidCode(string code)
        {
            return code.Length > 0 && code.Length <= 40 && code.All(c => char.IsLetterOrDigit(c) || c == '_');
        }

        static string Field(Dictionary<string, object> item, string key)
        {
            object value;
            return item.TryGetValue(key, out value) ? (value as string ?? "") : "";
        }

        void ApplySelection()
        {
            ResetFeeds();
            LayoutWidget();
            SaveSettings();
        }

        internal void ToggleCompact()
        {
            compact = !compact;
            firstRow = 0;
            LayoutWidget();
            SaveSettings();
        }

        internal void SetOpacity(int value)
        {
            opacity = Math.Max(0, Math.Min(100, value));
            Redraw();
        }

        int Mode { get { return compact ? 1 : 0; } }
        int Head { get { return compact ? 30 : 42; } }
        int Lead { get { return compact ? 4 : 21; } }
        int Foot { get { return compact ? 15 : 28; } }
        int DesignWidth { get { return compact ? 174 : 312; } }
        int RowTop { get { return Px(Head + Lead); } }
        int RowHeight
        {
            get { return Math.Max(1, (ClientSize.Height - RowTop - Px(Foot)) / Math.Max(1, visibleRows)); }
        }

        void RememberSize()
        {
            layoutWidths[Mode] = (int)Math.Round(ClientSize.Width * 96d / unit);
            int rows = Math.Max(1, Math.Min(selected.Count, 14));
            double height = ClientSize.Height * 96d / unit;
            layoutRows[Mode] = Math.Max(14, (height - Head - Lead - Foot) / rows);
        }

        void LayoutWidget()
        {
            Rectangle area = Screen.FromPoint(Location).WorkingArea;
            int height = Head + Lead + Foot + (int)Math.Round(layoutRows[Mode] * Math.Min(selected.Count, 14));
            ClientSize = new Size(Math.Min(area.Width, Px(layoutWidths[Mode])), Math.Min(area.Height, Px(height)));
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            LayoutChrome();
        }

        void LayoutChrome()
        {
            visibleRows = Math.Min(selected.Count, Math.Max(1,
                (ClientSize.Height - Px(Head + Lead + Foot)) / Px(16)));
            ScrollQuotes(0);
        }

        void ScrollQuotes(int delta)
        {
            firstRow = Math.Max(0, Math.Min(firstRow + delta, selected.Count - visibleRows));
            Redraw();
        }

        void ResetFeeds()
        {
            Instrument[] instruments = VisibleInstruments.ToArray();
            if (stop != null)
            {
                stop.Cancel();
                stop.Dispose();
            }
            var next = new MarketFeed(instruments);
            feed = next;
            WatchAlertTrades();
            feedStarted = DateTime.UtcNow;
            stop = new CancellationTokenSource();
            if (!testing)
            {
                var token = stop.Token;
                Task.Run(() => next.Run(token));
            }
        }

        internal void RefreshQuotes()
        {
            frameTime = DateTimeOffset.UtcNow;
            DateTime now = DateTime.UtcNow;
            bool changed = false;
            foreach (Instrument item in VisibleInstruments)
            {
                var current = feed.Get(item.Symbol);
                if (current == null) continue;
                int seen;
                scales.TryGetValue(item.Symbol, out seen);
                scales[item.Symbol] = Math.Max(seen, Scale(current.Price));
                Quote last;
                if (!displayed.TryGetValue(item.Symbol, out last) || last.Open != current.Open) changed = true;
                if (last != null && last.Price != current.Price)
                {
                    direction[item.Symbol] = current.Price > last.Price ? Green : Red;
                    changedAt[item.Symbol] = now;
                    changed = true;
                }
                displayed[item.Symbol] = current;
            }
            if (CheckAlerts(now)) changed = true;
            bool animating = changedAt.Values.Any(t => (now - t).TotalSeconds < 2.6) ||
                alertedAt.Values.Any(t => (now - t).TotalSeconds < AlertGlowSeconds);
            if (changed || animating || (now - lastFrame).TotalMilliseconds >= 1000) Redraw();
        }

        // ---------- Price alerts ----------

        const double AlertGlowSeconds = 12;

        // Fires each alert once when a live price reaches its target, then removes it.
        bool CheckAlerts(DateTime now)
        {
            var trades = feed.TakeExtremes();
            if (alerts.Count == 0) return false;
            bool fired = false;
            foreach (var alert in alerts.ToList())
            {
                decimal reached = 0;
                decimal[] range;
                Quote quote;
                // Every trade since the last tick (catches brief spikes) ...
                if (trades.TryGetValue(alert.Symbol, out range) && alert.Hit(alert.Above ? range[1] : range[0]))
                    reached = alert.Above ? range[1] : range[0];
                // ... or the latest price, when it is live.
                else if ((feed.Connected || testing) && displayed.TryGetValue(alert.Symbol, out quote) &&
                    (frameTime - quote.EventTime).TotalSeconds < 30 && alert.Hit(quote.Price))
                    reached = quote.Price;
                if (reached <= 0) continue;
                alerts.Remove(alert);
                alertedAt[alert.Symbol] = now;
                fired = true;
                Notify(alert, reached);
            }
            if (fired) { SaveSettings(); WatchAlertTrades(); }
            return fired;
        }

        void Notify(PriceAlert alert, decimal price)
        {
            var item = catalog.FirstOrDefault(i => i.Code == alert.Code);
            int decimals = item != null ? DecimalsFor(item) : -1;
            string text = alert.Symbol + (alert.Above ? " 已涨到 " : " 已跌到 ") + PriceText(price, decimals) +
                "\r\n提醒价：" + alert.Describe(decimals);
            firedLog.Add(alert.Symbol);
            if (testing) return;
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text.Replace("\r\n", "  ·  ");
            var steps = new List<string>();
            // Each delivery channel is independent: one failing must not stop the others.
            try { PlayAlarm(); steps.Add("声音"); }
            catch (Exception ex) { steps.Add("声音失败:" + ex.GetType().Name); }
            try
            {
                // A popup that stays until dismissed, so the alert is seen even when Windows hides notifications.
                if (toast == null || toast.IsDisposed)
                {
                    toast = new AlertToast();
                    toast.Dismissed += delegate
                    {
                        AppendHistory(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  弹窗已关闭（知道了）");
                    };
                }
                toast.Add(line.Substring(11));
                toast.ShowNear(Bounds);
                steps.Add(toast.Visible ? "弹窗" : "弹窗未显示");
            }
            catch (Exception ex) { steps.Add("弹窗失败:" + ex.GetType().Name + " " + ex.Message); }
            try
            {
                EnsureTray();
                tray.ShowBalloonTip(15000, "价格提醒 · " + (item != null ? item.Name : alert.Code), text, ToolTipIcon.Info);
                steps.Add("系统通知");
            }
            catch (Exception ex) { steps.Add("系统通知失败:" + ex.GetType().Name); }
            AppendHistory(line + "  [" + string.Join(", ", steps) + "]");
        }

        static readonly string HistoryPath = Path.Combine(Path.GetDirectoryName(SettingsPath), "提醒记录.txt");

        static void AppendHistory(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath));
                File.AppendAllText(HistoryPath, line + Environment.NewLine, new UTF8Encoding(true));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void PlayAlarm()
        {
            try
            {
                string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                string sound = new[] { "Alarm01.wav", "Windows Notify System Generic.wav", "notify.wav" }
                    .Select(name => Path.Combine(media, name)).FirstOrDefault(File.Exists);
                if (sound == null) { System.Media.SystemSounds.Exclamation.Play(); return; }
                if (alarm != null) alarm.Dispose();
                alarm = new System.Media.SoundPlayer(sound);
                alarm.Play();
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException)) throw;
                System.Media.SystemSounds.Exclamation.Play();
            }
        }

        void WatchAlertTrades()
        {
            feed.WatchTrades(alerts.Select(a => a.Symbol));
        }

        void EnsureTray()
        {
            if (tray != null) return;
            tray = new NotifyIcon { Text = "币安浮动行情", ContextMenuStrip = menu, Visible = true };
            using (var bitmap = new Bitmap(32, 32))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    FillRounded(graphics, Color.FromArgb(255, 22, 28, 37), new RectangleF(1, 1, 30, 30), 7);
                    using (var dot = new SolidBrush(Green)) graphics.FillEllipse(dot, 9, 9, 14, 14);
                }
                IntPtr handle = bitmap.GetHicon();
                trayIcon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
            }
            tray.Icon = trayIcon;
            tray.DoubleClick += delegate { BringToFront(); Activate(); };
            tray.BalloonTipClicked += delegate { BringToFront(); Activate(); };
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        void OpenAlerts(string preset)
        {
            using (var dialog = new AlertDialog(VisibleInstruments.ToList(), alerts,
                symbol => { Quote q; return displayed.TryGetValue(symbol, out q) ? q : null; },
                DecimalsFor, preset))
                dialog.ShowDialog(this);
            SaveSettings();
            WatchAlertTrades();
            Redraw();
        }

        bool Fresh(Instrument item)
        {
            Quote quote;
            return feed.Connected && displayed.TryGetValue(item.Symbol, out quote) &&
                (frameTime - quote.EventTime).TotalSeconds < 30;
        }

        void LoadSettings()
        {
            try
            {
                string[] lines = File.ReadAllLines(SettingsPath);
                if (lines.Length < 5) return;
                if (lines.Length >= 10)
                {
                    bool clear;
                    if (bool.TryParse(lines[5], out clear)) opacity = clear ? DefaultOpacity : 100;
                    for (int mode = 0; mode < 2; mode++)
                    {
                        int width;
                        double row;
                        if (int.TryParse(lines[6 + mode * 2], out width) && width >= 123 && width <= 4000)
                            layoutWidths[mode] = width;
                        if (double.TryParse(lines[7 + mode * 2], NumberStyles.Float, CultureInfo.InvariantCulture, out row) &&
                            row >= 14 && row <= 2000) layoutRows[mode] = row;
                    }
                }
                int level;
                if (lines.Length >= 11 && int.TryParse(lines[10], out level) && level >= 0 && level <= 100)
                    opacity = level;
                if (lines.Length >= 12)
                    alerts.AddRange(lines[11].Split(';').Select(PriceAlert.Parse).Where(a => a != null).Take(100));
                bool value;
                if (bool.TryParse(lines[0], out value)) compact = value;
                if (bool.TryParse(lines[1], out value)) TopMost = value;
                int x, y;
                if (int.TryParse(lines[2], out x) && int.TryParse(lines[3], out y) &&
                    Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(x, y))))
                    Location = new Point(x, y);
                var restored = lines[4].Split(',').Where(ValidCode).ToArray();
                if (restored.Length > 0)
                {
                    selected.Clear();
                    selected.UnionWith(restored);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void SaveSettings()
        {
            if (testing) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllLines(SettingsPath, new[] { compact.ToString(), TopMost.ToString(),
                    Left.ToString(CultureInfo.InvariantCulture), Top.ToString(CultureInfo.InvariantCulture),
                    string.Join(",", selected), (opacity < 100).ToString(),
                    layoutWidths[0].ToString(CultureInfo.InvariantCulture), layoutRows[0].ToString(CultureInfo.InvariantCulture),
                    layoutWidths[1].ToString(CultureInfo.InvariantCulture), layoutRows[1].ToString(CultureInfo.InvariantCulture),
                    opacity.ToString(CultureInfo.InvariantCulture),
                    string.Join(";", alerts.Select(a => a.Serialize())) });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Redraw();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Redraw();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            clock.Stop();
            SaveSettings();
            if (stop != null) { stop.Cancel(); stop.Dispose(); stop = null; }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (toast != null) toast.Dispose();
                if (alarm != null) alarm.Dispose();
                if (trayIcon != null) trayIcon.Dispose();
                clock.Dispose(); menu.Dispose(); tips.Dispose();
                ui.Dispose(); title.Dispose(); caption.Dispose(); code.Dispose(); miniCode.Dispose();
                digits.Dispose(); miniDigits.Dispose(); change24.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------- Mouse, hit testing and window behaviour ----------

        Rectangle ButtonRect(int index)
        {
            int size = Px(compact ? 22 : 26);
            return new Rectangle(ClientSize.Width - Px(compact ? 4 : 7) - size * (index + 1),
                (Px(Head) - size) / 2, size, size);
        }

        static readonly Hit[] ButtonHits = { Hit.Close, Hit.Compact, Hit.Menu };

        Rectangle GripRect
        {
            get { int size = Px(18); return new Rectangle(ClientSize.Width - size, ClientSize.Height - size, size, size); }
        }

        Hit HitTest(Point point)
        {
            for (int i = 0; i < ButtonHits.Length; i++)
                if (ButtonRect(i).Contains(point)) return ButtonHits[i];
            if (GripRect.Contains(point)) return Hit.Grip;
            return ClientRectangle.Contains(point) ? Hit.Body : Hit.None;
        }

        int RowAt(int y)
        {
            int top = RowTop, height = RowHeight;
            if (y < top || y >= top + height * visibleRows) return -1;
            return (y - top) / height;
        }

        string TipFor(Hit hit, int row)
        {
            if (hit == Hit.Menu) return "菜单：自选品种、透明度、置顶 (右键同样可用)";
            if (hit == Hit.Compact) return compact ? "恢复普通模式 (F2)" : "切换极简模式 (F2)";
            if (hit == Hit.Close) return "关闭";
            if (hit == Hit.Grip) return "拖动调整大小";
            if (hit == Hit.Body && row >= 0 && !ShowNames)
            {
                var item = VisibleInstruments.Skip(firstRow + row).FirstOrDefault();
                if (item == null) return null;
                Quote quote;
                displayed.TryGetValue(item.Symbol, out quote);
                string change = quote == null ? "" : ChangeText(quote.Change);
                return item.Symbol + " · " + item.Name + (change.Length > 0 ? " · 24h " + change : "");
            }
            return null;
        }

        void ShowMenu()
        {
            Rectangle anchor = ButtonRect(2);
            menu.Show(this, new Point(anchor.Right, anchor.Bottom + Px(2)), ToolStripDropDownDirection.BelowLeft);
        }

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) menuRow = HitTest(e.Location) == Hit.Body ? RowAt(e.Y) : -1;
            if (e.Button != MouseButtons.Left) return;
            Hit hit = HitTest(e.Location);
            if (hit == Hit.Grip)
            {
                resizing = true;
                resizeOrigin = Cursor.Position;
                resizeStart = ClientSize;
                Capture = true;
                Redraw();
            }
            else if (hit == Hit.Body)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION: native drag
            }
            else if (hit != Hit.None)
            {
                pressed = hit;
                Capture = true;
                Redraw();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (resizing)
            {
                Rectangle bounds = Screen.FromPoint(Location).WorkingArea;
                ClientSize = new Size(
                    Math.Max(Px(compact ? 123 : 171), Math.Min(bounds.Right - Left, resizeStart.Width + Cursor.Position.X - resizeOrigin.X)),
                    Math.Max(Px(Head + Lead + Foot + 14), Math.Min(bounds.Bottom - Top, resizeStart.Height + Cursor.Position.Y - resizeOrigin.Y)));
                RememberSize();
                return;
            }
            Hit hit = HitTest(e.Location);
            int row = hit == Hit.Body ? RowAt(e.Y) : -1;
            Cursor = hit == Hit.Grip ? Cursors.SizeNWSE : hit == Hit.Body || hit == Hit.None ? Cursors.Default : Cursors.Hand;
            string tip = TipFor(hit, row);
            if (tip != tipText) { tipText = tip; tips.SetToolTip(this, tip); }
            if (!inside || hit != hover || row != hoverRow)
            {
                inside = true;
                hover = hit;
                hoverRow = row;
                Redraw();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (resizing)
            {
                resizing = false;
                Capture = false;
                SaveSettings();
                Redraw();
                return;
            }
            if (pressed == Hit.None) return;
            Hit target = pressed;
            pressed = Hit.None;
            Capture = false;
            if (HitTest(e.Location) == target)
            {
                if (target == Hit.Close) { Close(); return; }
                if (target == Hit.Compact) ToggleCompact();
                if (target == Hit.Menu) ShowMenu();
            }
            Redraw();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture) return;
            if (resizing) { resizing = false; SaveSettings(); }
            if (pressed != Hit.None) pressed = Hit.None;
            Redraw();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (resizing) return;
            inside = false;
            hover = Hit.None;
            hoverRow = -1;
            Redraw();
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232, WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED && m.LParam != IntPtr.Zero)
            {
                // Per-monitor DPI: re-render at the new monitor's scale instead of being bitmap-stretched.
                int dpi = (int)((long)m.WParam & 0xFFFF);
                var suggested = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                if (dpi > 0 && dpi != unit)
                {
                    unit = dpi;
                    Location = new Point(suggested.Left, suggested.Top);
                    LayoutWidget();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_MOVING && m.LParam != IntPtr.Zero)
            {
                // Snap to the edges of the working area while dragging.
                var rect = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top, snap = Px(14);
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                if (Math.Abs(rect.Left - area.Left) < snap) rect.Left = area.Left;
                else if (Math.Abs(area.Right - rect.Right) < snap) rect.Left = area.Right - w;
                if (Math.Abs(rect.Top - area.Top) < snap) rect.Top = area.Top;
                else if (Math.Abs(area.Bottom - rect.Bottom) < snap) rect.Top = area.Bottom - h;
                rect.Right = rect.Left + w;
                rect.Bottom = rect.Top + h;
                Marshal.StructureToPtr(rect, m.LParam, false);
            }
            base.WndProc(ref m);
            if (m.Msg == WM_EXITSIZEMOVE) SaveSettings();
        }

        // ---------- Rendering (per-pixel alpha layered window) ----------

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        void Redraw()
        {
            if (!ready || feed == null || !IsHandleCreated || IsDisposed || Disposing ||
                WindowState == FormWindowState.Minimized || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            lastFrame = DateTime.UtcNow;
            using (var bitmap = Snapshot()) Push(bitmap);
        }

        Bitmap Snapshot()
        {
            var bitmap = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(bitmap)) Draw(graphics);
            return bitmap;
        }

        void Push(Bitmap bitmap)
        {
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                var info = new BITMAPINFOHEADER {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = bitmap.Width,
                    biHeight = -bitmap.Height, biPlanes = 1, biBitCount = 32 };
                IntPtr bits;
                dib = CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return;
                var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var buffer = new byte[bitmap.Width * 4];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, buffer, 0, buffer.Length);
                        Marshal.Copy(buffer, 0, bits + y * buffer.Length, buffer.Length);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                old = SelectObject(memory, dib);
                var size = new SIZE { Width = bitmap.Width, Height = bitmap.Height };
                var origin = new POINT();
                var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
                UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, 2); // ULW_ALPHA
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memory, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        int PanelAlpha()
        {
            int alpha = opacity * 255 / 100;
            // Hovering a very clear panel lifts it slightly so it is easy to read and grab;
            // alpha never drops to 0 so the whole panel stays draggable.
            if (inside || resizing || pressed != Hit.None) alpha = Math.Max(alpha, 82);
            return Math.Max(2, alpha);
        }

        bool ShowNames
        {
            get { return !compact && RowHeight >= Px(32) && ClientSize.Width >= Px(240); }
        }

        static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                (int)(from.A + (to.A - from.A) * amount), (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
        }

        static Color Fade(Color color, int alpha)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), color);
        }

        static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (d <= 1) { path.AddRectangle(rect); return path; }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        void FillRounded(Graphics graphics, Color color, RectangleF rect, float radius)
        {
            if (color.A == 0 || rect.Width <= 0 || rect.Height <= 0) return;
            using (var path = RoundedRect(rect, radius))
            using (var brush = new SolidBrush(color))
                graphics.FillPath(brush, path);
        }

        static bool HasWide(string value)
        {
            foreach (char c in value) if (c > 0x2E7F) return true;
            return false;
        }

        // Draws text as an anti-aliased path, vertically centred on the cap height, shrinking it to fit.
        // halo > 0 adds a soft dark outline so text stays legible on any desktop behind a clear panel.
        RectangleF Write(Graphics graphics, string value, Font font, Color color, float left, float top,
            float right, float height, int halo, double scale = 1, bool alignRight = false, Color backdrop = default(Color))
        {
            if (string.IsNullOrEmpty(value) || right - left < 1 || height < 1) return RectangleF.Empty;
            float em = (float)(font.SizeInPoints * unit / 72d * scale);
            FontFamily family = font.FontFamily;
            using (var path = new GraphicsPath())
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                path.AddString(value, family, (int)font.Style, em, PointF.Empty, format);
                RectangleF box = path.GetBounds();
                if (box.Width <= 0 || box.Height <= 0) return RectangleF.Empty;
                float fit = Math.Min(1f, (right - left) / box.Width);
                float ascent = em * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
                float lift = (HasWide(value) ? .40f : .355f) * em;
                float dx = alignRight ? right - box.Right * fit : left - box.Left * fit;
                float dy = top + height / 2f + fit * (lift - ascent);
                using (var matrix = new Matrix(fit, 0, 0, fit, dx, dy)) path.Transform(matrix);
                RectangleF bounds = path.GetBounds();
                if (backdrop.A > 0)
                {
                    var pill = bounds;
                    pill.Inflate(Pf(5), Pf(3));
                    FillRounded(graphics, backdrop, pill, Pf(4));
                }
                if (halo > 0)
                {
                    float size = em * fit;
                    using (var outer = new Pen(Color.FromArgb(halo / 4, 0, 0, 0), Math.Max(Pf(3.4f), size * .2f)) { LineJoin = LineJoin.Round })
                        graphics.DrawPath(outer, path);
                    using (var inner = new Pen(Color.FromArgb(halo * 4 / 5, 0, 0, 0), Math.Max(Pf(1.5f), size * .085f)) { LineJoin = LineJoin.Round })
                        graphics.DrawPath(inner, path);
                }
                using (var brush = new SolidBrush(color)) graphics.FillPath(brush, path);
                return bounds;
            }
        }

        void DrawBell(Graphics graphics, float x, float cy, float size, Color color, int halo)
        {
            float w = size * .82f, h = size, top = cy - h / 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(x + w * .14f, top, w * .72f, h * .8f, 180, 180);
                path.AddLine(x + w * .86f, top + h * .4f, x + w, top + h * .76f);
                path.AddLine(x + w, top + h * .76f, x, top + h * .76f);
                path.AddLine(x, top + h * .76f, x + w * .14f, top + h * .4f);
                path.CloseFigure();
                path.AddEllipse(x + w * .37f, top + h * .79f, w * .26f, h * .21f);
                if (halo > 0)
                    using (var pen = new Pen(Color.FromArgb(halo, 0, 0, 0), Pf(1.6f)) { LineJoin = LineJoin.Round })
                        graphics.DrawPath(pen, path);
                using (var brush = new SolidBrush(color)) graphics.FillPath(brush, path);
            }
        }

        void DrawIcon(Graphics graphics, Hit kind, RectangleF rect, Color ink, int halo)
        {
            float cx = rect.X + rect.Width / 2f, cy = rect.Y + rect.Height / 2f, s = rect.Width * .2f;
            var lines = new List<PointF[]>();
            if (kind == Hit.Menu)
                for (int i = -1; i <= 1; i++)
                    lines.Add(new[] { new PointF(cx - s, cy + i * s * .75f), new PointF(cx + s, cy + i * s * .75f) });
            else if (kind == Hit.Compact)
            {
                lines.Add(new[] { new PointF(cx - s, cy), new PointF(cx + s, cy) });
                if (compact) lines.Add(new[] { new PointF(cx, cy - s), new PointF(cx, cy + s) });
            }
            else
            {
                float k = s * .85f;
                lines.Add(new[] { new PointF(cx - k, cy - k), new PointF(cx + k, cy + k) });
                lines.Add(new[] { new PointF(cx - k, cy + k), new PointF(cx + k, cy - k) });
            }
            float width = Pf(1.5f);
            if (halo > 0)
                using (var shadow = new Pen(Color.FromArgb(halo, 0, 0, 0), width + Pf(2.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    foreach (var line in lines) graphics.DrawLine(shadow, line[0], line[1]);
            using (var pen = new Pen(ink, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                foreach (var line in lines) graphics.DrawLine(pen, line[0], line[1]);
        }

        void Draw(Graphics graphics)
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            int width = ClientSize.Width, height = ClientSize.Height;
            int alpha = PanelAlpha();
            int halo = alpha < 100 ? 150 : alpha < 180 ? 60 : 0;
            int head = Px(Head);
            int count = selected.Count;
            Instrument[] all = VisibleInstruments.ToArray();
            int live = all.Count(Fresh);
            DateTime now = DateTime.UtcNow;

            // Glass panel.
            var outline = new RectangleF(.5f, .5f, width - 1, height - 1);
            float radius = Pf(compact ? 8 : 12);
            using (var shape = RoundedRect(outline, radius))
            {
                using (var fill = new LinearGradientBrush(new Rectangle(0, -1, width, height + 2),
                    Fade(GlassTop, alpha), Fade(GlassBottom, alpha), 90f))
                    graphics.FillPath(fill, shape);
                if (alpha >= 24)
                {
                    // Gradient brushes tile, so clip the header sheen to the header band.
                    graphics.SetClip(new Rectangle(0, 0, width, head));
                    using (var sheen = new LinearGradientBrush(new Rectangle(0, -1, width, head + 2),
                        Color.FromArgb(Math.Min(16, alpha / 10), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                        graphics.FillPath(sheen, shape);
                    graphics.ResetClip();
                    using (var edge = new Pen(Color.FromArgb(Math.Min(52, 16 + alpha / 7), 255, 255, 255), Pf(1)))
                        graphics.DrawPath(edge, shape);
                }
            }

            int left = Px(compact ? 9 : 12);
            int right = width - left;
            int divider = (int)(width * (compact ? .34 : .43));
            int rowHeight = RowHeight;
            int top = RowTop;
            double scale = Math.Max(.64, Math.Min(1.65, Math.Min(width / (double)Px(DesignWidth),
                rowHeight / (double)Px(compact ? 24 : 42))));
            double chromeScale = Math.Min(1, width / (double)Px(DesignWidth));
            bool names = ShowNames;

            // Header: connection light, title and icon buttons.
            Color state = feed.Connected ? (live == count ? Green : Gold) :
                (now - feedStarted).TotalSeconds < 12 ? Gold : Red;
            float dot = Pf(compact ? 6 : 7);
            float cy = head / 2f;
            using (var glow = new SolidBrush(Fade(state, 55)))
                graphics.FillEllipse(glow, left - dot * .4f, cy - dot * .9f, dot * 1.8f, dot * 1.8f);
            using (var core = new SolidBrush(state))
                graphics.FillEllipse(core, left + dot * .05f, cy - dot / 2f, dot, dot);
            int buttonsLeft = ButtonRect(2).Left;
            string heading = compact ? (width < Px(150) ? "永续" : "USDT 永续") : "币安永续";
            Write(graphics, heading, compact ? ui : title, Main, left + dot + Px(7), 0, buttonsLeft - Px(4), head, halo,
                chromeScale);

            bool chromeVisible = inside || alpha >= 90 || pressed != Hit.None;
            for (int i = 0; i < ButtonHits.Length; i++)
            {
                Hit kind = ButtonHits[i];
                RectangleF rect = ButtonRect(i);
                bool hot = hover == kind, down = pressed == kind && hot;
                if (hot)
                    FillRounded(graphics, kind == Hit.Close ? Color.FromArgb(down ? 255 : 225, 226, 64, 86) :
                        Color.FromArgb(down ? 46 : 30, 255, 255, 255), rect, Pf(6));
                Color ink = hot ? (kind == Hit.Close ? Color.White : Main) : Fade(Muted, chromeVisible ? 255 : 170);
                DrawIcon(graphics, kind, rect, ink, hot ? 0 : halo / 2);
            }

            if (alpha >= 24)
                using (var rule = new Pen(Color.FromArgb(Math.Min(26, alpha / 6), 255, 255, 255), Pf(1)))
                    graphics.DrawLine(rule, Px(8), head, width - Px(8), head);

            if (!compact)
            {
                Write(graphics, "品种", caption, Muted, left, head, divider, Px(Lead), halo, chromeScale);
                Write(graphics, "最新价 · 24h 涨跌", caption, Muted, divider, head, right, Px(Lead), halo, chromeScale, true);
            }

            // Quote rows.
            int index = 0;
            foreach (Instrument item in all.Skip(firstRow).Take(visibleRows))
            {
                int y = top + index * rowHeight;
                if (hoverRow == index)
                    FillRounded(graphics, Color.FromArgb(16, 255, 255, 255),
                        new RectangleF(Px(4), y + Pf(1), width - Px(8), rowHeight - Pf(2)), Pf(6));
                else if (index > 0 && alpha >= 120 && hoverRow != index - 1)
                    using (var rule = new Pen(Color.FromArgb(14, 255, 255, 255), Pf(1)))
                        graphics.DrawLine(rule, left, y, right, y);

                Quote quote;
                displayed.TryGetValue(item.Symbol, out quote);
                string price = quote == null ? "—" : PriceText(quote.Price, DecimalsFor(item));
                decimal? change = quote == null ? null : quote.Change;
                string changeText = ChangeText(change);
                Color changeColor = Fresh(item) ? ChangeColor(change) : Muted;
                Color tint = Main, flash = Color.Empty;
                Color move;
                DateTime at;
                if (!Fresh(item)) tint = Muted;
                else if (direction.TryGetValue(item.Symbol, out move) && changedAt.TryGetValue(item.Symbol, out at))
                {
                    double age = (now - at).TotalSeconds;
                    tint = age < 1.2 ? move : Blend(move, Main, (age - 1.2) / 1.3);
                    if (age < .9) flash = Fade(move, (int)(64 * (1 - age / .9)));
                }

                // A just-triggered alert makes the row pulse gold for a few seconds.
                DateTime alerted;
                double alertAge = alertedAt.TryGetValue(item.Symbol, out alerted) ? (now - alerted).TotalSeconds : double.MaxValue;
                if (alertAge < AlertGlowSeconds)
                {
                    double pulse = .5 + .5 * Math.Cos(alertAge * Math.PI * 2 / 1.2);
                    int glow = (int)((24 + 40 * pulse) * (1 - alertAge / AlertGlowSeconds));
                    FillRounded(graphics, Fade(Gold, glow), new RectangleF(Px(4), y + Pf(1), width - Px(8), rowHeight - Pf(2)), Pf(6));
                }

                Font codeFont = compact ? miniCode : code;
                RectangleF codeBox = Write(graphics, item.Code, codeFont, item.Metal ? Gold : Main, left, y, divider - Px(4),
                    names ? (int)(rowHeight * .56) : rowHeight, halo, scale);
                bool armed = alerts.Any(a => a.Code == item.Code);
                if ((armed || alertAge < AlertGlowSeconds) && codeBox.Width > 0)
                    DrawBell(graphics, codeBox.Right + Pf(4), codeBox.Top + codeBox.Height / 2, Pf(compact ? 9 : 10) * (float)Math.Min(1.3, scale),
                        armed ? Fade(Gold, 210) : Gold, halo);
                if (names)
                    Write(graphics, item.Name, caption, Muted, left, y + (int)(rowHeight * .52), divider - Px(4),
                        (int)(rowHeight * .42), halo, scale);
                if (names)
                {
                    // Two lines: price on the code line, 24h change on the name line.
                    Write(graphics, price, digits, tint, divider + Px(4), y, right, (int)(rowHeight * .58),
                        halo, scale, true, flash);
                    Write(graphics, changeText, change24, changeColor, divider + Px(4), y + (int)(rowHeight * .54),
                        right, (int)(rowHeight * .40), halo, scale, true);
                }
                else if (!compact && changeText.Length > 0)
                {
                    // One line: a narrow change column to the right of the price.
                    int column = (int)(Px(60) * Math.Min(1.3, scale));
                    Write(graphics, price, digits, tint, divider + Px(4), y, right - column - Px(6), rowHeight,
                        halo, scale, true, flash);
                    Write(graphics, changeText, change24, changeColor, right - column, y, right, rowHeight, halo, scale, true);
                }
                else
                    Write(graphics, price, compact ? miniDigits : digits, tint, divider + Px(4), y, right, rowHeight,
                        halo, scale, true, flash);
                index++;
            }

            // Scroll indicator.
            int footTop = top + visibleRows * rowHeight;
            if (visibleRows < count)
            {
                float trackTop = top + Pf(3), track = visibleRows * rowHeight - Pf(6);
                float thumb = Math.Max(Pf(14), track * visibleRows / count);
                float offset = (track - thumb) * firstRow / Math.Max(1, count - visibleRows);
                FillRounded(graphics, Color.FromArgb(inside ? 110 : 60, 255, 255, 255),
                    new RectangleF(width - Pf(5), trackTop + offset, Pf(3), thumb), Pf(1.5f));
            }

            // Footer: status on the left, position / count on the right.
            int footHeight = Math.Max(1, height - footTop);
            string range = visibleRows < count ? (firstRow + 1) + "–" + (firstRow + visibleRows) + " / " + count : "";
            if (!compact)
            {
                string status = live == count && count > 0 ? "实时 · " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) :
                    feed.Connected ? "部分品种暂无新成交" : feed.Status;
                Color statusColor = live == count && count > 0 ? Green : feed.Connected ? Gold : state;
                if (range.Length == 0) range = count + " 个品种";
                Write(graphics, status, caption, statusColor, left, footTop, width - Px(84), footHeight, halo, chromeScale);
            }
            if (range.Length > 0)
                Write(graphics, range, caption, Muted, width - Px(84), footTop, width - Px(20), footHeight, halo,
                    compact ? .85 : chromeScale, true);

            // Resize grip: a small triangle of dots, hidden on a fully clear, idle panel.
            int gripAlpha = hover == Hit.Grip || resizing ? 220 : inside ? 110 : alpha >= 24 ? 60 : 0;
            if (gripAlpha > 0)
            {
                float step = Pf(3.5f), size = Pf(1.9f);
                float ox = width - Pf(6), oy = height - Pf(6);
                using (var brush = new SolidBrush(Color.FromArgb(gripAlpha, 255, 255, 255)))
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j <= i; j++)
                            graphics.FillEllipse(brush, ox - j * step - size / 2, oy - (i - j) * step - size / 2, size, size);
            }
        }

        internal static string PriceText(decimal price)
        {
            return price.ToString("#,0.00##########", CultureInfo.InvariantCulture);
        }

        // Fixed number of decimals so digits do not jump between ticks.
        internal static string PriceText(decimal price, int decimals)
        {
            if (decimals < 0) return PriceText(price);
            decimals = Math.Min(10, decimals);
            return Math.Round(price, decimals, MidpointRounding.AwayFromZero)
                .ToString(decimals == 0 ? "#,0" : "#,0." + new string('0', decimals), CultureInfo.InvariantCulture);
        }

        internal static string ChangeText(decimal? change)
        {
            if (!change.HasValue) return "";
            decimal percent = Math.Round(change.Value * 100, 2, MidpointRounding.AwayFromZero);
            return (percent > 0 ? "+" : "") + percent.ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }

        int DecimalsFor(Instrument item)
        {
            if (item.Decimals >= 0) return item.Decimals;
            int seen;
            return scales.TryGetValue(item.Symbol, out seen) ? Math.Max(2, seen) : -1;
        }

        static Color ChangeColor(decimal? change)
        {
            if (!change.HasValue || change.Value == 0) return Muted;
            return change.Value > 0 ? Green : Red;
        }

        internal static void Test(string directory)
        {
            using (var form = new Widget(true))
            {
                if (form.catalog.Count < 700 || !form.catalog.Any(i => i.Symbol == "SNDKUSDT" && i.Category == "美股／ETF"))
                    throw new InvalidOperationException("Bundled Binance contract catalog was not loaded.");
                string snapshotPath = Path.Combine(directory, "test-binance-prices.json");
                if (File.Exists(snapshotPath))
                {
                    var fixture = new MarketFeed(form.catalog.Where(i => i.Code == "SNDK" || i.Code == "BTC" || i.Code == "XAU"));
                    if (!fixture.Accept(File.ReadAllText(snapshotPath)) ||
                        new[] { "SNDKUSDT", "BTCUSDT", "XAUUSDT" }.Any(s => fixture.Get(s) == null))
                        throw new InvalidOperationException("Binance price response validation failed.");
                }
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string btc = "[{\"symbol\":\"BTCUSDT\",\"price\":\"68012.34\",\"time\":" + now + "}]";
                string gold = "[{\"symbol\":\"XAUUSDT\",\"price\":\"4200.018\",\"time\":" + now + "}]";
                if (!form.feed.Accept(btc) || !form.feed.Accept(gold) ||
                    form.feed.Accept(gold.Replace("\"price\"", "\"markPrice\"")) ||
                    form.feed.Accept(btc.Replace("BTCUSDT", "UNKNOWNUSDT")) ||
                    form.feed.Accept("{}") || form.feed.Accept(btc.Replace("68012.34", "-1")))
                    throw new InvalidOperationException("Feed validation failed.");
                form.feed.Accept(btc.Replace("68012.34", "500.00").Replace(now.ToString(), (now - 1000).ToString()));
                form.RefreshQuotes();
                if (form.displayed["BTCUSDT"].Price != 68012.34m ||
                    form.displayed["XAUUSDT"].Price != 4200.018m ||
                    form.Fresh(Universe[0]) || PriceText(0.124876m) != "0.124876")
                    throw new InvalidOperationException("Price display validation failed.");
                string backup = "[{\"e\":\"24hrMiniTicker\",\"s\":\"BTCUSDT\",\"c\":\"68013.00\",\"E\":" + (now + 1) + ",\"st\":1}]";
                if (form.feed.Accept(backup.Replace("\"st\":1", "\"st\":2"), true) ||
                    !form.feed.Accept(backup, true) || form.feed.Get("BTCUSDT").Price != 68013m)
                    throw new InvalidOperationException("USD-M backup quote validation failed.");
                form.RefreshQuotes();
                if (form.direction["BTCUSDT"] != Green || !form.changedAt.ContainsKey("BTCUSDT"))
                    throw new InvalidOperationException("Price change highlight validation failed.");
                string day = "[{\"symbol\":\"XAUUSDT\",\"lastPrice\":\"4200.02\",\"openPrice\":\"4000.00\",\"closeTime\":" + (now + 2) + "}," +
                    "{\"symbol\":\"BTCUSDT\",\"lastPrice\":\"68013.00\",\"openPrice\":\"70000.00\",\"closeTime\":" + (now + 2) + "}]";
                if (!form.feed.Accept(day, FeedSource.Day) || form.feed.Get("XAUUSDT").Open != 4000m ||
                    ChangeText(form.feed.Get("XAUUSDT").Change) != "+5.00%" || ChangeText(form.feed.Get("BTCUSDT").Change) != "-2.84%")
                    throw new InvalidOperationException("24h change validation failed.");
                string tick = "[{\"e\":\"24hrMiniTicker\",\"s\":\"BTCUSDT\",\"c\":\"68020.10\",\"o\":\"68000.00\",\"E\":" + (now + 3) + "}]";
                form.feed.Accept(btc.Replace("68012.34", "68030.00").Replace(now.ToString(), (now + 4).ToString()));
                if (!form.feed.Accept(tick, FeedSource.Stream) || form.feed.Get("BTCUSDT").Price != 68030m ||
                    form.feed.Get("BTCUSDT").Open != 68000m)
                    throw new InvalidOperationException("Stream open merge validation failed.");
                if (PriceText(65005.2m, 2) != "65,005.20" || PriceText(0.12m, 5) != "0.12000" || PriceText(1234.5m, 0) != "1,235" ||
                    ChangeText(0m) != "0.00%" || ChangeText(null) != "" ||
                    Scale(70.5500m) != 2 || Scale(64222.50m) != 1 || Scale(100.000m) != 0)
                    throw new InvalidOperationException("Price precision validation failed.");
                form.RefreshQuotes();
                form.Show();
                Application.DoEvents();
                if ((GetWindowLong(form.Handle, -20) & 0x00080000) == 0)
                    throw new InvalidOperationException("Layered window style is missing.");
                foreach (int level in new[] { 100, 55, 0 })
                {
                    form.SetOpacity(level);
                    if (form.opacity != level || form.PanelAlpha() < 2)
                        throw new InvalidOperationException("Opacity validation failed.");
                    form.RenderTest(directory, "normal-" + level + ".png");
                }
                form.SetOpacity(DefaultOpacity);
                form.RenderTest(directory, "normal.png");
                int normalWidth = form.Width, normalHeight = form.Height;
                form.ToggleCompact();
                if (!form.compact || form.Width >= normalWidth || form.Height >= normalHeight)
                    throw new InvalidOperationException("Compact mode validation failed.");
                form.RenderTest(directory, "compact.png");
                if (form.HitTest(new Point(form.ClientSize.Width - form.Px(2), form.ClientSize.Height - form.Px(2))) != Hit.Grip ||
                    form.HitTest(form.ButtonRect(0).Location + new Size(2, 2)) != Hit.Close ||
                    form.HitTest(new Point(form.Px(40), form.ClientSize.Height / 2)) != Hit.Body)
                    throw new InvalidOperationException("Hit testing validation failed.");
                using (var picker = new WatchDialog(form.catalog, form.selected, "test"))
                {
                    picker.Show(form);
                    Application.DoEvents();
                    picker.ToggleForTest("ETH");
                    picker.ToggleForTest("XPT");
                    picker.ToggleForTest("NVDA", "英伟达");
                    picker.ToggleForTest("SNDK", "闪迪");
                    if (picker.Selection.Contains("ETH") || !picker.Selection.Contains("XPT") ||
                        !picker.Selection.Contains("NVDA") || !picker.Selection.Contains("SNDK") || picker.Selection.Count() != 7 ||
                        picker.PickedCount != 7)
                        throw new InvalidOperationException("Watchlist validation failed.");
                    picker.RenderForTest(Path.Combine(directory, "watchlist.png"));
                    picker.RemoveForTest("XPT");
                    picker.ToggleForTest("XPT", "XPT");
                    picker.ToggleForTest("XPT", "XPT");
                    picker.RemoveForTest("XPT");
                    if (picker.Selection.Contains("XPT") || picker.Selection.Count() != 6 || picker.PickedCount != 6)
                        throw new InvalidOperationException("Watchlist removal validation failed.");
                }
                form.ClientSize = new Size(form.Width + form.Px(73), form.Height + form.Px(36));
                form.RememberSize();
                Size resized = form.ClientSize;
                form.ToggleCompact();
                form.ToggleCompact();
                if (Math.Abs(form.Width - resized.Width) > 2 || Math.Abs(form.Height - resized.Height) > 2)
                    throw new InvalidOperationException("Custom size was not preserved.");
                form.RenderTest(directory, "resized.png");
                form.ToggleCompact();
                form.selected.UnionWith(new[] { "BNB", "XRP", "DOGE", "TSLA", "NVDA", "SNDK" });
                form.LayoutWidget();
                form.ClientSize = new Size(form.ClientSize.Width, form.Px(260));
                form.ScrollQuotes(2);
                form.RenderTest(directory, "scrolled.png");

                // Price alerts: parse, dialog, trigger once, persistence format.
                var parsed = PriceAlert.Parse("BTC>70000.5");
                decimal typed;
                if (parsed == null || !parsed.Above || parsed.Target != 70000.5m || parsed.Serialize() != "BTC>70000.5" ||
                    PriceAlert.Parse("XAU<4000").Above || PriceAlert.Parse("bad") != null || PriceAlert.Parse("BTC>-1") != null ||
                    !PriceAlert.TryReadPrice(" 70,000.5 ", out typed) || typed != 70000.5m || PriceAlert.TryReadPrice("abc", out typed))
                    throw new InvalidOperationException("Alert parsing validation failed.");
                form.firstRow = 0;
                using (var dialog = new AlertDialog(form.VisibleInstruments.ToList(), form.alerts,
                    s => { Quote q; return form.displayed.TryGetValue(s, out q) ? q : null; }, form.DecimalsFor, "BTC"))
                {
                    dialog.Show(form);
                    Application.DoEvents();
                    dialog.SetForTest("BTC", "68,100");
                    string first = dialog.AddAlert();
                    dialog.SetForTest("XAU", "4000");
                    string second = dialog.AddAlert();
                    dialog.SetForTest("XAU", "4000");
                    string duplicate = dialog.AddAlert();
                    dialog.SetForTest("BTC", "0");
                    string invalid = dialog.AddAlert();
                    if (first != null || second != null || duplicate == null || invalid == null || form.alerts.Count != 2 ||
                        !form.alerts[0].Above || form.alerts[1].Above)
                        throw new InvalidOperationException("Alert dialog validation failed.");
                }
                form.RenderTest(directory, "alerts-armed.png");
                form.feed.Accept("[{\"symbol\":\"BTCUSDT\",\"price\":\"68150.00\",\"time\":" + (now + 10) + "}]");
                form.RefreshQuotes();
                if (form.alerts.Count != 1 || form.alerts[0].Code != "XAU" || form.firedLog.Count != 1 ||
                    form.firedLog[0] != "BTCUSDT" || !form.alertedAt.ContainsKey("BTCUSDT"))
                    throw new InvalidOperationException("Alert trigger validation failed.");
                form.RefreshQuotes();
                if (form.firedLog.Count != 1) throw new InvalidOperationException("Alert fired twice.");
                form.RenderTest(directory, "alerts-fired.png");

                // A brief spike seen only in the trade stream must fire the alert even though the last price never got there.
                form.alerts.Add(new PriceAlert("BTC", true, 69000m));
                form.WatchAlertTrades();
                string spike = "{\"stream\":\"btcusdt@aggTrade\",\"data\":{\"e\":\"aggTrade\",\"s\":\"BTCUSDT\",\"p\":\"69000.50\",\"T\":" + (now + 11) + "}}";
                if (!form.feed.AcceptTrades(spike) || form.feed.AcceptTrades(spike.Replace("BTCUSDT", "FOOUSDT")) ||
                    form.feed.AcceptTrades("{}"))
                    throw new InvalidOperationException("Trade stream parsing failed.");
                form.RefreshQuotes();
                if (form.firedLog.Count != 2 || form.alerts.Any(a => a.Code == "BTC") || form.displayed["BTCUSDT"].Price != 68150m)
                    throw new InvalidOperationException("Spike alert validation failed.");
                using (var popup = new AlertToast())
                {
                    popup.Add("first");
                    popup.Add("second");
                    popup.ShowNear(form.Bounds);
                    Application.DoEvents();
                    if (!popup.Visible || !popup.Lines.StartsWith("second") || popup.Height < 40)
                        throw new InvalidOperationException("Alert popup validation failed.");
                    using (var shot = new Bitmap(popup.Width, popup.Height))
                    {
                        popup.DrawToBitmap(shot, new Rectangle(Point.Empty, popup.Size));
                        shot.Save(Path.Combine(directory, "alert-popup.png"));
                    }
                }
                var entries = ParseCatalog("{\"symbols\":[" + TestContract("HYPE", "COIN", "PERPETUAL", "TRADING") + "," +
                    TestContract("ABCD", "EQUITY", "TRADIFI_PERPETUAL", "TRADING") + "," +
                    TestContract("HKD", "HK_EQUITY", "TRADIFI_PERPETUAL", "TRADING") + "," +
                    TestContract("DELISTED", "COIN", "PERPETUAL", "SETTLING") + "]}");
                if (entries.Count != 2 || !entries.Any(i => i.Code == "ABCD" && i.Category == "美股／ETF") ||
                    entries.First(i => i.Code == "HYPE").Decimals != 3 || entries.First(i => i.Code == "ABCD").Decimals != -1)
                    throw new InvalidOperationException("Exchange catalog validation failed.");
                form.Close();
            }
            File.WriteAllText(Path.Combine(directory, "self-test.txt"), "PASS: latest trade quotes, price highlight, stale state, compact layout, resizing, layered transparency, hit testing, catalog and watchlist.");
        }

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);

        static string TestContract(string code, string kind, string type, string status)
        {
            if (code == "HYPE")
                return new JavaScriptSerializer().Serialize(new { symbol = code + "USDT", baseAsset = code,
                    quoteAsset = "USDT", underlyingType = kind, contractType = type, status = status,
                    filters = new[] { new { filterType = "PRICE_FILTER", tickSize = "0.0010" } } });
            return new JavaScriptSerializer().Serialize(new { symbol = code + "USDT", baseAsset = code,
                quoteAsset = "USDT", underlyingType = kind, contractType = type, status = status });
        }

        // Saves the panel composited over a light and a dark backdrop, to judge legibility on both.
        void RenderTest(string directory, string name)
        {
            using (var panel = Snapshot())
            using (var canvas = new Bitmap(panel.Width * 2 + Px(48), panel.Height + Px(32)))
            using (var graphics = Graphics.FromImage(canvas))
            {
                int half = canvas.Width / 2;
                using (var light = new LinearGradientBrush(new Rectangle(0, 0, half, canvas.Height),
                    Color.FromArgb(244, 246, 250), Color.FromArgb(150, 186, 226), 35f))
                    graphics.FillRectangle(light, 0, 0, half, canvas.Height);
                using (var dark = new LinearGradientBrush(new Rectangle(half, 0, canvas.Width - half, canvas.Height),
                    Color.FromArgb(62, 96, 140), Color.FromArgb(14, 16, 22), 35f))
                    graphics.FillRectangle(dark, half, 0, canvas.Width - half, canvas.Height);
                graphics.DrawImage(panel, Px(16), Px(16));
                graphics.DrawImage(panel, half + Px(8), Px(16));
                canvas.Save(Path.Combine(directory, name));
            }
        }
    }

    sealed class DarkMenuColors : ProfessionalColorTable
    {
        static readonly Color Back = Color.FromArgb(27, 32, 41);
        static readonly Color Hot = Color.FromArgb(46, 56, 70);
        static readonly Color Rule = Color.FromArgb(52, 61, 75);
        public override Color ToolStripDropDownBackground { get { return Back; } }
        public override Color ImageMarginGradientBegin { get { return Back; } }
        public override Color ImageMarginGradientMiddle { get { return Back; } }
        public override Color ImageMarginGradientEnd { get { return Back; } }
        public override Color MenuBorder { get { return Rule; } }
        public override Color MenuItemBorder { get { return Hot; } }
        public override Color MenuItemSelected { get { return Hot; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hot; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hot; } }
        public override Color MenuItemPressedGradientBegin { get { return Hot; } }
        public override Color MenuItemPressedGradientMiddle { get { return Hot; } }
        public override Color MenuItemPressedGradientEnd { get { return Hot; } }
        public override Color SeparatorDark { get { return Rule; } }
        public override Color SeparatorLight { get { return Back; } }
        public override Color CheckBackground { get { return Back; } }
        public override Color CheckSelectedBackground { get { return Hot; } }
        public override Color CheckPressedBackground { get { return Hot; } }
    }

    sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Widget.Main : Widget.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Widget.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            var graphics = e.Graphics;
            var mode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Widget.Green, Math.Max(1.6f, r.Height / 8f))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                graphics.DrawLines(pen, new[] {
                    new PointF(r.Left + r.Width * .2f, r.Top + r.Height * .52f),
                    new PointF(r.Left + r.Width * .42f, r.Top + r.Height * .74f),
                    new PointF(r.Left + r.Width * .8f, r.Top + r.Height * .28f) });
            graphics.SmoothingMode = mode;
        }
    }

    // Topmost popup listing triggered alerts; stays until the user clicks "知道了".
    sealed class AlertToast : Form
    {
        readonly Label body = new Label();
        readonly List<string> lines = new List<string>();
        readonly TableLayoutPanel layout;

        public AlertToast()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(30, 36, 46);
            ForeColor = Widget.Main;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            Padding = new Padding(14, 12, 14, 12);
            AutoScaleMode = AutoScaleMode.Font;
            Text = "价格提醒";

            layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = BackColor };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = "🔔 价格提醒", AutoSize = true, ForeColor = Color.FromArgb(246, 196, 96),
                Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) };
            var dismiss = new Button { Text = "知道了", AutoSize = true, FlatStyle = FlatStyle.Flat, TabStop = false,
                ForeColor = Widget.Main, BackColor = Color.FromArgb(52, 62, 78), Cursor = Cursors.Hand };
            dismiss.FlatAppearance.BorderColor = Color.FromArgb(80, 92, 110);
            dismiss.Click += delegate { lines.Clear(); Hide(); AppendDismissed(); };
            body.AutoSize = true;
            body.MaximumSize = new Size(360, 0);
            body.Margin = new Padding(0, 4, 0, 0);
            layout.Controls.Add(title, 0, 0);
            layout.Controls.Add(dismiss, 1, 0);
            layout.Controls.Add(body, 0, 1);
            layout.SetColumnSpan(body, 2);
            Controls.Add(layout);
            Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var edge = new Pen(Color.FromArgb(246, 196, 96), 2)) e.Graphics.DrawRectangle(edge, 1, 1, Width - 2, Height - 2);
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // Topmost tool window that never takes keyboard focus (WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
        // so typing elsewhere can never "press" 知道了 by accident; it closes only by mouse click.
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x00000008 | 0x00000080 | 0x08000000; return p; }
        }

        public event EventHandler Dismissed;
        void AppendDismissed() { var handler = Dismissed; if (handler != null) handler(this, EventArgs.Empty); }

        public string Lines { get { return body.Text; } }

        public void Add(string line)
        {
            lines.Insert(0, line);
            if (lines.Count > 6) lines.RemoveAt(lines.Count - 1);
            body.Text = string.Join("\r\n", lines);
        }

        public void ShowNear(Rectangle panel)
        {
            var need = layout.GetPreferredSize(new Size(372, 0));
            ClientSize = new Size(Math.Max(300, Math.Min(420, need.Width + Padding.Horizontal)), need.Height + Padding.Vertical);
            Rectangle area = Screen.FromRectangle(panel).WorkingArea;
            int x = Math.Max(area.Left, Math.Min(panel.Right - Width, area.Right - Width));
            int y = panel.Bottom + 8 + Height <= area.Bottom ? panel.Bottom + 8 : Math.Max(area.Top, panel.Top - Height - 8);
            Location = new Point(x, y);
            if (!Visible) Show();
            BringToFront();
            Invalidate();
        }
    }

    sealed class AlertDialog : Form
    {
        sealed class Choice
        {
            public readonly Instrument Item;
            public Choice(Instrument item) { Item = item; }
            public override string ToString() { return Item.Code + "  ·  " + Item.Name; }
        }

        readonly ComboBox symbol = new ComboBox();
        readonly TextBox price = new TextBox();
        readonly Label hint = new Label();
        readonly ListView list = new ListView();
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly List<Instrument> instruments;
        readonly List<PriceAlert> alerts;
        readonly Func<string, Quote> quoteOf;
        readonly Func<Instrument, int> decimalsOf;

        public AlertDialog(List<Instrument> instruments, List<PriceAlert> alerts, Func<string, Quote> quoteOf,
            Func<Instrument, int> decimalsOf, string preset)
        {
            this.instruments = instruments;
            this.alerts = alerts;
            this.quoteOf = quoteOf;
            this.decimalsOf = decimalsOf;
            Text = "价格提醒";
            Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(560, 420);
            MinimumSize = new Size(460, 340);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(body);

            var entry = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
            entry.Controls.Add(new Label { Text = "品种", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
            symbol.DropDownStyle = ComboBoxStyle.DropDownList;
            symbol.Width = 190;
            symbol.AccessibleName = "提醒品种";
            foreach (var item in instruments) symbol.Items.Add(new Choice(item));
            entry.Controls.Add(symbol);
            entry.Controls.Add(new Label { Text = "提醒价格", AutoSize = true, Margin = new Padding(12, 7, 4, 0) });
            price.Width = 130;
            price.AccessibleName = "提醒价格 (USDT)";
            entry.Controls.Add(price);
            var add = new Button { Text = "添加提醒", AutoSize = true, Margin = new Padding(10, 2, 0, 0) };
            entry.Controls.Add(add);
            body.Controls.Add(entry, 0, 0);

            hint.AutoSize = true;
            hint.ForeColor = SystemColors.GrayText;
            hint.Margin = new Padding(3, 6, 3, 8);
            body.Controls.Add(hint, 0, 1);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add("品种", 130);
            list.Columns.Add("提醒条件", 170);
            list.Columns.Add("当前价", 120);
            list.Columns.Add("状态", 90);
            body.Controls.Add(list, 0, 2);

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var remove = new Button { Text = "删除所选", AutoSize = true };
            var done = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK };
            var note = new Label { Text = "价格触达后会弹出通知并响铃，每条提醒只触发一次。", AutoSize = true,
                ForeColor = SystemColors.GrayText, Margin = new Padding(10, 8, 3, 0) };
            actions.Controls.Add(remove);
            actions.Controls.Add(done);
            actions.Controls.Add(note);
            body.Controls.Add(actions, 0, 3);
            AcceptButton = add;
            CancelButton = done;

            symbol.SelectedIndexChanged += delegate { PrefillPrice(); UpdateHint(); };
            price.TextChanged += delegate { UpdateHint(); };
            add.Click += delegate
            {
                string error = AddAlert();
                if (error != null) MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            remove.Click += delegate
            {
                foreach (ListViewItem row in list.SelectedItems) alerts.Remove((PriceAlert)row.Tag);
                Fill();
            };
            list.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete) remove.PerformClick(); };
            clock.Tick += delegate { Fill(); UpdateHint(); };
            clock.Start();

            int index = instruments.FindIndex(i => i.Code == preset);
            if (symbol.Items.Count > 0) symbol.SelectedIndex = Math.Max(0, index);
            Fill();
            ActiveControl = price;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) clock.Dispose();
            base.Dispose(disposing);
        }

        Instrument Current { get { var choice = symbol.SelectedItem as Choice; return choice == null ? null : choice.Item; } }

        void PrefillPrice()
        {
            var item = Current;
            var quote = item == null ? null : quoteOf(item.Symbol);
            price.Text = quote == null ? "" : Widget.PriceText(quote.Price, decimalsOf(item)).Replace(",", "");
            price.SelectAll();
        }

        void UpdateHint()
        {
            var item = Current;
            if (item == null) { hint.Text = "请先在“自选品种”中添加品种。"; return; }
            var quote = quoteOf(item.Symbol);
            if (quote == null) { hint.Text = item.Symbol + " 暂未收到价格。"; return; }
            string now = "当前价 " + Widget.PriceText(quote.Price, decimalsOf(item));
            decimal target;
            if (!PriceAlert.TryReadPrice(price.Text, out target) || target == quote.Price)
                hint.Text = now + "。输入高于当前价 = 涨到提醒，低于当前价 = 跌到提醒。";
            else
                hint.Text = now + " → 将在价格" + (target > quote.Price ? "涨到 ≥ " : "跌到 ≤ ") +
                    Widget.PriceText(target, decimalsOf(item)) + " 时提醒（" +
                    Widget.ChangeText((target - quote.Price) / quote.Price) + "）";
        }

        // Returns an error message, or null when the alert was added.
        internal string AddAlert()
        {
            var item = Current;
            if (item == null) return "请先在“自选品种”中添加品种。";
            decimal target;
            if (!PriceAlert.TryReadPrice(price.Text, out target)) return "请输入有效的提醒价格，例如 70000。";
            var quote = quoteOf(item.Symbol);
            if (quote == null) return item.Symbol + " 暂未收到价格，请稍后再试。";
            if (target == quote.Price) return "提醒价格不能等于当前价。";
            if (alerts.Count >= 100) return "提醒数量已达上限（100 条）。";
            bool above = target > quote.Price;
            if (alerts.Any(a => a.Code == item.Code && a.Above == above && a.Target == target)) return "已经有相同的提醒了。";
            alerts.Add(new PriceAlert(item.Code, above, target));
            Fill();
            price.Text = "";
            UpdateHint();
            return null;
        }

        internal void SetForTest(string code, string value)
        {
            symbol.SelectedIndex = instruments.FindIndex(i => i.Code == code);
            price.Text = value;
        }

        void Fill()
        {
            var keep = new HashSet<PriceAlert>(list.SelectedItems.Cast<ListViewItem>().Select(r => (PriceAlert)r.Tag));
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var alert in alerts)
            {
                var item = instruments.FirstOrDefault(i => i.Code == alert.Code);
                int decimals = item != null ? decimalsOf(item) : -1;
                var quote = quoteOf(alert.Symbol);
                var row = new ListViewItem(new[] { alert.Symbol, alert.Describe(decimals),
                    quote == null ? "—" : Widget.PriceText(quote.Price, decimals),
                    item == null ? "不在自选中" : "等待触发" }) { Tag = alert, Selected = keep.Contains(alert) };
                if (item == null) row.ForeColor = SystemColors.GrayText;
                list.Items.Add(row);
            }
            list.EndUpdate();
        }
    }

    // Watchlist editor: search and tick on the left, the current selection on the right for quick removal.
    sealed class WatchDialog : Form
    {
        readonly TextBox search = new TextBox();
        readonly ComboBox filter = new ComboBox();
        readonly ListView entries = new ListView();
        readonly ListView picked = new ListView();
        readonly Label status = new Label();
        readonly Label pickedTitle = new Label();
        readonly ToolTip tips = new ToolTip();
        readonly HashSet<string> choices;
        List<Instrument> catalog;
        string source;
        bool filling;

        public IEnumerable<string> Selection { get { return choices; } }

        public WatchDialog(List<Instrument> catalog, IEnumerable<string> selected, string source)
        {
            choices = new HashSet<string>(selected, StringComparer.Ordinal);
            Text = "自选品种";
            Font = new Font("Microsoft YaHei UI", 9);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(860, 540);
            MinimumSize = new Size(680, 420);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(12) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(body);

            // Left: search + category filter + full catalog with checkboxes.
            var finder = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            finder.Controls.Add(new Label { Text = "搜索", AutoSize = true, Margin = new Padding(3, 7, 4, 0) });
            search.Width = 220;
            search.AccessibleName = "搜索合约代码或名称";
            finder.Controls.Add(search);
            filter.DropDownStyle = ComboBoxStyle.DropDownList;
            filter.AccessibleName = "品种类别";
            filter.Width = 120;
            filter.Margin = new Padding(8, 3, 3, 3);
            filter.Items.AddRange(new object[] { "全部品种", "加密货币", "贵金属", "美股／ETF", "已自选" });
            filter.SelectedIndex = 0;
            finder.Controls.Add(filter);
            body.Controls.Add(finder, 0, 0);

            entries.Dock = DockStyle.Fill;
            entries.CheckBoxes = true;
            entries.View = View.Details;
            entries.FullRowSelect = true;
            entries.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            entries.Columns.Add("合约代码", 135);
            entries.Columns.Add("名称", 150);
            entries.Columns.Add("类别", 90);
            entries.AccessibleName = "全部合约，勾选加入自选";
            body.Controls.Add(entries, 0, 1);

            // Right: what is selected now, with remove actions.
            pickedTitle.AutoSize = true;
            pickedTitle.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            pickedTitle.Margin = new Padding(6, 7, 3, 4);
            body.Controls.Add(pickedTitle, 1, 0);

            picked.Dock = DockStyle.Fill;
            picked.View = View.Details;
            picked.FullRowSelect = true;
            picked.MultiSelect = true;
            picked.HideSelection = false;
            picked.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            picked.Columns.Add("合约代码", 125);
            picked.Columns.Add("名称", 150);
            picked.AccessibleName = "已选品种";
            picked.Margin = new Padding(6, 3, 3, 3);
            body.Controls.Add(picked, 1, 1);

            status.AutoSize = true;
            status.ForeColor = SystemColors.GrayText;
            status.Margin = new Padding(3, 8, 3, 0);
            body.Controls.Add(status, 0, 2);

            var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(6, 2, 0, 0) };
            var remove = new Button { Text = "移除所选", AutoSize = true };
            var clear = new Button { Text = "全部清空", AutoSize = true };
            tools.Controls.Add(remove);
            tools.Controls.Add(clear);
            tips.SetToolTip(picked, "双击或按 Delete 键移除");
            tools.Controls.Add(new Label { Text = "双击也可移除", AutoSize = true,
                ForeColor = SystemColors.GrayText, Margin = new Padding(6, 8, 0, 0) });
            body.Controls.Add(tools, 1, 2);

            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
            var apply = new Button { Text = "应用", AutoSize = true };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            actions.Controls.Add(apply);
            actions.Controls.Add(cancel);
            body.Controls.Add(actions, 0, 3);
            body.SetColumnSpan(actions, 2);
            CancelButton = cancel;
            apply.Click += delegate
            {
                if (choices.Count == 0)
                {
                    MessageBox.Show(this, "请至少选择一个品种。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };

            entries.ItemChecked += delegate(object sender, ItemCheckedEventArgs e)
            {
                if (filling) return;
                string code = (string)e.Item.Tag;
                if (e.Item.Checked) choices.Add(code);
                else choices.Remove(code);
                RefreshPicked();
            };
            remove.Click += delegate { RemoveCodes(picked.SelectedItems.Cast<ListViewItem>().Select(r => (string)r.Tag).ToList()); };
            clear.Click += delegate
            {
                if (choices.Count > 0 && MessageBox.Show(this, "确定清空全部 " + choices.Count + " 个已选品种吗？", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    RemoveCodes(choices.ToList());
            };
            picked.DoubleClick += delegate { RemoveCodes(picked.SelectedItems.Cast<ListViewItem>().Select(r => (string)r.Tag).ToList()); };
            picked.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Delete && e.KeyCode != Keys.Back) return;
                remove.PerformClick();
                e.Handled = true;
            };
            search.TextChanged += delegate { Fill(); };
            filter.SelectedIndexChanged += delegate { Fill(); };
            ActiveControl = search;
            SetCatalog(catalog, source);
        }

        public void SetCatalog(List<Instrument> contracts, string statusText)
        {
            catalog = contracts;
            source = statusText;
            Fill();
        }

        IEnumerable<Instrument> AllInstruments()
        {
            var known = new HashSet<string>(catalog.Select(item => item.Code));
            return catalog.Concat(choices.Where(code => !known.Contains(code)).OrderBy(code => code, StringComparer.Ordinal)
                .Select(code => new Instrument(code, code, category:"已自选")));
        }

        void Fill()
        {
            string query = search.Text.Trim();
            string category = (string)filter.SelectedItem;
            filling = true;
            entries.BeginUpdate();
            try
            {
                entries.Items.Clear();
                foreach (var item in AllInstruments())
                {
                    if (category == "已自选" ? !choices.Contains(item.Code) :
                        category != "全部品种" && category != item.Category) continue;
                    if (item.Symbol.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                        item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var row = new ListViewItem(new[] { item.Symbol, item.Name, item.Category })
                    { Tag = item.Code, Checked = choices.Contains(item.Code) };
                    entries.Items.Add(row);
                }
            }
            finally
            {
                entries.EndUpdate();
                filling = false;
            }
            RefreshPicked();
        }

        void RemoveCodes(ICollection<string> codes)
        {
            if (codes.Count == 0) return;
            choices.ExceptWith(codes);
            filling = true;
            try
            {
                foreach (ListViewItem row in entries.Items)
                    if (codes.Contains((string)row.Tag)) row.Checked = false;
            }
            finally { filling = false; }
            if ((string)filter.SelectedItem == "已自选") Fill();
            else RefreshPicked();
        }

        void RefreshPicked()
        {
            picked.BeginUpdate();
            try
            {
                picked.Items.Clear();
                foreach (var item in AllInstruments().Where(i => choices.Contains(i.Code)))
                    picked.Items.Add(new ListViewItem(new[] { item.Symbol, item.Name }) { Tag = item.Code });
            }
            finally { picked.EndUpdate(); }
            pickedTitle.Text = "已选品种（" + choices.Count + "）";
            status.Text = source + "  ｜  勾选左侧加入自选";
        }

        internal int PickedCount { get { return picked.Items.Count; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        internal void ToggleForTest(string code, string query = null)
        {
            if (query != null) search.Text = query;
            foreach (ListViewItem row in entries.Items)
            {
                if (!Equals(row.Tag, code)) continue;
                row.Checked = !row.Checked;
                return;
            }
            throw new InvalidOperationException("Missing watchlist entry.");
        }

        internal void RemoveForTest(string code)
        {
            foreach (ListViewItem row in picked.Items)
                row.Selected = Equals(row.Tag, code);
            RemoveCodes(picked.SelectedItems.Cast<ListViewItem>().Select(r => (string)r.Tag).ToList());
        }

        internal void RenderForTest(string path)
        {
            using (var shot = new Bitmap(Width, Height))
            {
                DrawToBitmap(shot, new Rectangle(Point.Empty, Size));
                shot.Save(path);
            }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

        static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext((IntPtr)(-4))) return; // PER_MONITOR_AWARE_V2 (Windows 10 1703+)
            }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Apply modern Windows TLS defaults even when built with the in-box Framework compiler.
            AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
            AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
            EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--self-test")
            {
                try
                {
                    if (ServicePointManager.SecurityProtocol != SecurityProtocolType.SystemDefault ||
                        ServicePointManager.ServerCertificateValidationCallback != null)
                        throw new InvalidOperationException("Windows TLS defaults and certificate validation are required.");
                    Widget.Test(args[1]);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(args[1], "self-test.txt"), ex.ToString());
                    Environment.ExitCode = 1;
                }
                return;
            }
            if (args.Length == 2 && args[0] == "--check-live")
            {
                CheckLive(args[1]).GetAwaiter().GetResult();
                return;
            }
            bool first;
            using (var single = new Mutex(true, "Local\\MarketFloat.Widget", out first))
            {
                if (!first)
                {
                    // Already running: bring the existing panel forward instead of opening a second one.
                    IntPtr existing = FindWindow(null, "币安浮动行情");
                    if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); }
                    return;
                }
                Application.Run(new Widget(false));
                GC.KeepAlive(single);
            }
        }

        static async Task CheckLive(string path)
        {
            Instrument[] symbols = Widget.Universe.Where(x => x.Default || x.Code == "SNDK").ToArray();
            var feed = new MarketFeed(symbols);
            feed.WatchTrades(symbols.Select(x => x.Symbol));
            var seen = new Dictionary<string, decimal[]>();
            string details;
            using (var stop = new CancellationTokenSource())
            {
                var feeds = feed.Run(stop.Token);
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    foreach (var pair in feed.TakeExtremes())
                    {
                        decimal[] range;
                        if (!seen.TryGetValue(pair.Key, out range)) seen[pair.Key] = pair.Value;
                        else { range[0] = Math.Min(range[0], pair.Value[0]); range[1] = Math.Max(range[1], pair.Value[1]); }
                    }
                }
                for (int i = 0; i < 20; i++)
                {
                    if (symbols.All(x => feed.Get(x.Symbol) != null) && feed.Status == "币安推送已连接") break;
                    await Task.Delay(1000).ConfigureAwait(false);
                }
                details = feed.Details;
                stop.Cancel();
                await feeds.ConfigureAwait(false);
            }
            File.WriteAllLines(path, new[] { details }.Concat(symbols.Select(x =>
            {
                var quote = feed.Get(x.Symbol);
                decimal[] range;
                string trades = seen.TryGetValue(x.Symbol, out range)
                    ? " | trades 12s low " + Widget.PriceText(range[0]) + " high " + Widget.PriceText(range[1]) : " | no trades in 12s";
                return x.Symbol + " " + (quote == null ? "no response" :
                    Widget.PriceText(quote.Price) + " 24h " + Widget.ChangeText(quote.Change)) + trades;
            })));
        }
    }
}
