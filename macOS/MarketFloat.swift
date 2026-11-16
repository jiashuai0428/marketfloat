// 币安浮动行情 · macOS
// Floating Binance USDⓈ-M perpetual ticker. Single-file AppKit app; build with ./build.sh.

import Cocoa
import UserNotifications

// MARK: - Model

struct Instrument {
    let code: String
    let name: String
    let category: String
    let metal: Bool
    let isDefault: Bool
    var decimals: Int = -1   // from tickSize; -1 when unknown
    var symbol: String { return code + "USDT" }
}

let universe: [Instrument] = [
    Instrument(code: "BTC", name: "比特币", category: "加密货币", metal: false, isDefault: true),
    Instrument(code: "ETH", name: "以太坊", category: "加密货币", metal: false, isDefault: true),
    Instrument(code: "SOL", name: "Solana", category: "加密货币", metal: false, isDefault: true),
    Instrument(code: "BNB", name: "BNB", category: "加密货币", metal: false, isDefault: false),
    Instrument(code: "XRP", name: "XRP", category: "加密货币", metal: false, isDefault: false),
    Instrument(code: "DOGE", name: "Dogecoin", category: "加密货币", metal: false, isDefault: false),
    Instrument(code: "ADA", name: "Cardano", category: "加密货币", metal: false, isDefault: false),
    Instrument(code: "AVAX", name: "Avalanche", category: "加密货币", metal: false, isDefault: false),
    Instrument(code: "SUI", name: "Sui", category: "加密货币", metal: false, isDefault: false),
    Instrument(code: "LINK", name: "Chainlink", category: "加密货币", metal: false, isDefault: false),
    Instrument(code: "XAU", name: "黄金", category: "贵金属", metal: true, isDefault: true),
    Instrument(code: "XAG", name: "白银", category: "贵金属", metal: true, isDefault: true),
    Instrument(code: "XPT", name: "铂金", category: "贵金属", metal: true, isDefault: false),
    Instrument(code: "XPD", name: "钯金", category: "贵金属", metal: true, isDefault: false),
    Instrument(code: "TSLA", name: "特斯拉", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "NVDA", name: "英伟达", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "MSTR", name: "Strategy", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "AAPL", name: "苹果", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "SNDK", name: "闪迪 Sandisk", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "MU", name: "美光 Micron", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "SPY", name: "标普 500 ETF", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "QQQ", name: "纳斯达克 100 ETF", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "AMZN", name: "亚马逊", category: "美股／ETF", metal: false, isDefault: false),
    Instrument(code: "GOOGL", name: "Alphabet", category: "美股／ETF", metal: false, isDefault: false),
]

struct Quote {
    let price: Decimal
    let open: Decimal
    let time: Date
    let scale: Int
    /// 24h change as a fraction (0.0231 = +2.31%), nil while the 24h open is unknown.
    var change: Double? {
        guard open > 0 else { return nil }
        return NSDecimalNumber(decimal: (price - open) / open).doubleValue
    }
}

enum FeedSource { case price, day, stream }

/// One-shot price alert: fires when the live price reaches `target` from below (`above`) or from above.
struct PriceAlert: Equatable {
    let code: String
    let above: Bool
    let target: Decimal

    var symbol: String { return code + "USDT" }

    func hit(_ price: Decimal) -> Bool { return above ? price >= target : price <= target }

    func describe(_ decimals: Int) -> String {
        return (above ? "涨到 ≥ " : "跌到 ≤ ") + Format.price(target, decimals: decimals)
    }

    var serialized: String { return code + (above ? ">" : "<") + "\(target)" }

    static func parse(_ text: String) -> PriceAlert? {
        guard let at = text.firstIndex(where: { $0 == ">" || $0 == "<" }), at != text.startIndex else { return nil }
        let code = String(text[..<at])
        guard validCode(code), let target = Format.decimal(String(text[text.index(after: at)...])) else { return nil }
        return PriceAlert(code: code, above: text[at] == ">", target: target)
    }

    /// Accepts "70000", "70,000.5" or " 0.1234 ".
    static func readPrice(_ text: String) -> Decimal? {
        let cleaned = text.replacingOccurrences(of: ",", with: "").replacingOccurrences(of: "，", with: "")
            .trimmingCharacters(in: .whitespaces)
        return Format.decimal(cleaned)
    }
}

func validCode(_ code: String) -> Bool {
    return !code.isEmpty && code.count <= 40 && code.allSatisfy { $0.isLetter || $0.isNumber || $0 == "_" }
}

// MARK: - Formatting

enum Format {
    private static var formatters: [Int: NumberFormatter] = [:]

    static func price(_ value: Decimal, decimals: Int) -> String {
        let key = decimals < 0 ? -1 : min(10, decimals)
        let formatter: NumberFormatter
        if let cached = formatters[key] { formatter = cached } else {
            formatter = NumberFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.numberStyle = .decimal
            formatter.usesGroupingSeparator = true
            formatter.groupingSeparator = ","
            formatter.groupingSize = 3
            formatter.decimalSeparator = "."
            formatter.roundingMode = .halfUp
            formatter.minimumFractionDigits = key < 0 ? 2 : key
            formatter.maximumFractionDigits = key < 0 ? 10 : key
            formatters[key] = formatter
        }
        return formatter.string(from: NSDecimalNumber(decimal: value)) ?? "\(value)"
    }

    static func change(_ value: Double?) -> String {
        guard let value = value else { return "" }
        let percent = (value * 100 * 100).rounded() / 100
        if percent == 0 { return "0.00%" }
        return (percent > 0 ? "+" : "") + String(format: "%.2f%%", percent)
    }

    static func decimal(_ text: String) -> Decimal? {
        guard !text.isEmpty, text.allSatisfy({ $0.isASCII && ($0.isNumber || $0 == ".") }),
              text.filter({ $0 == "." }).count <= 1,
              let value = Decimal(string: text, locale: Locale(identifier: "en_US_POSIX")), value > 0 else { return nil }
        return value
    }

    static func scale(_ text: String) -> Int {
        guard let dot = text.firstIndex(of: ".") else { return 0 }
        return text.distance(from: dot, to: text.endIndex) - 1
    }

    /// Decimals actually used by a price string, ignoring zero padding ("70.5500" -> 2).
    static func significantScale(_ text: String) -> Int {
        var trimmed = text
        while trimmed.contains(".") && trimmed.hasSuffix("0") { trimmed.removeLast() }
        return scale(trimmed)
    }
}

// MARK: - Catalog

enum Catalog {
    static func parse(_ data: Data) -> [Instrument]? {
        guard let object = try? JSONSerialization.jsonObject(with: data),
              let root = object as? [String: Any],
              let rows = root["symbols"] as? [[String: Any]] else { return nil }
        var result: [String: Instrument] = [:]
        for item in rows {
            let type = item["contractType"] as? String ?? ""
            guard item["status"] as? String == "TRADING", item["quoteAsset"] as? String == "USDT",
                  type == "PERPETUAL" || type == "TRADIFI_PERPETUAL",
                  let symbol = item["symbol"] as? String, symbol.hasSuffix("USDT") else { continue }
            let code = String(symbol.dropLast(4))
            guard validCode(code) else { continue }
            let kind = item["underlyingType"] as? String ?? ""
            let metal = kind == "COMMODITY" && ["XAU", "XAG", "XPT", "XPD"].contains(code)
            let category: String
            if metal { category = "贵金属" }
            else if kind == "EQUITY" { category = "美股／ETF" }
            else if kind == "COIN" { category = "加密货币" }
            else { continue }
            let known = universe.first { $0.code == code && $0.category == category }
            result[code] = Instrument(code: code, name: known?.name ?? code, category: category, metal: metal,
                                      isDefault: known?.isDefault ?? false, decimals: tickDecimals(item))
        }
        return result.isEmpty ? nil : Catalog.sorted(Array(result.values))
    }

    static func sorted(_ items: [Instrument]) -> [Instrument] {
        return items.sorted { $0.category != $1.category ? $0.category < $1.category : $0.code < $1.code }
    }

    static func tickDecimals(_ item: [String: Any]) -> Int {
        guard let filters = item["filters"] as? [[String: Any]] else { return -1 }
        for filter in filters where filter["filterType"] as? String == "PRICE_FILTER" {
            guard let text = filter["tickSize"] as? String, Format.decimal(text) != nil else { return -1 }
            var trimmed = text
            while trimmed.contains(".") && trimmed.hasSuffix("0") { trimmed.removeLast() }
            return min(10, Format.scale(trimmed))
        }
        return -1
    }

    static var cacheURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSTemporaryDirectory())
        return base.appendingPathComponent("MarketFloat", isDirectory: true).appendingPathComponent("contracts.json")
    }
}

// MARK: - Market feed
// WebSocket push (!miniTicker@arr) is primary; REST polling takes over when the stream is down,
// and the 24h ticker is fetched once a minute so quiet symbols still have an open price.

final class MarketFeed {
    static let streamURL = URL(string: "wss://fstream.binance.com/market/ws/!miniTicker@arr")!
    static let priceURL = URL(string: "https://fapi.binance.com/fapi/v2/ticker/price")!
    static let dayURL = URL(string: "https://fapi.binance.com/fapi/v1/ticker/24hr")!

    let symbols: Set<String>
    private let lock = NSLock()
    private var quotes: [String: Quote] = [:]
    private var streamStatusText = "正在连接", restStatusText = "等待币安接口"
    private var streamConnected = false, restConnected = false, blocked = false
    private var lastStream = Date.distantPast

