#!/bin/bash
# 构建 MarketFloat.app（币安浮动行情）。需要 Xcode 命令行工具：xcode-select --install
set -euo pipefail
cd "$(dirname "$0")"

APP="MarketFloat.app"
MIN_OS="11.0"
SDK="$(xcrun --sdk macosx --show-sdk-path)"

rm -rf build "$APP"
mkdir -p build "$APP/Contents/MacOS" "$APP/Contents/Resources"

native="$(uname -m)"
if [ "$native" = "arm64" ]; then other="x86_64"; else other="arm64"; fi

echo "编译 $native …"
xcrun swiftc -O -sdk "$SDK" -target "$native-apple-macos$MIN_OS" MarketFloat.swift -o "build/MarketFloat-$native"
binaries=("build/MarketFloat-$native")

# 顺便编译另一种芯片架构，生成通用版（Apple 芯片 + Intel）；失败则只保留本机架构。
if xcrun swiftc -O -sdk "$SDK" -target "$other-apple-macos$MIN_OS" MarketFloat.swift -o "build/MarketFloat-$other" 2>/dev/null; then
  binaries+=("build/MarketFloat-$other")
fi
lipo -create "${binaries[@]}" -output "$APP/Contents/MacOS/MarketFloat"

cp BinanceContracts.json "$APP/Contents/Resources/"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>MarketFloat</string>
  <key>CFBundleIdentifier</key><string>local.marketfloat.widget</string>
  <key>CFBundleName</key><string>币安浮动行情</string>
  <key>CFBundleDisplayName</key><string>币安浮动行情</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.finance</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# 本机临时签名（不上传、不需要开发者账号），避免 Apple 芯片拒绝运行未签名程序。
codesign --force --sign - "$APP"

echo "自检 …"
"$APP/Contents/MacOS/MarketFloat" --self-test
echo "架构：$(lipo -archs "$APP/Contents/MacOS/MarketFloat")"
echo "完成。运行：open $APP   （可拖到“应用程序”文件夹）"
