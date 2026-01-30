using System;
using Jellyfin.Data.Enums;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Jellyfin.Services;

namespace StrmAssistant.Jellyfin.ScheduledTasks
{
    /// <summary>
    /// 多版本合并计划任务
    /// </summary>
    public class MergeVersionTask : IScheduledTask
    {
        private readonly MergeVersionService _mergeVersionService;
        private readonly ILogger<MergeVersionTask> _logger;
        
        public string Name => "合并多版本电影";
        
        public string Key => "MergeMultiVersion";
        
        public string Description => "自动检测并合并同目录下的同名电影的不同版本（如 4K、1080p 等）";
        
        public string Category => "StrmAssistant";
        
        public MergeVersionTask(MergeVersionService mergeVersionService, ILogger<MergeVersionTask> logger)
        {
            _mergeVersionService = mergeVersionService;
            _logger = logger;
        }
        
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting multi-version merge task");
            
            try
            {
                var mergedCount = await _mergeVersionService.MergeDuplicateMoviesAsync(
                    progress,
                    cancellationToken);
                
                _logger.LogInformation($"Multi-version merge completed: {mergedCount} groups merged");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in multi-version merge task: {ex.Message}");
                throw;
            }
        }
        
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 默认在媒体库扫描后执行
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerInterval,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks
                }
            };
        }
    }
}
