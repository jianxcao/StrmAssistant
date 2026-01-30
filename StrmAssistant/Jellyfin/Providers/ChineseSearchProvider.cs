using System;
using Jellyfin.Data.Enums;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Search;
using MediaBrowser.Model.Querying;
using StrmAssistant.Common;
using StrmAssistant.Jellyfin.Adapters;

namespace StrmAssistant.Jellyfin.Providers
{
    /// <summary>
    /// 中文搜索提供者 - 增强中文、拼音搜索能力
    /// </summary>
    public class ChineseSearchProvider
    {
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly ILogger<ChineseSearchProvider> _logger;
        
        public ChineseSearchProvider(LibraryManagerAdapter libraryManager, ILogger<ChineseSearchProvider> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }
        
        /// <summary>
        /// 执行中文增强搜索
        /// </summary>
        public async Task<List<BaseItem>> SearchAsync(
            string searchTerm,
            BaseItem parent = null,
            BaseItemKind[] includeItemTypes = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(searchTerm))
                    return new List<BaseItem>();
                
                _logger.LogDebug($"Chinese enhanced search for: {searchTerm}");
                
                // 1. 繁简转换
                var simplifiedTerm = LanguageUtility.ConvertTraditionalToSimplified(searchTerm);
                
                // 2. 拼音转换
                var pinyinInitials = LanguageUtility.ConvertToPinyinInitials(simplifiedTerm);
                
                // 3. 构建查询
                var query = new InternalItemsQuery
                {
                    SearchTerm = searchTerm,
                    Recursive = true,
                    IncludeItemTypes = includeItemTypes
                };
                
                if (parent != null)
                {
                    query.ParentId = parent.Id;
                }
                
                // 4. 执行标准搜索
                var standardResults = await _libraryManager.GetItemsAsync(query, cancellationToken);
                
                // 5. 执行拼音搜索（如果是中文）
                List<BaseItem> pinyinResults = new List<BaseItem>();
                if (LanguageUtility.IsChinese(simplifiedTerm))
                {
                    pinyinResults = await SearchByPinyinAsync(
                        pinyinInitials,
                        parent,
                        includeItemTypes,
                        cancellationToken);
                }
                
                // 6. 合并结果并去重
                var combinedResults = standardResults
                    .Union(pinyinResults)
                    .Distinct(new BaseItemComparer())
                    .ToList();
                
                // 7. 按相关性排序
                var sortedResults = SortByRelevance(combinedResults, searchTerm, simplifiedTerm, pinyinInitials);
                
                _logger.LogDebug($"Found {sortedResults.Count} items for search: {searchTerm}");
                
                return sortedResults;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in Chinese search: {ex.Message}");
                return new List<BaseItem>();
            }
        }
        
        /// <summary>
        /// 根据拼音首字母搜索
        /// </summary>
        private async Task<List<BaseItem>> SearchByPinyinAsync(
            string pinyinInitials,
            BaseItem parent,
            BaseItemKind[] includeItemTypes,
            CancellationToken cancellationToken)
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = includeItemTypes
            };
            
            if (parent != null)
            {
                query.ParentId = parent.Id;
            }
            
            var allItems = await _libraryManager.GetItemsAsync(query, cancellationToken);
            
            // 过滤出拼音匹配的项
            return allItems.Where(item =>
            {
                if (string.IsNullOrEmpty(item.Name))
                    return false;
                
                var itemPinyin = LanguageUtility.ConvertToPinyinInitials(item.Name);
                return itemPinyin.Contains(pinyinInitials, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }
        
        /// <summary>
        /// 按相关性排序搜索结果
        /// </summary>
        private List<BaseItem> SortByRelevance(
            List<BaseItem> items,
            string originalTerm,
            string simplifiedTerm,
            string pinyinTerm)
        {
            return items.OrderByDescending(item =>
            {
                var name = item.Name ?? string.Empty;
                var score = 0;
                
                // 完全匹配 - 最高分
                if (name.Equals(originalTerm, StringComparison.OrdinalIgnoreCase))
                    score += 1000;
                
                // 简体匹配
                if (name.Equals(simplifiedTerm, StringComparison.OrdinalIgnoreCase))
                    score += 900;
                
                // 开头匹配
                if (name.StartsWith(originalTerm, StringComparison.OrdinalIgnoreCase))
                    score += 500;
                
                if (name.StartsWith(simplifiedTerm, StringComparison.OrdinalIgnoreCase))
                    score += 400;
                
                // 包含匹配
                if (name.Contains(originalTerm, StringComparison.OrdinalIgnoreCase))
                    score += 200;
                
                if (name.Contains(simplifiedTerm, StringComparison.OrdinalIgnoreCase))
                    score += 100;
                
                // 拼音匹配
                if (LanguageUtility.IsChinese(name))
                {
                    var itemPinyin = LanguageUtility.ConvertToPinyinInitials(name);
                    if (itemPinyin.Equals(pinyinTerm, StringComparison.OrdinalIgnoreCase))
                        score += 300;
                    else if (itemPinyin.Contains(pinyinTerm, StringComparison.OrdinalIgnoreCase))
                        score += 50;
                }
                
                return score;
            })
            .ThenBy(item => item.Name)
            .ToList();
        }
        
        /// <summary>
        /// BaseItem 比较器（用于去重）
        /// </summary>
        private class BaseItemComparer : IEqualityComparer<BaseItem>
        {
            public bool Equals(BaseItem x, BaseItem y)
            {
                if (x == null || y == null)
                    return false;
                
                return x.Id == y.Id;
            }
            
            public int GetHashCode(BaseItem obj)
            {
                return obj?.Id.GetHashCode() ?? 0;
            }
        }
    }
}