    // Touched only on `queue`.
    private let queue = DispatchQueue(label: "MarketFloat.feed")
    private var session: URLSession!
    private var socket: URLSessionWebSocketTask?
    private var lastMessage = Date()
    private var stopped = false, streamReceived = false
    private var restFailures = 0, streamFailures = 0
    private var nextDay = Date.distantPast

    init(symbols: [String]) {
        self.symbols = Set(symbols)
        let operations = OperationQueue()
        operations.maxConcurrentOperationCount = 1
        operations.underlyingQueue = queue
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 8
        session = URLSession(configuration: configuration, delegate: nil, delegateQueue: operations)
    }

    private func locked<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    var streamFresh: Bool {
        return locked { streamConnected && Date().timeIntervalSince(lastStream) < 5 }
    }
    var connected: Bool {
        if streamFresh { return true }
        return locked { restConnected }
    }
    var status: String {
        if streamFresh { return "币安推送已连接" }
        return locked { restConnected ? "币安 REST 已连接" : blocked ? restStatusText : streamStatusText }
    }
    var details: String {
        return locked { "WebSocket 推送（主）：\(streamStatusText)\nREST 轮询（备用）：\(restStatusText)" }
    }

    func get(_ symbol: String) -> Quote? {
        return locked { quotes[symbol] }
    }

    func start() {
        guard !symbols.isEmpty else { return }
        queue.async {
            self.connectStream()
            self.poll()
        }
    }

    func stop() {
        queue.async {
            self.stopped = true
            self.socket?.cancel(with: .goingAway, reason: nil)
            self.socket = nil
            self.session.invalidateAndCancel()
        }
    }

    // MARK: Stream

    private func setStream(_ text: String, connected: Bool? = nil) {
        locked {
            streamStatusText = text
            if let connected = connected { streamConnected = connected }
        }
    }

    private func connectStream() {
        guard !stopped else { return }
        setStream("正在连接")
        let task = session.webSocketTask(with: MarketFeed.streamURL)
        task.maximumMessageSize = 4 * 1024 * 1024
        socket = task
        streamReceived = false
        lastMessage = Date()
        task.resume()
        receive(task)
        watchdog(task)
    }

    private func receive(_ task: URLSessionWebSocketTask) {
        task.receive { [weak self] result in
            guard let self = self, self.socket === task else { return }
            switch result {
            case .failure(let error):
                self.streamEnded(task, reason: "连接中断 · " + error.localizedDescription)
            case .success(let message):
                self.lastMessage = Date()
                var text: String?
                switch message {
                case .string(let value): text = value
                case .data(let data): text = String(data: data, encoding: .utf8)
                @unknown default: text = nil
                }
                if let text = text {
                    if self.accept(text, source: .stream) {
                        self.streamReceived = true
                        self.setStream("实时推送中", connected: true)
                    } else if self.streamReceived {
                        self.locked { self.lastStream = Date() } // alive; no selected symbol moved
                    }
                }
                self.receive(task)
            }
        }
    }

    private func watchdog(_ task: URLSessionWebSocketTask) {
        queue.asyncAfter(deadline: .now() + 5) { [weak self] in
            guard let self = self, self.socket === task else { return }
            if Date().timeIntervalSince(self.lastMessage) > 15 {
                self.streamEnded(task, reason: "连接或接收超时，正在重连")
            } else {
                self.watchdog(task)
            }
        }
    }

    private func streamEnded(_ task: URLSessionWebSocketTask, reason: String) {
        guard socket === task else { return }
        socket = nil
        task.cancel(with: .goingAway, reason: nil)
        setStream(stopped ? "已停止" : reason, connected: false)
        streamFailures = streamReceived ? 0 : min(streamFailures + 1, 5)
        guard !stopped else { return }
        queue.asyncAfter(deadline: .now() + Double(1 << streamFailures)) { [weak self] in
            self?.connectStream()
        }
    }

    // MARK: REST

    private func poll() {
        guard !stopped else { return }
        let day = Date() >= nextDay
        if !day && streamFresh {
            locked {
                restConnected = false
                restStatusText = "待命（推送正常）"
            }
            restFailures = 0
            queue.asyncAfter(deadline: .now() + 1) { [weak self] in self?.poll() }
            return
        }
        let started = Date()
        var request = URLRequest(url: day ? MarketFeed.dayURL : MarketFeed.priceURL)
        request.timeoutInterval = 8
        session.dataTask(with: request) { [weak self] data, response, error in
            guard let self = self, !self.stopped else { return }
            var ok = false
            var retryAfter: Double = 0
            var text: String
            if let http = response as? HTTPURLResponse {
                let code = http.statusCode
                self.locked { self.blocked = code == 451 || code == 403 }
                if code == 429 || code == 418 {
                    retryAfter = code == 418 ? 120 : 60
                    if let header = http.allHeaderFields["Retry-After"] as? String, let seconds = Double(header) {
                        retryAfter = max(retryAfter, seconds)
                    }
                }
                if (200..<300).contains(code), let data = data, let body = String(data: data, encoding: .utf8) {
                    ok = self.accept(body, source: day ? .day : .price)
                    text = ok ? "已收到报价" : "接口未返回所选合约"
                } else {
                    text = MarketFeed.httpStatus(code)
                }
            } else {
                text = "连接失败 · " + (error?.localizedDescription ?? "未知错误")
            }
            if ok && day { self.nextDay = Date().addingTimeInterval(60) }
            let fresh = self.streamFresh
            self.locked {
                self.restStatusText = text
                self.restConnected = ok && !fresh
            }
            self.restFailures = ok ? 0 : min(self.restFailures + 1, 5)
            var delay = Double(1 << self.restFailures)
            if ok { delay = max(0.05, delay - Date().timeIntervalSince(started)) }
            self.queue.asyncAfter(deadline: .now() + max(delay, retryAfter)) { [weak self] in self?.poll() }
        }.resume()
    }

    static func httpStatus(_ code: Int) -> String {
        switch code {
        case 451: return "币安限制当前网络地区 (HTTP 451)"
        case 403: return "币安拒绝访问 (HTTP 403)"
        case 429, 418: return "币安接口限流 (HTTP \(code))"
        default: return "币安接口错误 (HTTP \(code))"
        }
    }

    // MARK: Parsing

    @discardableResult
    func accept(_ message: String, source: FeedSource) -> Bool {
        guard let data = message.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data),
              let rows = object as? [[String: Any]] else { return false }
        let stream = source == .stream, day = source == .day
        let priceKey = stream ? "c" : day ? "lastPrice" : "price"
        let timeKey = stream ? "E" : day ? "closeTime" : "time"
        let openKey: String? = stream ? "o" : day ? "openPrice" : nil
        var received = false
        lock.lock()
        for row in rows {
            if stream {
                guard row["e"] as? String == "24hrMiniTicker" else { continue }
                if let market = row["st"] as? NSNumber, market.intValue != 1 { continue }
            }
            guard let code = row[stream ? "s" : "symbol"] as? String, symbols.contains(code),
                  let priceText = row[priceKey] as? String, let price = Format.decimal(priceText),
                  let stamp = row[timeKey] as? NSNumber else { continue }
            let millis = stamp.int64Value
            guard millis > 0 && millis <= 253_402_300_799_999 else { continue }
            let last = quotes[code]
            var open = last?.open ?? 0
            if let key = openKey, let openText = row[key] as? String, let value = Format.decimal(openText) {
                open = value
            }
            let next = Quote(price: price, open: open, time: Date(timeIntervalSince1970: Double(millis) / 1000),
                             scale: Format.significantScale(priceText))
            if let last = last, next.time < last.time {
                if open > 0 && last.open != open {
                    quotes[code] = Quote(price: last.price, open: open, time: last.time, scale: last.scale)
                }
            } else {
                quotes[code] = next
            }
            received = true
        }
        if received && stream { lastStream = Date() }
        lock.unlock()
        return received
    }
}

// MARK: - Palette

extension NSColor {
    static func rgb(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> NSColor {
        return NSColor(srgbRed: r / 255, green: g / 255, blue: b / 255, alpha: a)
    }
}

enum Palette {
    static let main = NSColor.rgb(236, 242, 250)
    static let muted = NSColor.rgb(150, 164, 184)
    static let green = NSColor.rgb(52, 216, 152)
    static let gold = NSColor.rgb(246, 196, 96)
    static let red = NSColor.rgb(255, 104, 124)
    static let glassTop = NSColor.rgb(28, 34, 45)
    static let glassBottom = NSColor.rgb(12, 15, 21)

    static func blend(_ from: NSColor, _ to: NSColor, _ amount: CGFloat) -> NSColor {
        return from.blended(withFraction: max(0, min(1, amount)), of: to) ?? to
    }
}

// MARK: - Window

final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { return true }
    override var canBecomeMain: Bool { return false }
}

enum Hit { case none, body, menu, compact, close, grip }

let opacityLevels = [100, 85, 70, 55, 40, 25, 0]
let defaultOpacity = 55

func opacityTitle(_ value: Int) -> String {
    if value >= 100 { return "不透明" }
    if value <= 0 { return "完全透明" }
    return "透明 \(100 - value)%" + (value == defaultOpacity ? "（推荐）" : "")
}

