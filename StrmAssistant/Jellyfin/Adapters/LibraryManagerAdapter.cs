using System;
using Jellyfin.Data.Enums;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace StrmAssistant.Jellyfin.Adapters
{
    /// <summary>
    /// 库管理器适配器 - 统一 Jellyfin 的库管理接口
    /// </summary>
    public class LibraryManagerAdapter
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<LibraryManagerAdapter> _logger;
        
        public LibraryManagerAdapter(ILibraryManager libraryManager, ILogger<LibraryManagerAdapter> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }
        
        /// <summary>
        /// 获取所有媒体项
        /// </summary>
        public Task<List<BaseItem>> GetItemsAsync(InternalItemsQuery query, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug(
                    "Querying items: Types={Types}, Recursive={Recursive}, ParentId={ParentId}", 
                    string.Join(",", query.IncludeItemTypes ?? Array.Empty<BaseItemKind>()), 
                    query.Recursive,
                    query.ParentId);
                
                // Jellyfin 10.11+ GetItemList 返回 IReadOnlyList<BaseItem>
                var result = _libraryManager.GetItemList(query);
                
                _logger.LogDebug("Query returned {Count} items", result?.Count ?? 0);
                
                return Task.FromResult(result?.ToList() ?? new List<BaseItem>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get items with query: Types={Types}", 
                    string.Join(",", query.IncludeItemTypes ?? Array.Empty<BaseItemKind>()));
                return Task.FromResult(new List<BaseItem>());
            }
        }
        
        /// <summary>
        /// 合并多个版本到主项
        /// </summary>
        public Task<bool> MergeItemsAsync(Guid primaryId, Guid[] alternativeIds, CancellationToken cancellationToken = default)
        {
            try
            {
                // Jellyfin 的 MergeItems 方法
                foreach (var altId in alternativeIds)
                {
                    var primaryItem = _libraryManager.GetItemById(primaryId);
                    var altItem = _libraryManager.GetItemById(altId);
                    
                    if (primaryItem != null && altItem != null)
                    {
                        // 使用 Jellyfin 的内部合并逻辑
                        _logger.LogInformation("Merging {AltName} into {PrimaryName}", altItem.Name, primaryItem.Name);
                        // TODO: 调用实际的合并方法
                    }
                }
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to merge items");
                return Task.FromResult(false);
            }
        }
        
        /// <summary>
        /// 更新媒体项
        /// </summary>
        public async Task<bool> UpdateItemAsync(BaseItem item, ItemUpdateType updateType = ItemUpdateType.MetadataEdit, CancellationToken cancellationToken = default)
        {
            try
            {
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), updateType, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update item {ItemName}", item.Name);
                return false;
            }
        }
        
        /// <summary>
        /// 根据路径查找媒体项
        /// </summary>
        public BaseItem FindByPath(string path)
        {
            return _libraryManager.FindByPath(path, null);
        }
    }
}
