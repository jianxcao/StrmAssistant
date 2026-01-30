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
                
                // 直接获取所有剧集（Episode），不要通过 Series -> Season 层级查询
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                };
                
                _logger.LogDebug("Querying for episodes with recursive search");
                
                var items = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var episodes = items.OfType<Episode>().ToList();
                
                _logger.LogInformation("Found {Count} episodes for intro detection", episodes.Count);
                
                if (episodes.Count == 0)
                {
                    _logger.LogWarning("No episodes found in library");
                    return;
                }
                
                // 按 Series 分组
                var episodesBySeries = episodes.GroupBy(e => e.SeriesId).ToList();
                _logger.LogInformation("Episodes grouped into {Count} series", episodesBySeries.Count);
                
                // 检查是否强制重新检测
                var forceReDetect = config?.ForceReDetectIntro ?? false;
                if (forceReDetect)
                {
                    _logger.LogWarning("🔄 Force re-detect is enabled - all episodes will be re-detected");
                }
                
                var totalProcessed = 0;
                var processedEpisodes = 0;
                
                foreach (var seriesGroup in episodesBySeries)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var firstEpisode = seriesGroup.First();
                    var seriesName = firstEpisode.SeriesName ?? "Unknown";
                    
                    _logger.LogInformation("Processing series: {SeriesName} ({EpisodeCount} episodes)", 
                        seriesName, seriesGroup.Count());
                    
                    // 按季分组
                    var episodesBySeason = seriesGroup.GroupBy(e => e.ParentIndexNumber ?? 0).ToList();
                    
                    foreach (var seasonGroup in episodesBySeason)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;
                        
                        var seasonNumber = seasonGroup.Key;
                        var seasonEpisodes = seasonGroup.ToList();
                        
                        _logger.LogInformation("Processing Season {Season} ({EpisodeCount} episodes)", 
                            seasonNumber, seasonEpisodes.Count);
                        
                        // 检测每一集
                        foreach (var episode in seasonEpisodes)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;
                            
                            processedEpisodes++;
                            
                            var result = await _introDetectionService.DetectIntroAsync(episode, cancellationToken);
                            if (result != null && result.HasIntro)
                            {
                                totalProcessed++;
                            }
                            
                            progress?.Report((double)processedEpisodes / episodes.Count * 100);
                        }
                    }
                }
                
                _logger.LogInformation(
                    "Intro detection completed. Detected intros for {DetectedCount}/{TotalCount} episodes", 
                    totalProcessed, episodes.Count);
                    
                // 如果启用了强制重新检测，任务完成后自动关闭该选项
                if (forceReDetect && config != null)
                {
                    config.ForceReDetectIntro = false;
                    JellyfinPlugin.Instance?.SaveConfiguration();
                    _logger.LogInformation("✅ Force re-detect option has been automatically disabled");
                }
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