final class WidgetView: NSView {
    let panel: FloatingPanel
    private let blur = NSVisualEffectView()
    private var catalog: [Instrument]
    private var catalogState = "内置目录"
    private var selected: Set<String>
    private var feed: MarketFeed
    private var displayed: [String: Quote] = [:]
    private var direction: [String: NSColor] = [:]
    private var changedAt: [String: Date] = [:]
    private var scales: [String: Int] = [:]
    private var alerts: [PriceAlert] = []
    private var alertedAt: [String: Date] = [:]
    private let alertGlowSeconds: TimeInterval = 12
    private var feedStarted = Date()
    private var lastFrame = Date.distantPast
    private var timer: Timer?

    private var opacity = defaultOpacity
    private var compact = false
    private var pinned = true
    private var layoutWidths: [CGFloat] = [312, 174]
    private var layoutRows: [CGFloat] = [42, 24]
    private var firstRow = 0
    private var visibleRows = 1
    private var inside = false, resizing = false
    private var hover = Hit.none, pressed = Hit.none
    private var hoverRow = -1
    private var resizeStartMouse = NSPoint.zero
    private var resizeStartFrame = NSRect.zero
    private var ready = false

    private let defaults = UserDefaults.standard

    init() {
        var items = universe
        if let url = Bundle.main.url(forResource: "BinanceContracts", withExtension: "json"),
           let data = try? Data(contentsOf: url), let parsed = Catalog.parse(data) {
            items = parsed
            catalogState = "内置目录 · 2026-11-16"
        }
        if let data = try? Data(contentsOf: Catalog.cacheURL), let parsed = Catalog.parse(data) {
            items = parsed
            catalogState = "缓存目录"
        }
        catalog = items
        selected = Set(universe.filter { $0.isDefault }.map { $0.code })
        feed = MarketFeed(symbols: [])
        panel = FloatingPanel(contentRect: NSRect(x: 0, y: 0, width: 312, height: 280),
                              styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init(frame: NSRect(x: 0, y: 0, width: 312, height: 280))
        loadSettings()
        for code in selected where !catalog.contains(where: { $0.code == code }) {
            catalog.append(Instrument(code: code, name: code, category: "已自选", metal: false, isDefault: false))
        }

        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isMovableByWindowBackground = false
        panel.title = "币安浮动行情"
        panel.level = pinned ? .floating : .normal

        let container = NSView(frame: bounds)
        container.autoresizingMask = [.width, .height]
        blur.frame = container.bounds
        blur.autoresizingMask = [.width, .height]
        blur.material = .hudWindow
        blur.blendingMode = .behindWindow
        blur.state = .active
        container.addSubview(blur)
        frame = container.bounds
        autoresizingMask = [.width, .height]
        container.addSubview(self)
        panel.contentView = container

        ready = true
        applyOpacity()
        layoutWidget(keepTop: false)
        resetFeed()
        timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in self?.refreshQuotes() }
        RunLoop.main.add(timer!, forMode: .common)
        NotificationCenter.default.addObserver(self, selector: #selector(windowMoved),
                                               name: NSWindow.didMoveNotification, object: panel)
        refreshCatalog()
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func show() {
        panel.orderFrontRegardless()
    }

    override var isFlipped: Bool { return true }
    override var acceptsFirstResponder: Bool { return true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { return true }
    override var mouseDownCanMoveWindow: Bool { return false }
    override var needsPanelToBecomeKey: Bool { return true }

    // MARK: Layout

    private var mode: Int { return compact ? 1 : 0 }
    private var head: CGFloat { return compact ? 30 : 42 }
    private var lead: CGFloat { return compact ? 4 : 21 }
    private var foot: CGFloat { return compact ? 15 : 28 }
    private var designWidth: CGFloat { return compact ? 174 : 312 }
    private var rowTop: CGFloat { return head + lead }
    private var rowHeight: CGFloat {
        return max(1, floor((bounds.height - rowTop - foot) / CGFloat(max(1, visibleRows))))
    }
    private var visibleInstruments: [Instrument] { return catalog.filter { selected.contains($0.code) } }
    private var showNames: Bool { return !compact && rowHeight >= 32 && bounds.width >= 240 }

    private func layoutWidget(keepTop: Bool = true) {
        let screen = panel.screen ?? NSScreen.main
        let area = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let rows = CGFloat(min(selected.count, 14))
        let size = NSSize(width: min(area.width, layoutWidths[mode]),
                          height: min(area.height, (head + lead + foot + (layoutRows[mode] * rows).rounded())))
        var frame = panel.frame
        let top = keepTop ? frame.maxY : (defaultsTop() ?? area.maxY - 24)
        if !keepTop { frame.origin.x = defaultsLeft() ?? area.maxX - size.width - 24 }
        frame.size = size
        frame.origin.y = top - size.height
        frame.origin.x = max(area.minX, min(frame.origin.x, area.maxX - size.width))
        frame.origin.y = max(area.minY, min(frame.origin.y, area.maxY - size.height))
        panel.setFrame(frame, display: true)
        layoutChrome()
    }

    private func layoutChrome() {
        visibleRows = min(selected.count, max(1, Int((bounds.height - head - lead - foot) / 16)))
        scrollRows(0)
    }

    private func rememberSize() {
        layoutWidths[mode] = bounds.width.rounded()
        let rows = CGFloat(max(1, min(selected.count, 14)))
        layoutRows[mode] = max(14, (bounds.height - head - lead - foot) / rows)
    }

    private func scrollRows(_ delta: Int) {
        firstRow = max(0, min(firstRow + delta, selected.count - visibleRows))
        redraw()
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        if ready { layoutChrome() }
    }

    // MARK: State

    private func resetFeed() {
        feed.stop()
        feed = MarketFeed(symbols: visibleInstruments.map { $0.symbol })
        feedStarted = Date()
        feed.start()
    }

    private func refreshCatalog() {
        let task = URLSession.shared.dataTask(with: URL(string: "https://fapi.binance.com/fapi/v1/exchangeInfo")!) {
            [weak self] data, _, _ in
            guard let data = data, let parsed = Catalog.parse(data) else {
                DispatchQueue.main.async {
                    if self?.catalogState == "缓存目录" { self?.catalogState = "目录暂不可用 · 显示缓存品种" }
                }
                return
            }
            try? FileManager.default.createDirectory(at: Catalog.cacheURL.deletingLastPathComponent(),
                                                     withIntermediateDirectories: true)
            try? data.write(to: Catalog.cacheURL)
            DispatchQueue.main.async { self?.setCatalog(parsed, state: "已连接币安") }
        }
        task.resume()
    }

    private func setCatalog(_ entries: [Instrument], state: String) {
        var merged = entries
        let known = Set(entries.map { $0.code })
        merged.append(contentsOf: catalog.filter { selected.contains($0.code) && !known.contains($0.code) })
        catalog = Catalog.sorted(merged)
        catalogState = state
        redraw()
    }

    private func refreshQuotes() {
        let now = Date()
        var changed = false
        for item in visibleInstruments {
            guard let current = feed.get(item.symbol) else { continue }
            scales[item.symbol] = max(scales[item.symbol] ?? 0, current.scale)
            let last = displayed[item.symbol]
            if last == nil || last!.open != current.open { changed = true }
            if let last = last, last.price != current.price {
                direction[item.symbol] = current.price > last.price ? Palette.green : Palette.red
                changedAt[item.symbol] = now
                changed = true
            }
            displayed[item.symbol] = current
        }
        if checkAlerts(now) { changed = true }
        let animating = changedAt.values.contains { now.timeIntervalSince($0) < 2.6 } ||
            alertedAt.values.contains { now.timeIntervalSince($0) < alertGlowSeconds }
        if changed || animating || now.timeIntervalSince(lastFrame) >= 1 { redraw() }
    }

    // MARK: Price alerts

    /// Fires each alert once when a live price reaches its target, then removes it.
    private func checkAlerts(_ now: Date) -> Bool {
        guard !alerts.isEmpty, feed.connected else { return false }
        var fired = false
        for alert in alerts {
            guard let quote = displayed[alert.symbol], now.timeIntervalSince(quote.time) < 30, alert.hit(quote.price) else { continue }
            alerts.removeAll { $0 == alert }
            alertedAt[alert.symbol] = now
            fired = true
            notify(alert, price: quote.price)
        }
        if fired { saveSettings() }
        return fired
    }

    private func notify(_ alert: PriceAlert, price: Decimal) {
        let item = catalog.first { $0.code == alert.code }
        let places = item.map { decimals(for: $0) } ?? -1
        let body = alert.symbol + (alert.above ? " 已涨到 " : " 已跌到 ") + Format.price(price, decimals: places) +
            "\n提醒价：" + alert.describe(places)
        NSSound(named: "Glass")?.play()
        NSApp.requestUserAttention(.informationalRequest)
        let content = UNMutableNotificationContent()
        content.title = "价格提醒 · " + (item?.name ?? alert.code)
        content.body = body
        content.sound = .default
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request, withCompletionHandler: nil)
    }

    private func requestNotificationPermission() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    @objc private func openAlerts(_ sender: NSMenuItem?) {
        let preset = sender?.representedObject as? String
        requestNotificationPermission()
        let picker = AlertPicker(instruments: visibleInstruments, alerts: alerts, preset: preset,
                                 quote: { [weak self] symbol in self?.displayed[symbol] },
                                 decimals: { [weak self] item in self?.decimals(for: item) ?? -1 })
        alerts = picker.run()
        saveSettings()
        redraw()
    }

    private func drawBell(x: CGFloat, centerY: CGFloat, size: CGFloat, color: NSColor, halo: CGFloat) {
        let w = size * 0.82, h = size, top = centerY - h / 2
        NSGraphicsContext.saveGraphicsState()
        if halo > 0 {
            let shadow = NSShadow()
            shadow.shadowColor = NSColor.black.withAlphaComponent(halo)
            shadow.shadowBlurRadius = 2
            shadow.shadowOffset = .zero
            shadow.set()
        }
        color.setFill()
        NSBezierPath(roundedRect: NSRect(x: x + w * 0.14, y: top, width: w * 0.72, height: h * 0.7),
                     xRadius: w * 0.36, yRadius: w * 0.36).fill()
        let flare = NSBezierPath()
        flare.move(to: NSPoint(x: x + w * 0.14, y: top + h * 0.4))
        flare.line(to: NSPoint(x: x + w * 0.86, y: top + h * 0.4))
        flare.line(to: NSPoint(x: x + w, y: top + h * 0.76))
        flare.line(to: NSPoint(x: x, y: top + h * 0.76))
        flare.close()
        flare.fill()
        NSBezierPath(ovalIn: NSRect(x: x + w * 0.37, y: top + h * 0.79, width: w * 0.26, height: h * 0.21)).fill()
        NSGraphicsContext.restoreGraphicsState()
    }

    private func fresh(_ item: Instrument) -> Bool {
        guard feed.connected, let quote = displayed[item.symbol] else { return false }
        return Date().timeIntervalSince(quote.time) < 30
    }

    private func decimals(for item: Instrument) -> Int {
        if item.decimals >= 0 { return item.decimals }
        if let seen = scales[item.symbol] { return max(2, seen) }
        return -1
    }

    private func redraw() {
        lastFrame = Date()
        needsDisplay = true
    }

    private func applyOpacity() {
        blur.isHidden = opacity == 0 || opacity == 100
        blur.alphaValue = min(1, CGFloat(opacity) / 40)
        blur.maskImage = WidgetView.mask(radius: compact ? 8 : 12)
        panel.hasShadow = opacity >= 25
        redraw()
        panel.invalidateShadow()
    }

    private static func mask(radius: CGFloat) -> NSImage {
        let edge = radius * 2 + 1
        let image = NSImage(size: NSSize(width: edge, height: edge), flipped: false) { rect in
            NSColor.black.set()
            NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
            return true
        }
        image.capInsets = NSEdgeInsets(top: radius, left: radius, bottom: radius, right: radius)
        image.resizingMode = .stretch
        return image
    }

    // MARK: Settings

    private func defaultsTop() -> CGFloat? {
        return defaults.object(forKey: "top") == nil ? nil : CGFloat(defaults.double(forKey: "top"))
    }

    private func defaultsLeft() -> CGFloat? {
        guard defaults.object(forKey: "left") != nil else { return nil }
        let left = CGFloat(defaults.double(forKey: "left"))
        let top = defaultsTop() ?? 0
        let visible = NSScreen.screens.contains { $0.visibleFrame.insetBy(dx: -4, dy: -4).contains(NSPoint(x: left + 4, y: top - 4)) }
        return visible ? left : nil
    }

    private func loadSettings() {
        if let codes = defaults.array(forKey: "selected") as? [String] {
            let valid = codes.filter(validCode)
            if !valid.isEmpty { selected = Set(valid) }
        }
        if defaults.object(forKey: "compact") != nil { compact = defaults.bool(forKey: "compact") }
        if defaults.object(forKey: "pinned") != nil { pinned = defaults.bool(forKey: "pinned") }
        if defaults.object(forKey: "opacity") != nil { opacity = max(0, min(100, defaults.integer(forKey: "opacity"))) }
        if let widths = defaults.array(forKey: "widths") as? [Double], widths.count == 2 {
            layoutWidths = widths.map { CGFloat(max(123, min(4000, $0))) }
        }
        if let rows = defaults.array(forKey: "rows") as? [Double], rows.count == 2 {
            layoutRows = rows.map { CGFloat(max(14, min(2000, $0))) }
        }
        if defaultsLeft() == nil { defaults.removeObject(forKey: "top") }
        if let saved = defaults.array(forKey: "alerts") as? [String] {
            alerts = Array(saved.compactMap(PriceAlert.parse).prefix(100))
        }
    }

    private func saveSettings() {
        defaults.set(Array(selected).sorted(), forKey: "selected")
        defaults.set(compact, forKey: "compact")
        defaults.set(pinned, forKey: "pinned")
        defaults.set(opacity, forKey: "opacity")
        defaults.set(layoutWidths.map { Double($0) }, forKey: "widths")
        defaults.set(layoutRows.map { Double($0) }, forKey: "rows")
        defaults.set(Double(panel.frame.minX), forKey: "left")
        defaults.set(Double(panel.frame.maxY), forKey: "top")
        defaults.set(alerts.map { $0.serialized }, forKey: "alerts")
    }

    @objc private func windowMoved() {
        if ready && !resizing { saveSettings() }
    }

    // MARK: Actions

    @objc func toggleCompact() {
        compact.toggle()
        firstRow = 0
        applyOpacity()
        layoutWidget()
        saveSettings()
    }

    @objc private func togglePin() {
        pinned.toggle()
        panel.level = pinned ? .floating : .normal
        saveSettings()
    }

    @objc private func chooseOpacity(_ sender: NSMenuItem) {
        opacity = sender.tag
        applyOpacity()
        saveSettings()
    }

    @objc private func showDiagnostics() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "行情连接诊断"
        alert.informativeText = feed.details + "\n\n合约目录：\(catalogState) · \(catalog.count) 个品种"
        alert.runModal()
    }

