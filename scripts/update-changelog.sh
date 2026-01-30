#!/usr/bin/env bash
# Changelog 生成辅助脚本
# 用法: ./update-changelog.sh VERSION "description"

set -e
cd "$(dirname "$0")/.."

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

VERSION=$1
DESCRIPTION=$2

if [ -z "$VERSION" ] || [ -z "$DESCRIPTION" ]; then
    echo -e "${YELLOW}用法: $0 VERSION \"description\"${NC}"
    echo ""
    echo "示例："
    echo "  $0 3.0.1 \"修复 Jellyfin 10.11 API 兼容性问题\""
    echo ""
    echo "描述模板："
    echo "  - 新功能：✨ 新增 XXX 功能"
    echo "  - 修复：🐛 修复 XXX 问题"
    echo "  - 改进：🔧 优化 XXX 性能"
    echo "  - 文档：📚 更新 XXX 文档"
    exit 1
fi

# 获取当前日期
DATE=$(date +"%Y-%m-%d")

# 生成 changelog 条目
CHANGELOG_ENTRY="### Version $VERSION ($DATE)

$DESCRIPTION
"

echo -e "${BLUE}生成的 Changelog 条目：${NC}"
echo "----------------------------------------"
echo "$CHANGELOG_ENTRY"
echo "----------------------------------------"
echo ""

# 创建临时文件用于编辑
TEMP_FILE=$(mktemp)
cat > "$TEMP_FILE" << 'EOF'
# 请在下面编辑 Changelog（保存并退出以应用）
# 格式参考：
#   ✨ 新功能
#   🐛 Bug 修复
#   🔧 改进优化
#   📚 文档更新
#   🏗️ 架构变化
#   ⚠️ 破坏性变更

EOF
echo "$CHANGELOG_ENTRY" >> "$TEMP_FILE"

# 打开编辑器
${EDITOR:-vim} "$TEMP_FILE"

# 读取编辑后的内容
NEW_CHANGELOG=$(cat "$TEMP_FILE" | grep -v "^#" | sed '/^$/d')

if [ -z "$NEW_CHANGELOG" ]; then
    echo -e "${YELLOW}未输入任何内容，已取消${NC}"
    rm "$TEMP_FILE"
    exit 0
fi

echo ""
echo -e "${GREEN}✓ Changelog 已准备${NC}"
echo ""
echo -e "${YELLOW}请手动将以下内容添加到 build.yaml 的 changelog 部分：${NC}"
echo ""
echo "$NEW_CHANGELOG"
echo ""
echo -e "${BLUE}或运行以下命令自动更新（需要手动验证）：${NC}"
echo "  # 备份 build.yaml"
echo "  cp build.yaml build.yaml.bak"
echo "  "
echo "  # 手动编辑 build.yaml，将上面的内容添加到 changelog: > 下方"
echo "  vi build.yaml"

rm "$TEMP_FILE"
