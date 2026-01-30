using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Adapters;

namespace StrmAssistant.Jellyfin.Services
{
    /// <summary>
    /// 媒体信息提取服务 - 使用 FFProbe 提取和持久化媒体流信息
    /// </summary>
    public class MediaInfoService
    {
        private readonly MediaEncoderAdapter _mediaEncoder;
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger<MediaInfoService> _logger;
        
        public MediaInfoService(
            MediaEncoderAdapter mediaEncoder,
            LibraryManagerAdapter libraryManager,
            IItemRepository itemRepository,
            ILogger<MediaInfoService> logger)
        {
            _mediaEncoder = mediaEncoder;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logger;
        }
        
        /// <summary>
        /// 提取并持久化媒体信息
        /// </summary>
        public async Task<bool> ExtractAndPersistMediaInfoAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Extracting media info for: {ItemName}", item.Name);
                
                // 1. 使用 FFProbe 提取媒体信息
                var mediaInfo = await _mediaEncoder.ExtractMediaInfoAsync(item, cancellationToken);
                if (mediaInfo == null)
                {
                    _logger.LogWarning("Failed to extract media info for {ItemName}", item.Name);
                    return false;
                }
                
                // 2. 保存媒体流信息到数据库
                if (mediaInfo.MediaStreams != null && mediaInfo.MediaStreams.Any())
                {
                    // 使用 IItemRepository 保存 MediaStreams
                    _itemRepository.SaveMediaStreams(item.Id, mediaInfo.MediaStreams.ToList(), cancellationToken);
                    
                    // 3. 更新 Item 元数据
                    await _libraryManager.UpdateItemAsync(item, ItemUpdateType.MetadataEdit, cancellationToken);
                    
                    _logger.LogInformation("Successfully extracted {Count} streams for {ItemName}", 
                        mediaInfo.MediaStreams.Count, item.Name);
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting media info for {ItemName}", item.Name);
                return false;
            }
        }
        
        /// <summary>
        /// 批量提取媒体信息
        /// </summary>
        public async Task<int> BatchExtractMediaInfoAsync(
            IEnumerable<BaseItem> items,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            var itemList = items.ToList();
            var successCount = 0;
            var totalCount = itemList.Count;
            
            _logger.LogInformation("Starting batch media info extraction for {Count} items", totalCount);
            
            for (var i = 0; i < totalCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                
                var item = itemList[i];
                var success = await ExtractAndPersistMediaInfoAsync(item, cancellationToken);
                
                if (success)
                    successCount++;
                
                // 报告进度
                progress?.Report((double)(i + 1) / totalCount * 100);
            }
            
            _logger.LogInformation("Batch extraction completed: {Success}/{Total} successful", successCount, totalCount);
            return successCount;
        }
        
        /// <summary>
        /// 检查是否需要提取媒体信息
        /// </summary>
        public bool NeedsExtraction(BaseItem item)
        {
            var streams = item.GetMediaStreams();
            
            // 如果没有媒体流信息，则需要提取
            if (streams == null || !streams.Any())
                return true;
            
            // 如果是视频但没有视频流，需要重新提取
            if (item is Video && !streams.Any(s => s.Type == MediaStreamType.Video))
                return true;
            
            return false;
        }
    }
}