    @objc private func pickInstruments() {
        refreshCatalog()
        let picker = WatchPicker(catalog: catalog, selected: selected, source: catalogState)
        guard let choice = picker.run() else { return }
        selected = choice
        for code in selected where !catalog.contains(where: { $0.code == code }) {
            catalog.append(Instrument(code: code, name: code, category: "已自选", metal: false, isDefault: false))
        }
        firstRow = 0
        resetFeed()
        layoutWidget()
        saveSettings()
    }

    @objc private func hideApp() { NSApp.hide(nil) }
    @objc private func quit() { NSApp.terminate(nil) }

    func shutdown() {
        saveSettings()
        timer?.invalidate()
        feed.stop()
    }

    private func buildMenu(row: Int = -1) -> NSMenu {
        let menu = NSMenu()
        let items = visibleInstruments
        if row >= 0 && firstRow + row < items.count {
            // Right-clicking a quote row offers an alert for that instrument first.
            let code = items[firstRow + row].code
            let quick = menu.addItem(withTitle: "为 \(code) 设置价格提醒…", action: #selector(openAlerts(_:)), keyEquivalent: "")
            quick.target = self
            quick.representedObject = code
            menu.addItem(.separator())
        }
        menu.addItem(withTitle: "自选品种…", action: #selector(pickInstruments), keyEquivalent: "").target = self
        let alertTitle = alerts.isEmpty ? "价格提醒…" : "价格提醒（\(alerts.count)）…"
        menu.addItem(withTitle: alertTitle, action: #selector(openAlerts(_:)), keyEquivalent: "").target = self
        menu.addItem(.separator())
        let pin = menu.addItem(withTitle: "窗口置顶", action: #selector(togglePin), keyEquivalent: "")
        pin.target = self
        pin.state = pinned ? .on : .off
        let fade = NSMenuItem(title: "背景透明度", action: nil, keyEquivalent: "")
        let levels = NSMenu()
        for level in opacityLevels {
            let item = levels.addItem(withTitle: opacityTitle(level), action: #selector(chooseOpacity(_:)), keyEquivalent: "")
            item.target = self
            item.tag = level
            item.state = level == opacity ? .on : .off
        }
        fade.submenu = levels
        menu.addItem(fade)
        menu.addItem(withTitle: compact ? "恢复普通模式" : "切换极简模式", action: #selector(toggleCompact), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "行情连接诊断…", action: #selector(showDiagnostics), keyEquivalent: "").target = self
        menu.addItem(withTitle: "隐藏（点程序坞图标恢复）", action: #selector(hideApp), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出", action: #selector(quit), keyEquivalent: "").target = self
        return menu
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        return buildMenu(row: hitAt(point) == .body ? row(at: point.y) : -1)
    }

    private func showMenu() {
        let anchor = buttonRect(2)
        buildMenu().popUp(positioning: nil, at: NSPoint(x: anchor.minX, y: anchor.maxY + 2), in: self)
    }

    // MARK: Mouse

    private let buttonHits: [Hit] = [.close, .compact, .menu]

    private func buttonRect(_ index: Int) -> NSRect {
        let size: CGFloat = compact ? 20 : 24
        return NSRect(x: bounds.width - (compact ? 4 : 7) - size * CGFloat(index + 1),
                      y: ((head - size) / 2).rounded(), width: size, height: size)
    }

    private var gripRect: NSRect { return NSRect(x: bounds.width - 16, y: bounds.height - 16, width: 16, height: 16) }

    private func hitAt(_ point: NSPoint) -> Hit {
        for (index, kind) in buttonHits.enumerated() where buttonRect(index).contains(point) { return kind }
        if gripRect.contains(point) { return .grip }
        return bounds.contains(point) ? .body : .none
    }

    private func row(at y: CGFloat) -> Int {
        let top = rowTop, height = rowHeight
        guard y >= top, y < top + height * CGFloat(visibleRows) else { return -1 }
        return Int((y - top) / height)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect],
                                       owner: self, userInfo: nil))
    }

    override func mouseEntered(with event: NSEvent) {
        inside = true
        redraw()
    }

    override func mouseExited(with event: NSEvent) {
        guard !resizing else { return }
        inside = false
        hover = .none
        hoverRow = -1
        toolTip = nil
        NSCursor.arrow.set()
        redraw()
    }

    override func mouseMoved(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let hit = hitAt(point)
        let line = hit == .body ? row(at: point.y) : -1
        if hit == .grip { NSCursor.crosshair.set() }
        else if hit == .body || hit == .none { NSCursor.arrow.set() }
        else { NSCursor.pointingHand.set() }
        if hit != hover || line != hoverRow || !inside {
            inside = true
            hover = hit
            hoverRow = line
            toolTip = tip(for: hit, row: line)
            redraw()
        }
    }

    private func tip(for hit: Hit, row: Int) -> String? {
        switch hit {
        case .menu: return "菜单：自选品种、透明度、置顶（右键同样可用）"
        case .compact: return compact ? "恢复普通模式 (F2)" : "切换极简模式 (F2)"
        case .close: return "退出"
        case .grip: return "拖动调整大小"
        case .body:
            guard row >= 0, !showNames else { return nil }
            let items = visibleInstruments
            guard firstRow + row < items.count else { return nil }
            let item = items[firstRow + row]
            let change = Format.change(displayed[item.symbol]?.change)
            return item.symbol + " · " + item.name + (change.isEmpty ? "" : " · 24h " + change)
        case .none: return nil
        }
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let hit = hitAt(point)
        switch hit {
        case .grip:
            resizing = true
            resizeStartMouse = NSEvent.mouseLocation
            resizeStartFrame = panel.frame
            redraw()
        case .body:
            if event.clickCount == 2 { toggleCompact(); return }
            panel.performDrag(with: event)
        case .none:
            break
        default:
            pressed = hit
            redraw()
        }
    }

    override func mouseDragged(with event: NSEvent) {
        guard resizing else { return }
        let mouse = NSEvent.mouseLocation
        let area = panel.screen?.visibleFrame ?? resizeStartFrame
        let minimum = NSSize(width: compact ? 123 : 171, height: head + lead + foot + 14)
        var frame = resizeStartFrame
        frame.size.width = max(minimum.width, min(area.maxX - frame.minX, resizeStartFrame.width + mouse.x - resizeStartMouse.x))
        frame.size.height = max(minimum.height, min(resizeStartFrame.maxY - area.minY,
                                                    resizeStartFrame.height - (mouse.y - resizeStartMouse.y)))
        frame.origin.y = resizeStartFrame.maxY - frame.height
        panel.setFrame(frame, display: true)
        rememberSize()
    }

    override func mouseUp(with event: NSEvent) {
        if resizing {
            resizing = false
            saveSettings()
            redraw()
            return
        }
        let target = pressed
        pressed = .none
        redraw()
        guard target != .none, hitAt(convert(event.locationInWindow, from: nil)) == target else { return }
        switch target {
        case .close: quit()
        case .compact: toggleCompact()
        case .menu: showMenu()
        default: break
        }
    }

    override func scrollWheel(with event: NSEvent) {
        let delta = event.scrollingDeltaY
        guard abs(delta) >= (event.hasPreciseScrollingDeltas ? 6 : 0.5) else { return }
        scrollRows(delta > 0 ? -1 : 1)
    }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 120: toggleCompact()                 // F2
        case 121: scrollRows(visibleRows)             // Page Down
        case 116: scrollRows(-visibleRows)            // Page Up
        case 125: scrollRows(1)                       // ↓
        case 126: scrollRows(-1)                      // ↑
        default: super.keyDown(with: event)
        }
    }

    // MARK: Drawing

    private var tintAlpha: CGFloat {
        var alpha = CGFloat(opacity) / 100
        if opacity > 0 && opacity < 100 { alpha *= 0.82 }   // blur already darkens the backdrop
        if inside || resizing || pressed != .none { alpha = max(alpha, 0.32) }
        return max(0.02, alpha)                               // never 0, so the whole panel stays clickable
    }

    private func scaled(_ font: NSFont, _ factor: CGFloat) -> NSFont {
        return NSFont(descriptor: font.fontDescriptor, size: max(6, font.pointSize * factor)) ?? font
    }

    @discardableResult
    private func write(_ text: String, _ font: NSFont, _ color: NSColor, left: CGFloat, top: CGFloat, right: CGFloat,
                       height: CGFloat, halo: CGFloat, alignRight: Bool = false, backdrop: NSColor? = nil) -> NSRect {
        guard !text.isEmpty, right - left >= 1, height >= 1 else { return .zero }
        var face = font
        var attributes: [NSAttributedString.Key: Any] = [.font: face, .foregroundColor: color]
        var size = (text as NSString).size(withAttributes: attributes)
        if size.width > right - left && size.width > 0 {
            face = scaled(font, (right - left) / size.width)
            attributes[.font] = face
            size = (text as NSString).size(withAttributes: attributes)
        }
        if halo > 0 {
            let shadow = NSShadow()
            shadow.shadowColor = NSColor.black.withAlphaComponent(halo)
            shadow.shadowBlurRadius = 2.5
            shadow.shadowOffset = .zero
            attributes[.shadow] = shadow
        }
        let x = alignRight ? right - size.width : left
        let y = top + height / 2 + face.capHeight / 2 - face.ascender
        if let backdrop = backdrop {
            backdrop.setFill()
            let pill = NSRect(x: x - 5, y: top + height / 2 - face.capHeight / 2 - 4, width: size.width + 10, height: face.capHeight + 8)
            NSBezierPath(roundedRect: pill, xRadius: 4, yRadius: 4).fill()
        }
        (text as NSString).draw(at: NSPoint(x: x, y: y), withAttributes: attributes)
        return NSRect(x: x, y: y, width: size.width, height: size.height)
    }

    private func drawIcon(_ kind: Hit, in rect: NSRect, color: NSColor) {
        let cx = rect.midX, cy = rect.midY, s = rect.width * 0.2
        let path = NSBezierPath()
        path.lineWidth = 1.5
        path.lineCapStyle = .round
        switch kind {
        case .menu:
            for i in -1...1 {
                path.move(to: NSPoint(x: cx - s, y: cy + CGFloat(i) * s * 0.75))
                path.line(to: NSPoint(x: cx + s, y: cy + CGFloat(i) * s * 0.75))
            }
        case .compact:
            path.move(to: NSPoint(x: cx - s, y: cy))
            path.line(to: NSPoint(x: cx + s, y: cy))
            if compact {
                path.move(to: NSPoint(x: cx, y: cy - s))
                path.line(to: NSPoint(x: cx, y: cy + s))
            }
        default:
            let k = s * 0.85
            path.move(to: NSPoint(x: cx - k, y: cy - k))
            path.line(to: NSPoint(x: cx + k, y: cy + k))
            path.move(to: NSPoint(x: cx - k, y: cy + k))
            path.line(to: NSPoint(x: cx + k, y: cy - k))
        }
        color.setStroke()
        path.stroke()
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.setFill()
        bounds.fill(using: .copy)

        let width = bounds.width, height = bounds.height
        let alpha = tintAlpha
        let blurOn = !blur.isHidden
        let halo: CGFloat = alpha < 0.4 && !blurOn ? 0.9 : alpha < 0.55 && !blurOn ? 0.5 : 0
        let count = selected.count
        let items = visibleInstruments
        let live = items.filter(fresh).count
        let now = Date()

        // Glass tint and edge.
        let radius: CGFloat = compact ? 8 : 12
        let shape = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: radius, yRadius: radius)
        NSGradient(starting: Palette.glassTop.withAlphaComponent(alpha),
                   ending: Palette.glassBottom.withAlphaComponent(alpha))?.draw(in: shape, angle: 90)
        if alpha >= 0.1 || blurOn {
            NSColor.white.withAlphaComponent(min(0.2, 0.07 + alpha * 0.12)).setStroke()
            shape.lineWidth = 1
            shape.stroke()
        }

        let left: CGFloat = compact ? 9 : 12
        let right = width - left
        let divider = (width * (compact ? 0.34 : 0.43)).rounded()
        let rowH = rowHeight
        let top = rowTop
        let scale = max(0.64, min(1.65, min(width / designWidth, rowH / (compact ? 24 : 42))))
        let chrome = min(1, width / designWidth)
        let names = showNames

        // Header.
        let state: NSColor = feed.connected ? (live == count ? Palette.green : Palette.gold)
            : now.timeIntervalSince(feedStarted) < 12 ? Palette.gold : Palette.red
        let dot: CGFloat = compact ? 6 : 7
        let cy = head / 2
        state.withAlphaComponent(0.22).setFill()
        NSBezierPath(ovalIn: NSRect(x: left - dot * 0.4, y: cy - dot * 0.9, width: dot * 1.8, height: dot * 1.8)).fill()
        state.setFill()
        NSBezierPath(ovalIn: NSRect(x: left + dot * 0.05, y: cy - dot / 2, width: dot, height: dot)).fill()
        let heading = compact ? (width < 150 ? "永续" : "USDT 永续") : "币安永续"
        write(heading, scaled(Fonts.title(compact), chrome), Palette.main, left: left + dot + 7, top: 0,
              right: buttonRect(2).minX - 4, height: head, halo: halo)

        let chromeVisible = inside || alpha >= 0.35 || blurOn
        for (index, kind) in buttonHits.enumerated() {
            let rect = buttonRect(index)
            let hot = hover == kind, down = pressed == kind && hot
            if hot {
                (kind == .close ? NSColor.rgb(226, 64, 86, down ? 1 : 0.88) : NSColor.white.withAlphaComponent(down ? 0.18 : 0.12)).setFill()
                NSBezierPath(roundedRect: rect, xRadius: 6, yRadius: 6).fill()
            }
            let ink = hot ? (kind == .close ? NSColor.white : Palette.main) : Palette.muted.withAlphaComponent(chromeVisible ? 1 : 0.7)
            drawIcon(kind, in: rect, color: ink)
        }

        if alpha >= 0.1 || blurOn {
            NSColor.white.withAlphaComponent(0.09).setStroke()
            let rule = NSBezierPath()
            rule.move(to: NSPoint(x: 8, y: head + 0.5))
            rule.line(to: NSPoint(x: width - 8, y: head + 0.5))
            rule.lineWidth = 1
            rule.stroke()
        }

        if !compact {
            write("品种", scaled(Fonts.caption, chrome), Palette.muted, left: left, top: head, right: divider, height: lead, halo: halo)
            write("最新价 · 24h 涨跌", scaled(Fonts.caption, chrome), Palette.muted, left: divider, top: head, right: right,
                  height: lead, halo: halo, alignRight: true)
        }

        // Rows.
        let shown = Array(items.dropFirst(firstRow).prefix(visibleRows))
        for (index, item) in shown.enumerated() {
            let y = top + CGFloat(index) * rowH
            if hoverRow == index {
                NSColor.white.withAlphaComponent(0.07).setFill()
                NSBezierPath(roundedRect: NSRect(x: 4, y: y + 1, width: width - 8, height: rowH - 2), xRadius: 6, yRadius: 6).fill()
            } else if index > 0 && (alpha >= 0.45 || blurOn) && hoverRow != index - 1 {
                NSColor.white.withAlphaComponent(0.06).setStroke()
                let rule = NSBezierPath()
                rule.move(to: NSPoint(x: left, y: y + 0.5))
                rule.line(to: NSPoint(x: right, y: y + 0.5))
                rule.lineWidth = 1
                rule.stroke()
            }

            let quote = displayed[item.symbol]
            let price = quote.map { Format.price($0.price, decimals: decimals(for: item)) } ?? "—"
            let change = quote?.change
            let changeText = Format.change(change)
            let isFresh = fresh(item)
            let changeColor = !isFresh || change == nil || change == 0 ? Palette.muted : change! > 0 ? Palette.green : Palette.red
            var tint = Palette.main
            var flash: NSColor?
            if !isFresh { tint = Palette.muted }
            else if let move = direction[item.symbol], let at = changedAt[item.symbol] {
                let age = CGFloat(now.timeIntervalSince(at))
                tint = age < 1.2 ? move : Palette.blend(move, Palette.main, (age - 1.2) / 1.3)
                if age < 0.9 { flash = move.withAlphaComponent(0.25 * (1 - age / 0.9)) }
            }

            // A just-triggered alert makes the row pulse gold for a few seconds.
            let alertAge = alertedAt[item.symbol].map { now.timeIntervalSince($0) } ?? .greatestFiniteMagnitude
            if alertAge < alertGlowSeconds {
                let pulse = 0.5 + 0.5 * cos(alertAge * .pi * 2 / 1.2)
                let glow = CGFloat((0.1 + 0.16 * pulse) * (1 - alertAge / alertGlowSeconds))
                Palette.gold.withAlphaComponent(glow).setFill()
                NSBezierPath(roundedRect: NSRect(x: 4, y: y + 1, width: width - 8, height: rowH - 2), xRadius: 6, yRadius: 6).fill()
            }

            let codeFont = scaled(compact ? Fonts.miniCode : Fonts.code, scale)
            let codeBox = write(item.code, codeFont, item.metal ? Palette.gold : Palette.main, left: left, top: y, right: divider - 4,
                                height: names ? (rowH * 0.56).rounded() : rowH, halo: halo)
            let armed = alerts.contains { $0.code == item.code }
            if (armed || alertAge < alertGlowSeconds) && codeBox.width > 0 {
                drawBell(x: codeBox.maxX + 4, centerY: codeBox.midY, size: (compact ? 9 : 10) * min(1.3, scale),
                         color: armed ? Palette.gold.withAlphaComponent(0.82) : Palette.gold, halo: halo)
            }
            if names {
                write(item.name, scaled(Fonts.caption, scale), Palette.muted, left: left, top: y + (rowH * 0.52).rounded(),
                      right: divider - 4, height: (rowH * 0.42).rounded(), halo: halo)
                write(price, scaled(Fonts.digits, scale), tint, left: divider + 4, top: y, right: right,
                      height: (rowH * 0.58).rounded(), halo: halo, alignRight: true, backdrop: flash)
                write(changeText, scaled(Fonts.change, scale), changeColor, left: divider + 4, top: y + (rowH * 0.54).rounded(),
                      right: right, height: (rowH * 0.40).rounded(), halo: halo, alignRight: true)
            } else if !compact && !changeText.isEmpty {
                let column = (60 * min(1.3, scale)).rounded()
                write(price, scaled(Fonts.digits, scale), tint, left: divider + 4, top: y, right: right - column - 6,
                      height: rowH, halo: halo, alignRight: true, backdrop: flash)
                write(changeText, scaled(Fonts.change, scale), changeColor, left: right - column, top: y, right: right,
                      height: rowH, halo: halo, alignRight: true)
            } else {
                write(price, scaled(compact ? Fonts.miniDigits : Fonts.digits, scale), tint, left: divider + 4, top: y,
                      right: right, height: rowH, halo: halo, alignRight: true, backdrop: flash)
            }
        }

        // Scroll indicator.
        let footTop = top + CGFloat(visibleRows) * rowH
        if visibleRows < count {
            let trackTop = top + 3, track = CGFloat(visibleRows) * rowH - 6
            let thumb = max(14, track * CGFloat(visibleRows) / CGFloat(count))
            let offset = (track - thumb) * CGFloat(firstRow) / CGFloat(max(1, count - visibleRows))
            NSColor.white.withAlphaComponent(inside ? 0.43 : 0.24).setFill()
            NSBezierPath(roundedRect: NSRect(x: width - 5, y: trackTop + offset, width: 3, height: thumb), xRadius: 1.5, yRadius: 1.5).fill()
        }

        // Footer.
        let footHeight = max(1, height - footTop)
        var range = visibleRows < count ? "\(firstRow + 1)–\(firstRow + visibleRows) / \(count)" : ""
        if !compact {
            let allLive = live == count && count > 0
            let clock = DateFormatter.localizedString(from: now, dateStyle: .none, timeStyle: .medium)
            let status = allLive ? "实时 · " + clock : feed.connected ? "部分品种暂无新成交" : feed.status
            let color = allLive ? Palette.green : feed.connected ? Palette.gold : state
            if range.isEmpty { range = "\(count) 个品种" }
            write(status, scaled(Fonts.caption, chrome), color, left: left, top: footTop, right: width - 84, height: footHeight, halo: halo)
        }
        if !range.isEmpty {
            write(range, scaled(Fonts.caption, compact ? 0.85 : chrome), Palette.muted, left: width - 84, top: footTop,
                  right: width - 20, height: footHeight, halo: halo, alignRight: true)
        }

        // Resize grip.
        let gripAlpha: CGFloat = hover == .grip || resizing ? 0.86 : inside ? 0.43 : (alpha >= 0.1 || blurOn) ? 0.24 : 0
        if gripAlpha > 0 {
            NSColor.white.withAlphaComponent(gripAlpha).setFill()
            let step: CGFloat = 3.5, size: CGFloat = 1.9
            let ox = width - 6, oy = height - 6
            for i in 0..<3 {
                for j in 0...i {
                    NSBezierPath(ovalIn: NSRect(x: ox - CGFloat(j) * step - size / 2, y: oy - CGFloat(i - j) * step - size / 2,
                                                width: size, height: size)).fill()
                }
            }
        }
    }
}

enum Fonts {
    static func title(_ compact: Bool) -> NSFont {
        return compact ? NSFont.systemFont(ofSize: 12, weight: .medium) : NSFont.systemFont(ofSize: 13.5, weight: .semibold)
    }
    static let caption = NSFont.systemFont(ofSize: 10.5)
    static let code = NSFont.systemFont(ofSize: 13.5, weight: .semibold)
    static let miniCode = NSFont.systemFont(ofSize: 11.5, weight: .semibold)
    static let digits = NSFont.monospacedDigitSystemFont(ofSize: 19, weight: .semibold)
    static let miniDigits = NSFont.monospacedDigitSystemFont(ofSize: 15, weight: .semibold)
    static let change = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium)
}

