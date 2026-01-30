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
                // 获取所有剧集
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    Recursive = true,
                    IsVirtualItem = false
                };
                
                var series = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var seriesList = series.OfType<Series>().ToList();
                
                _logger.LogInformation("Found {Count} series for intro detection", seriesList.Count);
                
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
            // 在媒体库扫描完成后自动运行
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerLibraryScan
                }
            };
        }
    }
}
