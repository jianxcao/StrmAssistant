using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Adapters;
using StrmAssistant.Jellyfin.Services;

namespace StrmAssistant.Jellyfin.ScheduledTasks
{
    /// <summary>
    /// 媒体信息提取计划任务
    /// </summary>
    public class ExtractMediaInfoTask : IScheduledTask
    {
        private readonly MediaInfoService _mediaInfoService;
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly ILogger<ExtractMediaInfoTask> _logger;
        
        public string Name => "提取媒体信息";
        
        public string Key => "ExtractMediaInfo";
        
        public string Description => "使用 FFProbe 提取视频媒体流信息并持久化到数据库";
        
        public string Category => "StrmAssistant";
        
        public ExtractMediaInfoTask(
            MediaInfoService mediaInfoService,
            LibraryManagerAdapter libraryManager,
            ILogger<ExtractMediaInfoTask> logger)
        {
            _mediaInfoService = mediaInfoService;
            _libraryManager = libraryManager;
            _logger = logger;
        }
        
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting media info extraction task");
            
            try
            {
                // 检查是否强制重新提取
                var config = Jellyfin.JellyfinPlugin.Instance?.Configuration;
                var forceReExtract = config?.ForceReExtractMediaInfo ?? false;
                
                if (forceReExtract)
                {
                    _logger.LogWarning("Force re-extract mode enabled - will process ALL items");
                }
                
                // 获取所有需要提取媒体信息的视频项
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                };
                
                var items = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var itemsNeedingExtraction = items.Where(item => _mediaInfoService.NeedsExtraction(item, forceReExtract)).ToList();
                
                _logger.LogInformation("Found {Count} items needing media info extraction (Force: {Force})", 
                    itemsNeedingExtraction.Count, forceReExtract);
                
                // 批量提取媒体信息
                await _mediaInfoService.BatchExtractMediaInfoAsync(itemsNeedingExtraction, progress, cancellationToken);
                
                // 如果是强制重新提取模式，执行完后自动关闭
                if (forceReExtract && config != null)
                {
                    config.ForceReExtractMediaInfo = false;
                    Jellyfin.JellyfinPlugin.Instance?.SaveConfiguration();
                    _logger.LogInformation("Force re-extract mode has been disabled automatically");
                }
                
                _logger.LogInformation("Media info extraction completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during media info extraction");
                throw;
            }
        }
        
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 每小时自动检查一次新媒体
            // 注意：Jellyfin 不支持库扫描后触发，所以使用定时触发
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(1).Ticks
                }
            };
        }
    }
}