// MARK: - Watchlist picker

final class WatchPicker: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate, NSWindowDelegate {
    private let window: NSWindow
    private let search = NSSearchField()
    private let filter = NSPopUpButton(frame: .zero, pullsDown: false)
    private let table = NSTableView()
    private let status = NSTextField(labelWithString: "")
    private let catalog: [Instrument]
    private let source: String
    private(set) var choices: Set<String>
    private var rows: [Instrument] = []

    init(catalog: [Instrument], selected: Set<String>, source: String) {
        self.catalog = catalog
        self.choices = selected
        self.source = source
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 500),
                          styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "自选品种"
        window.minSize = NSSize(width: 440, height: 360)
        window.delegate = self
        window.isReleasedWhenClosed = false
        let content = window.contentView!
        let w: CGFloat = 580, h: CGFloat = 500

        search.frame = NSRect(x: 16, y: h - 44, width: 360, height: 24)
        search.placeholderString = "搜索代码或名称"
        search.autoresizingMask = [.width, .minYMargin]
        search.delegate = self
        content.addSubview(search)

        filter.frame = NSRect(x: 388, y: h - 46, width: 176, height: 28)
        filter.addItems(withTitles: ["全部品种", "加密货币", "贵金属", "美股／ETF", "已自选"])
        filter.autoresizingMask = [.minXMargin, .minYMargin]
        filter.target = self
        filter.action = #selector(reload)
        content.addSubview(filter)

