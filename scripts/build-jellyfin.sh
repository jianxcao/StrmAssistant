#!/usr/bin/env bash
# 构建 StrmAssistant Jellyfin 插件
# 需要：.NET 9.0 SDK（https://dotnet.microsoft.com/download）

set -e
cd "$(dirname "$0")/.."

if ! command -v dotnet &>/dev/null; then
  echo "错误：未找到 dotnet。请安装 .NET 9.0 SDK：https://dotnet.microsoft.com/download"
  exit 1
fi

echo "正在恢复依赖..."
dotnet restore StrmAssistant/StrmAssistant.Jellyfin.csproj

echo "正在编译 Jellyfin 插件 (Release)..."
dotnet build StrmAssistant/StrmAssistant.Jellyfin.csproj -c Release --no-restore

echo "构建完成。输出目录：StrmAssistant/bin/Release/net9.0/"
echo "发布单文件插件：dotnet publish StrmAssistant/StrmAssistant.Jellyfin.csproj -c Release -o publish-jellyfin"
