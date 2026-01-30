using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Sorting;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using StrmAssistant.Common;
using StrmAssistant.Jellyfin.Adapters;

namespace StrmAssistant.Jellyfin.Providers
{
    /// <summary>
    /// 拼音排序提供者 - 为中文内容提供拼音首字母排序
    /// </summary>
    public class PinyinSortComparer : IBaseItemComparer
    {
        private readonly ILogger<PinyinSortComparer> _logger;
        
        public PinyinSortComparer(ILogger<PinyinSortComparer> logger)
        {
            _logger = logger;
        }
        
        /// <summary>
        /// 排序类型
        /// </summary>
        public ItemSortBy Type => ItemSortBy.SortName;
        
        /// <summary>
        /// 比较两个媒体项
        /// </summary>
        public int Compare(BaseItem x, BaseItem y)
        {
            if (x == null && y == null)
                return 0;
            if (x == null)
                return -1;
            if (y == null)
                return 1;
            
            var xName = GetSortName(x);
            var yName = GetSortName(y);
            
            return string.Compare(xName, yName, StringComparison.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// 获取用于排序的名称
        /// </summary>
        private string GetSortName(BaseItem item)
        {
            var name = item.SortName ?? item.Name ?? string.Empty;
            
            // 如果是中文，转换为拼音首字母
            if (LanguageUtility.IsChinese(name))
            {
                return LanguageUtility.ConvertToPinyinInitials(name);
            }
            
            return name;
        }
    }
    
    /// <summary>
    /// 拼音排序服务 - 为媒体项设置拼音排序名称
    /// </summary>
    public class PinyinSortService
    {
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly ILogger<PinyinSortService> _logger;
        
        public PinyinSortService(LibraryManagerAdapter libraryManager, ILogger<PinyinSortService> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }
        
        /// <summary>
        /// 为所有中文媒体项设置拼音排序名称
        /// </summary>
        public async System.Threading.Tasks.Task<int> UpdatePinyinSortNamesAsync(System.IProgress<double> progress = null, System.Threading.CancellationToken cancellationToken = default)
        {
            var updatedCount = 0;
            
            try
            {
                var query = new InternalItemsQuery
                {
                    Recursive = true,
                    IsVirtualItem = false
                };
                
                var items = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var totalCount = items.Count;
                
                _logger.LogInformation("Processing {Count} items for pinyin sort name", totalCount);
                
                for (var i = 0; i < items.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var item = items[i];
                    var name = item.Name ?? string.Empty;
                    
                    // 仅处理中文名称
                    if (LanguageUtility.IsChinese(name))
                    {
                        var pinyinInitials = LanguageUtility.ConvertToPinyinInitials(name);
                        
                        if (item.SortName != pinyinInitials)
                        {
                            item.ForcedSortName = pinyinInitials;
                            await _libraryManager.UpdateItemAsync(item, cancellationToken: cancellationToken);
                            updatedCount++;
                            
                            _logger.LogDebug("Updated sort name for {ItemName}: {SortName}", name, pinyinInitials);
                        }
                    }
                    
                    progress?.Report((double)(i + 1) / totalCount * 100);
                }
                
                _logger.LogInformation("Updated pinyin sort names for {Count} items", updatedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update pinyin sort names");
            }
            
            return updatedCount;
        }
    }
}