        let columns: [(String, String, CGFloat)] = [("pick", "", 28), ("symbol", "合约代码", 150), ("name", "名称", 190), ("category", "类别", 120)]
        for (id, title, width) in columns {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            column.title = title
            column.width = width
            table.addTableColumn(column)
        }
        table.rowHeight = 22
        table.usesAlternatingRowBackgroundColors = true
        table.dataSource = self
        table.delegate = self
        table.target = self
        table.doubleAction = #selector(toggleRow)
        let scroller = NSScrollView(frame: NSRect(x: 16, y: 56, width: w - 32, height: h - 112))
        scroller.documentView = table
        scroller.hasVerticalScroller = true
        scroller.borderType = .bezelBorder
        scroller.autoresizingMask = [.width, .height]
        content.addSubview(scroller)

        status.frame = NSRect(x: 16, y: 20, width: 300, height: 18)
        status.textColor = .secondaryLabelColor
        content.addSubview(status)

        let apply = NSButton(title: "应用", target: self, action: #selector(applyChoice))
        apply.frame = NSRect(x: w - 106, y: 12, width: 90, height: 32)
        apply.keyEquivalent = "\r"
        apply.autoresizingMask = [.minXMargin]
        content.addSubview(apply)
        let cancel = NSButton(title: "取消", target: self, action: #selector(cancelChoice))
        cancel.frame = NSRect(x: w - 202, y: 12, width: 90, height: 32)
        cancel.keyEquivalent = "\u{1b}"
        cancel.autoresizingMask = [.minXMargin]
        content.addSubview(cancel)
        reload()
    }

    func run() -> Set<String>? {
        NSApp.activate(ignoringOtherApps: true)
        window.center()
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(search)
        let response = NSApp.runModal(for: window)
        window.orderOut(nil)
        return response == .OK ? choices : nil
    }

    @objc private func reload() {
        let query = search.stringValue.trimmingCharacters(in: .whitespaces)
        let category = filter.titleOfSelectedItem ?? "全部品种"
        let known = Set(catalog.map { $0.code })
        let extras = choices.subtracting(known).sorted().map {
            Instrument(code: $0, name: $0, category: "已自选", metal: false, isDefault: false)
        }
        rows = (catalog + extras).filter { item in
            if category == "已自选" { if !choices.contains(item.code) { return false } }
            else if category != "全部品种" && category != item.category { return false }
            return query.isEmpty || item.symbol.localizedCaseInsensitiveContains(query) ||
                item.name.localizedCaseInsensitiveContains(query)
        }
        table.reloadData()
        updateStatus()
    }

    private func updateStatus() {
        status.stringValue = "\(source)  ｜  已选 \(choices.count)"
    }

    func controlTextDidChange(_ obj: Notification) { reload() }

    func numberOfRows(in tableView: NSTableView) -> Int { return rows.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < rows.count, let id = tableColumn?.identifier.rawValue else { return nil }
        let item = rows[row]
        if id == "pick" {
            let box = NSButton(checkboxWithTitle: "", target: self, action: #selector(toggleBox(_:)))
            box.state = choices.contains(item.code) ? .on : .off
            box.tag = row
            return box
        }
        let text = id == "symbol" ? item.symbol : id == "name" ? item.name : item.category
        let label = NSTextField(labelWithString: text)
        label.lineBreakMode = .byTruncatingTail
        return label
    }

    @objc private func toggleBox(_ sender: NSButton) {
        guard sender.tag < rows.count else { return }
        let code = rows[sender.tag].code
        if sender.state == .on { choices.insert(code) } else { choices.remove(code) }
        updateStatus()
    }

    @objc private func toggleRow() {
        let row = table.clickedRow
        guard row >= 0, row < rows.count else { return }
        let code = rows[row].code
        if choices.contains(code) { choices.remove(code) } else { choices.insert(code) }
        table.reloadData(forRowIndexes: IndexSet(integer: row), columnIndexes: IndexSet(integer: 0))
        updateStatus()
    }

    @objc private func applyChoice() {
        guard !choices.isEmpty else {
            let alert = NSAlert()
            alert.messageText = "请至少选择一个品种。"
            alert.beginSheetModal(for: window, completionHandler: nil)
            return
        }
        NSApp.stopModal(withCode: .OK)
    }

    @objc private func cancelChoice() { NSApp.stopModal(withCode: .cancel) }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.stopModal(withCode: .cancel)
        return true
    }
}

// MARK: - Price alert window

final class AlertPicker: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSTextFieldDelegate, NSWindowDelegate {
    private let window: NSWindow
    private let symbolButton = NSPopUpButton(frame: .zero, pullsDown: false)
    private let priceField = NSTextField()
    private let hint = NSTextField(wrappingLabelWithString: "")
    private let table = NSTableView()
    private let instruments: [Instrument]
    private let quote: (String) -> Quote?
    private let decimals: (Instrument) -> Int
    private(set) var alerts: [PriceAlert]
    private var timer: Timer?

