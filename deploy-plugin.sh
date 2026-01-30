#!/bin/bash

# Jellyfin Plugin Deployment Script
# 将插件及所有依赖复制到 Jellyfin 插件目录

set -e

echo "======================================"
echo "StrmAssistant Jellyfin Plugin Deployment"
echo "======================================"

# 配置
BUILD_DIR="StrmAssistant/bin/Release/net8.0"
PLUGIN_DIR="$HOME/.local/share/jellyfin/plugins/StrmAssistant"

# 检查是否包含调试符号
INCLUDE_PDB=false
if [ "$1" = "--with-pdb" ] || [ "$1" = "--debug" ]; then
    INCLUDE_PDB=true
    echo "Debug mode: Will include PDB files"
fi

# 检查构建目录
if [ ! -d "$BUILD_DIR" ]; then
    echo "Error: Build directory not found: $BUILD_DIR"
    echo "Please build the project first: dotnet build --configuration Release"
    exit 1
fi

# 创建插件目录
echo "Creating plugin directory: $PLUGIN_DIR"
mkdir -p "$PLUGIN_DIR"

# 复制主 DLL
echo "Copying main plugin DLL..."
cp "$BUILD_DIR/StrmAssistant.Jellyfin.dll" "$PLUGIN_DIR/"

# 复制依赖 DLLs
echo "Copying dependencies..."
cp "$BUILD_DIR/ChineseConverter.dll" "$PLUGIN_DIR/"
cp "$BUILD_DIR/TinyPinyin.dll" "$PLUGIN_DIR/"
cp "$BUILD_DIR/SQLitePCL.pretty.dll" "$PLUGIN_DIR/"
cp "$BUILD_DIR/SQLitePCLRaw.core.dll" "$PLUGIN_DIR/"

# 可选：复制 PDB 调试符号
if [ "$INCLUDE_PDB" = true ]; then
    echo "Copying debug symbols (PDB)..."
    if [ -f "$BUILD_DIR/StrmAssistant.Jellyfin.pdb" ]; then
        cp "$BUILD_DIR/StrmAssistant.Jellyfin.pdb" "$PLUGIN_DIR/"
        echo "  ✓ PDB file included for debugging"
    else
        echo "  ⚠ PDB file not found"
    fi
fi

# 显示已复制的文件
echo ""
echo "Deployed files:"
ls -lh "$PLUGIN_DIR"

echo ""
echo "======================================"
echo "Deployment complete!"
echo "======================================"
echo ""
if [ "$INCLUDE_PDB" = true ]; then
    echo "Debug mode enabled - detailed error traces available"
    echo ""
fi
echo "Next steps:"
echo "1. Restart Jellyfin server"
echo "   sudo systemctl restart jellyfin"
echo ""
echo "2. Check plugin loaded in Jellyfin:"
echo "   Dashboard > Plugins"
echo ""
echo "3. View logs:"
echo "   tail -f /var/log/jellyfin/jellyfin.log"
echo ""
echo "Tip: Run with --with-pdb to include debug symbols"
echo "======================================"
