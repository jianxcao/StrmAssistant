#!/usr/bin/env bash
# 版本号管理脚本
# 用法: ./bump-version.sh [major|minor|patch|set VERSION]

set -e
cd "$(dirname "$0")/.."

# 颜色定义
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# 从 build.yaml 读取当前版本
CURRENT_VERSION=$(grep "^version:" build.yaml | awk '{print $2}')

if [ -z "$CURRENT_VERSION" ]; then
    echo -e "${RED}错误：无法从 build.yaml 读取当前版本${NC}"
    exit 1
fi

echo -e "${BLUE}当前版本：${GREEN}$CURRENT_VERSION${NC}"

# 解析版本号
IFS='.' read -ra VERSION_PARTS <<< "$CURRENT_VERSION"
MAJOR=${VERSION_PARTS[0]}
MINOR=${VERSION_PARTS[1]}
PATCH=${VERSION_PARTS[2]}

# 根据参数更新版本
case "$1" in
    major)
        MAJOR=$((MAJOR + 1))
        MINOR=0
        PATCH=0
        NEW_VERSION="$MAJOR.$MINOR.$PATCH"
        ;;
    minor)
        MINOR=$((MINOR + 1))
        PATCH=0
        NEW_VERSION="$MAJOR.$MINOR.$PATCH"
        ;;
    patch)
        PATCH=$((PATCH + 1))
        NEW_VERSION="$MAJOR.$MINOR.$PATCH"
        ;;
    set)
        if [ -z "$2" ]; then
            echo -e "${RED}错误：请提供版本号${NC}"
            echo "用法: $0 set 3.0.1"
            exit 1
        fi
        NEW_VERSION="$2"
        ;;
    *)
        echo -e "${YELLOW}用法: $0 [major|minor|patch|set VERSION]${NC}"
        echo ""
        echo "示例："
        echo "  $0 patch        # 3.0.0 -> 3.0.1"
        echo "  $0 minor        # 3.0.0 -> 3.1.0"
        echo "  $0 major        # 3.0.0 -> 4.0.0"
        echo "  $0 set 3.0.2    # 设置为 3.0.2"
        exit 0
        ;;
esac

echo -e "${BLUE}新版本：${GREEN}$NEW_VERSION${NC}"

# 确认
echo ""
read -p "确认更新版本号？(y/N) " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo -e "${YELLOW}已取消${NC}"
    exit 0
fi

# 备份文件
echo -e "${BLUE}备份文件...${NC}"
cp build.yaml build.yaml.bak
cp StrmAssistant/StrmAssistant.Jellyfin.csproj StrmAssistant/StrmAssistant.Jellyfin.csproj.bak

# 更新 build.yaml
echo -e "${BLUE}更新 build.yaml...${NC}"
if [[ "$OSTYPE" == "darwin"* ]]; then
    # macOS
    sed -i '' "s/^version: .*/version: $NEW_VERSION/" build.yaml
else
    # Linux
    sed -i "s/^version: .*/version: $NEW_VERSION/" build.yaml
fi

# 更新 .csproj 文件（AssemblyVersion 使用三位版本号）
ASSEMBLY_VERSION="${MAJOR}.${MINOR}.${PATCH}.0"
echo -e "${BLUE}更新 .csproj (AssemblyVersion: $ASSEMBLY_VERSION)...${NC}"
if [[ "$OSTYPE" == "darwin"* ]]; then
    sed -i '' "s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>$ASSEMBLY_VERSION</AssemblyVersion>|" StrmAssistant/StrmAssistant.Jellyfin.csproj
    sed -i '' "s|<FileVersion>.*</FileVersion>|<FileVersion>$ASSEMBLY_VERSION</FileVersion>|" StrmAssistant/StrmAssistant.Jellyfin.csproj
else
    sed -i "s|<AssemblyVersion>.*</AssemblyVersion>|<AssemblyVersion>$ASSEMBLY_VERSION</AssemblyVersion>|" StrmAssistant/StrmAssistant.Jellyfin.csproj
    sed -i "s|<FileVersion>.*</FileVersion>|<FileVersion>$ASSEMBLY_VERSION</FileVersion>|" StrmAssistant/StrmAssistant.Jellyfin.csproj
fi

# 验证更新
echo ""
echo -e "${GREEN}✓ 版本更新完成${NC}"
echo ""
echo "更新的文件："
echo "  - build.yaml: version: $NEW_VERSION"
echo "  - StrmAssistant.Jellyfin.csproj: AssemblyVersion: $ASSEMBLY_VERSION"
echo ""
echo -e "${YELLOW}请手动更新 build.yaml 中的 changelog 部分${NC}"
echo ""
echo "备份文件："
echo "  - build.yaml.bak"
echo "  - StrmAssistant.Jellyfin.csproj.bak"
echo ""
echo -e "${BLUE}下一步：${NC}"
echo "  1. 编辑 build.yaml，更新 changelog"
echo "  2. 提交更改：git add -A && git commit -m 'chore: bump version to $NEW_VERSION'"
echo "  3. 创建标签：git tag v$NEW_VERSION"
echo "  4. 推送：git push && git push --tags"