    init(instruments: [Instrument], alerts: [PriceAlert], preset: String?,
         quote: @escaping (String) -> Quote?, decimals: @escaping (Instrument) -> Int) {
        self.instruments = instruments
        self.alerts = alerts
        self.quote = quote
        self.decimals = decimals
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 580, height: 440),
                          styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "价格提醒"
        window.minSize = NSSize(width: 480, height: 340)
        window.delegate = self
        window.isReleasedWhenClosed = false
        let content = window.contentView!
        let w: CGFloat = 580, h: CGFloat = 440

        let symbolLabel = NSTextField(labelWithString: "品种")
        symbolLabel.frame = NSRect(x: 16, y: h - 40, width: 32, height: 20)
        symbolLabel.autoresizingMask = [.minYMargin]
        content.addSubview(symbolLabel)
        symbolButton.frame = NSRect(x: 50, y: h - 44, width: 200, height: 28)
        symbolButton.autoresizingMask = [.minYMargin]
        for item in instruments { symbolButton.addItem(withTitle: item.code + "  ·  " + item.name) }
        symbolButton.target = self
        symbolButton.action = #selector(symbolChanged)
        content.addSubview(symbolButton)

        let priceLabel = NSTextField(labelWithString: "提醒价格")
        priceLabel.frame = NSRect(x: 264, y: h - 40, width: 60, height: 20)
        priceLabel.autoresizingMask = [.minYMargin]
        content.addSubview(priceLabel)
        priceField.frame = NSRect(x: 326, y: h - 42, width: 130, height: 24)
        priceField.placeholderString = "例如 70000"
        priceField.autoresizingMask = [.minYMargin]
        priceField.delegate = self
        content.addSubview(priceField)
        let add = NSButton(title: "添加提醒", target: self, action: #selector(addAlert))
        add.frame = NSRect(x: 464, y: h - 46, width: 100, height: 32)
        add.keyEquivalent = "\r"
        add.autoresizingMask = [.minYMargin, .minXMargin]
        content.addSubview(add)

        hint.frame = NSRect(x: 16, y: h - 84, width: w - 32, height: 32)
        hint.textColor = .secondaryLabelColor
        hint.autoresizingMask = [.width, .minYMargin]
        content.addSubview(hint)

        let columns: [(String, String, CGFloat)] = [("symbol", "品种", 130), ("rule", "提醒条件", 180), ("now", "当前价", 120), ("state", "状态", 100)]
        for (id, title, width) in columns {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(id))
            column.title = title
            column.width = width
            table.addTableColumn(column)
        }
        table.rowHeight = 22
        table.allowsMultipleSelection = true
        table.usesAlternatingRowBackgroundColors = true
        table.dataSource = self
        table.delegate = self
        let scroller = NSScrollView(frame: NSRect(x: 16, y: 56, width: w - 32, height: h - 148))
        scroller.documentView = table
        scroller.hasVerticalScroller = true
        scroller.borderType = .bezelBorder
        scroller.autoresizingMask = [.width, .height]
        content.addSubview(scroller)

        let remove = NSButton(title: "删除所选", target: self, action: #selector(removeSelected))
        remove.frame = NSRect(x: 12, y: 12, width: 100, height: 32)
        content.addSubview(remove)
        let note = NSTextField(labelWithString: "价格触达后会弹出通知并响铃，每条提醒只触发一次。")
        note.frame = NSRect(x: 120, y: 20, width: 340, height: 18)
        note.textColor = .secondaryLabelColor
        content.addSubview(note)
        let done = NSButton(title: "完成", target: self, action: #selector(finish))
        done.frame = NSRect(x: w - 106, y: 12, width: 90, height: 32)
        done.keyEquivalent = "\u{1b}"
        done.autoresizingMask = [.minXMargin]
        content.addSubview(done)

        if let preset = preset, let index = instruments.firstIndex(where: { $0.code == preset }) {
            symbolButton.selectItem(at: index)
        }
        prefill()
        table.reloadData()
    }

    func run() -> [PriceAlert] {
        NSApp.activate(ignoringOtherApps: true)
        window.center()
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(priceField)
        let refresh = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            self?.table.reloadData()
            self?.updateHint()
        }
        RunLoop.main.add(refresh, forMode: .modalPanel)
        timer = refresh
        NSApp.runModal(for: window)
        timer?.invalidate()
        window.orderOut(nil)
        return alerts
    }

