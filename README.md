# 币安浮动行情

币安 USDT 永续合约的桌面浮动行情面板，支持 Windows 和 macOS。

- 实时价格：WebSocket 推送为主，REST 轮询备用，断线自动重连
- 显示 24 小时涨跌幅，价格变动时短暂高亮（涨绿跌红）
- 半透明毛玻璃面板，背景透明度可调 7 档，可置顶、可拖动、可缩放
- 自选品种：加密货币、贵金属、美股／ETF 的 USDT 永续合约，支持搜索
- 不需要登录，也不需要 API 密钥

## 下载

前往 **[下载页（Releases）](https://github.com/jiashuai0428/marketfloat/releases/latest)**：

| 系统 | 文件 | 安装 |
|---|---|---|
| Windows 10 / 11 | `MarketFloat-Windows.zip` | 解压后双击 `MarketFloat.exe` |
| macOS 11 及以上（Apple 芯片和 Intel 通用） | `MarketFloat-macOS.zip` | 解压后把「币安浮动行情.app」拖进「应用程序」 |

### 第一次打开时的系统提示

程序没有购买付费的开发者签名，第一次打开时系统会提醒，放行一次即可：

- **Windows**：出现「Windows 已保护你的电脑」时，点「更多信息」→「仍要运行」。
- **macOS 14 及更早**：按住 Control 键点按图标 →「打开」→「打开」。
- **macOS 15 及以上**：先双击一次并关掉提示，再打开「系统设置 › 隐私与安全性」，在下方点「仍要打开」。

## 使用

- 按住面板任意位置拖动，拖右下角调整大小。
- 按 F2（Mac 上也可以双击面板）切换普通／极简模式。
- 点 ≡ 或右键：自选品种、背景透明度、窗口置顶、行情连接诊断。
- 滚轮、↑／↓、Page Up／Page Down 翻看报价。

## 自己编译

- **Windows**：用系统自带的 .NET Framework 编译器：

  ```
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /out:MarketFloat.exe /r:System.Web.Extensions.dll /r:System.Net.Http.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /resource:BinanceContracts.json,BinanceContracts.json MarketFloat.cs
  ```

- **macOS**：安装 Xcode 命令行工具（`xcode-select --install`），然后在 `macOS` 文件夹运行 `bash build.sh`。

每次推送代码，GitHub Actions 都会自动在 Mac 上编译并测试 macOS 版。

> 报价来自币安公开行情接口，仅供参考，不构成任何投资建议。
