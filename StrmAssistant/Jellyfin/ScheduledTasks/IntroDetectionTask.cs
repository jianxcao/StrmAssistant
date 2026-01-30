using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Adapters;
using StrmAssistant.Jellyfin.Services;

namespace StrmAssistant.Jellyfin.ScheduledTasks
{
    /// <summary>
    /// 片头检测计划任务
    /// </summary>
    public class IntroDetectionTask : IScheduledTask
    {
        private readonly IntroDetectionService _introDetectionService;
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly ILogger<IntroDetectionTask> _logger;
        
        public string Name => "片头片尾检测";
        
        public string Key => "DetectIntros";
        
        public string Description => "检测剧集的片头片尾位置并标记";
        
        public string Category => "StrmAssistant";
        
        public IntroDetectionTask(
            IntroDetectionService introDetectionService,
            LibraryManagerAdapter libraryManager,
            ILogger<IntroDetectionTask> logger)
        {
            _introDetectionService = introDetectionService;
            _libraryManager = libraryManager;
            _logger = logger;
        }
        
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting intro detection task");
            
            try
            {
                // 检查配置
                var config = JellyfinPlugin.Instance?.Configuration;
                if (config == null || !config.EnableIntroDetection)
                {
                    _logger.LogInformation("Intro detection is disabled in configuration");
                    return;
                }
                
                // 获取所有剧集
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    Recursive = true,
                    IsVirtualItem = false
                };
                
                _logger.LogDebug("Querying for series with recursive search");
                
                var series = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var seriesList = series.OfType<Series>().ToList();
                
                _logger.LogInformation("Found {Count} series for intro detection (Total items: {Total})", 
                    seriesList.Count, series.Count);
                
                // 如果没有找到剧集，尝试诊断问题
                if (seriesList.Count == 0)
                {
                    _logger.LogWarning("⚠️ No series found. Diagnosing...");
                    
                    // 检查是否有任何媒体库
                    var allItemsQuery = new InternalItemsQuery
                    {
                        Recursive = true
                    };
                    var allItems = await _libraryManager.GetItemsAsync(allItemsQuery, cancellationToken);
                    _logger.LogInformation("Total items in all libraries: {Count}", allItems.Count);
                    
                    // 检查是否有剧集项
                    var episodeQuery = new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { BaseItemKind.Episode },
                        Recursive = true,
                        IsVirtualItem = false
                    };
                    var episodes = await _libraryManager.GetItemsAsync(episodeQuery, cancellationToken);
                    _logger.LogInformation("Total episodes found: {Count}", episodes.Count);
                    
                    if (episodes.Count > 0)
                    {
                        _logger.LogInformation("📺 Found {Count} episodes but no series. This might indicate a library structure issue.", episodes.Count);
                        _logger.LogInformation("💡 Suggestion: Make sure your TV shows are organized in the standard Jellyfin structure:");
                        _logger.LogInformation("   📁 TV Shows/");
                        _logger.LogInformation("      📁 Show Name/");
                        _logger.LogInformation("         📁 Season 01/");
                        _logger.LogInformation("            📄 S01E01.mkv");
                    }
                    else
                    {
                        _logger.LogInformation("❌ No episodes found in any library.");
                        _logger.LogInformation("💡 Suggestion: Add TV shows to your Jellyfin library first.");
                    }
                    
                    return;
                }
                
                var totalProcessed = 0;
                
                for (var i = 0; i < seriesList.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var currentSeries = seriesList[i];
                    _logger.LogInformation("Processing series: {SeriesName}", currentSeries.Name);
                    
                    // 获取所有季 - 使用 InternalItemsQuery 替代 GetRecursiveChildren()
                    var seasonQuery = new InternalItemsQuery
                    {
                        ParentId = currentSeries.Id,
                        IncludeItemTypes = new[] { BaseItemKind.Season },
                        Recursive = false
                    };
                    
                    var seasons = await _libraryManager.GetItemsAsync(seasonQuery, cancellationToken);
                    var seasonList = seasons.OfType<Season>().ToList();
                    
                    foreach (var season in seasonList)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;
                        
                        var processed = await _introDetectionService.BatchDetectIntrosAsync(season, null, cancellationToken);
                        totalProcessed += processed;
                    }
                    
                    progress?.Report((double)(i + 1) / seriesList.Count * 100);
                }
                
                _logger.LogInformation("Intro detection completed. Processed {Count} episodes", totalProcessed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during intro detection");
                throw;
            }
        }
        
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 每天凌晨 3 点自动运行
            // 注意：Jellyfin 不支持库扫描后触发，所以使用每日定时触发
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }
    }
}