    private var current: Instrument? {
        let index = symbolButton.indexOfSelectedItem
        return index >= 0 && index < instruments.count ? instruments[index] : nil
    }

    @objc private func symbolChanged() { prefill() }

    private func prefill() {
        if let item = current, let now = quote(item.symbol) {
            priceField.stringValue = Format.price(now.price, decimals: decimals(item)).replacingOccurrences(of: ",", with: "")
        } else {
            priceField.stringValue = ""
        }
        updateHint()
    }

    func controlTextDidChange(_ obj: Notification) { updateHint() }

    private func updateHint() {
        guard let item = current else { hint.stringValue = "请先在“自选品种”中添加品种。"; return }
        guard let now = quote(item.symbol) else { hint.stringValue = "\(item.symbol) 暂未收到价格。"; return }
        let places = decimals(item)
        let base = "当前价 " + Format.price(now.price, decimals: places)
        if let target = PriceAlert.readPrice(priceField.stringValue), target != now.price {
            let change = NSDecimalNumber(decimal: (target - now.price) / now.price).doubleValue
            hint.stringValue = base + " → 将在价格" + (target > now.price ? "涨到 ≥ " : "跌到 ≤ ") +
                Format.price(target, decimals: places) + " 时提醒（" + Format.change(change) + "）"
        } else {
            hint.stringValue = base + "。输入高于当前价 = 涨到提醒，低于当前价 = 跌到提醒。"
        }
    }

    @objc private func addAlert() {
        if let error = validateAndAdd() {
            let alert = NSAlert()
            alert.messageText = error
            alert.beginSheetModal(for: window, completionHandler: nil)
        }
    }

    /// Returns an error message, or nil when the alert was added.
    private func validateAndAdd() -> String? {
        guard let item = current else { return "请先在“自选品种”中添加品种。" }
        guard let target = PriceAlert.readPrice(priceField.stringValue) else { return "请输入有效的提醒价格，例如 70000。" }
        guard let now = quote(item.symbol) else { return "\(item.symbol) 暂未收到价格，请稍后再试。" }
        if target == now.price { return "提醒价格不能等于当前价。" }
        if alerts.count >= 100 { return "提醒数量已达上限（100 条）。" }
        let alert = PriceAlert(code: item.code, above: target > now.price, target: target)
        if alerts.contains(alert) { return "已经有相同的提醒了。" }
        alerts.append(alert)
        table.reloadData()
        priceField.stringValue = ""
        updateHint()
        return nil
    }

    @objc private func removeSelected() {
        let rows = table.selectedRowIndexes
        alerts = alerts.enumerated().filter { !rows.contains($0.offset) }.map { $0.element }
        table.reloadData()
    }

    @objc private func finish() { NSApp.stopModal() }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.stopModal()
        return true
    }

    func numberOfRows(in tableView: NSTableView) -> Int { return alerts.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < alerts.count, let id = tableColumn?.identifier.rawValue else { return nil }
        let alert = alerts[row]
        let item = instruments.first { $0.code == alert.code }
        let places = item.map { decimals($0) } ?? -1
        let text: String
        switch id {
        case "symbol": text = alert.symbol
        case "rule": text = alert.describe(places)
        case "now": text = quote(alert.symbol).map { Format.price($0.price, decimals: places) } ?? "—"
        default: text = item == nil ? "不在自选中" : "等待触发"
        }
        let label = NSTextField(labelWithString: text)
        label.lineBreakMode = .byTruncatingTail
        if item == nil { label.textColor = .secondaryLabelColor }
        return label
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    var widget: WidgetView?

    // Show alert banners even while the app is in front.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        widget?.show()
        completionHandler()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        let menu = NSMenu()
        let appItem = NSMenuItem()
        menu.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "隐藏币安浮动行情", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "退出币安浮动行情", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        NSApp.mainMenu = menu

        let view = WidgetView()
        widget = view
        view.show()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        widget?.show()
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        widget?.shutdown()
    }
}

// MARK: - Self test (./MarketFloat.app/Contents/MacOS/MarketFloat --self-test)

func selfTest() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ name: String) { if !condition { failures.append(name) } }

    let feed = MarketFeed(symbols: ["BTCUSDT", "XAUUSDT"])
    let now = Int64(Date().timeIntervalSince1970 * 1000)
    check(feed.accept("[{\"symbol\":\"BTCUSDT\",\"price\":\"68012.34\",\"time\":\(now)}]", source: .price), "rest price")
    check(!feed.accept("[{\"symbol\":\"UNKNOWNUSDT\",\"price\":\"1.0\",\"time\":\(now)}]", source: .price), "unknown symbol")
    check(!feed.accept("[{\"symbol\":\"BTCUSDT\",\"price\":\"-1\",\"time\":\(now)}]", source: .price), "negative price")
    check(!feed.accept("{}", source: .price), "bad json")
    check(feed.accept("[{\"symbol\":\"XAUUSDT\",\"lastPrice\":\"4200.02\",\"openPrice\":\"4000.00\",\"closeTime\":\(now + 2)}," +
                      "{\"symbol\":\"BTCUSDT\",\"lastPrice\":\"68013.00\",\"openPrice\":\"70000.00\",\"closeTime\":\(now + 2)}]", source: .day), "24h")
    check(Format.change(feed.get("XAUUSDT")?.change) == "+5.00%", "change up")
    check(Format.change(feed.get("BTCUSDT")?.change) == "-2.84%", "change down")
    check(!feed.accept("[{\"e\":\"24hrMiniTicker\",\"s\":\"BTCUSDT\",\"c\":\"1.0\",\"o\":\"1.0\",\"E\":\(now + 3),\"st\":2}]", source: .stream), "non usd-m")
    feed.accept("[{\"symbol\":\"BTCUSDT\",\"price\":\"68030.00\",\"time\":\(now + 4)}]", source: .price)
    check(feed.accept("[{\"e\":\"24hrMiniTicker\",\"s\":\"BTCUSDT\",\"c\":\"68020.10\",\"o\":\"68000.00\",\"E\":\(now + 3)}]", source: .stream), "stream")
    check(feed.get("BTCUSDT")?.price == Decimal(string: "68030.00") && feed.get("BTCUSDT")?.open == 68000, "stream open merge")
    check(Format.price(Decimal(string: "65005.2")!, decimals: 2) == "65,005.20", "price 2dp")
    check(Format.price(Decimal(string: "0.12")!, decimals: 5) == "0.12000", "price 5dp")
    check(Format.price(Decimal(string: "1234.5")!, decimals: 0) == "1,235", "price 0dp")
    check(Format.change(0) == "0.00%" && Format.change(nil) == "", "change zero")
    let alert = PriceAlert.parse("BTC>70000.5")
    check(alert != nil && alert!.above && alert!.target == Decimal(string: "70000.5") && alert!.serialized == "BTC>70000.5" &&
          PriceAlert.parse("XAU<4000")?.above == false && PriceAlert.parse("bad") == nil && PriceAlert.parse("BTC>-1") == nil,
          "alert parse")
    check(PriceAlert.readPrice(" 70,000.5 ") == Decimal(string: "70000.5") && PriceAlert.readPrice("abc") == nil, "alert price input")
    check(alert!.hit(Decimal(string: "70000.5")!) && !alert!.hit(70000) &&
          PriceAlert(code: "XAU", above: false, target: 4000).hit(3999), "alert trigger")
    check(Format.significantScale("70.5500") == 2 && Format.significantScale("64222.50") == 1 &&
          Format.significantScale("100.000") == 0, "significant scale")
    let sample = "{\"symbols\":[{\"symbol\":\"HYPEUSDT\",\"quoteAsset\":\"USDT\",\"contractType\":\"PERPETUAL\",\"status\":\"TRADING\",\"underlyingType\":\"COIN\",\"filters\":[{\"filterType\":\"PRICE_FILTER\",\"tickSize\":\"0.0010\"}]}," +
        "{\"symbol\":\"ABCDUSDT\",\"quoteAsset\":\"USDT\",\"contractType\":\"TRADIFI_PERPETUAL\",\"status\":\"TRADING\",\"underlyingType\":\"EQUITY\"}," +
        "{\"symbol\":\"HKDUSDT\",\"quoteAsset\":\"USDT\",\"contractType\":\"TRADIFI_PERPETUAL\",\"status\":\"TRADING\",\"underlyingType\":\"HK_EQUITY\"}]}"
    let parsed = Catalog.parse(sample.data(using: .utf8)!) ?? []
    check(parsed.count == 2 && parsed.first { $0.code == "HYPE" }?.decimals == 3 &&
          parsed.first { $0.code == "ABCD" }?.category == "美股／ETF", "catalog")
    if let url = Bundle.main.url(forResource: "BinanceContracts", withExtension: "json"),
       let data = try? Data(contentsOf: url) {
        check((Catalog.parse(data)?.count ?? 0) >= 700, "bundled catalog")
    } else {
        failures.append("bundled catalog missing")
    }
    print(failures.isEmpty ? "PASS: quotes, 24h change, stream merge, precision, catalog." : "FAIL: " + failures.joined(separator: ", "))
    return failures.isEmpty
}

if CommandLine.arguments.contains("--self-test") {
    exit(selfTest() ? 0 : 1)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.run()
