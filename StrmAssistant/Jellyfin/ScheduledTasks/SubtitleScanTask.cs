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
    /// 字幕扫描计划任务
    /// </summary>
    public class SubtitleScanTask : IScheduledTask
    {
        private readonly SubtitleScanService _subtitleScanService;
        private readonly ILogger<SubtitleScanTask> _logger;
        
        public string Name => "扫描外挂字幕";
        
        public string Key => "SubtitleScan";
        
        public string Description => "独立扫描视频目录下的外挂字幕文件并关联到媒体项";
        
        public string Category => "StrmAssistant";
        
        public SubtitleScanTask(SubtitleScanService subtitleScanService, ILogger<SubtitleScanTask> logger)
        {
            _subtitleScanService = subtitleScanService;
            _logger = logger;
        }
        
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting subtitle scan task");
            
            try
            {
                var scannedCount = await _subtitleScanService.BatchScanSubtitlesAsync(
                    progress,
                    cancellationToken);
                
                _logger.LogInformation($"Subtitle scan completed: {scannedCount} subtitles found");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in subtitle scan task: {ex.Message}");
                throw;
            }
        }
        
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 每天凌晨 4 点自动运行
            // 注意：Jellyfin 不支持库扫描后触发，所以使用每日定时触发
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }
    }
}
