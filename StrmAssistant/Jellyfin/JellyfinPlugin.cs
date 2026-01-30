using System;
using Jellyfin.Data.Enums;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using StrmAssistant.Jellyfin.Adapters;
using StrmAssistant.Jellyfin.Services;

namespace StrmAssistant.Jellyfin
{
    public class JellyfinPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        private readonly ILogger<JellyfinPlugin> _logger;
        private readonly ILibraryManager _libraryManager;
        private MediaInfoService _mediaInfoService;
        private IntroDetectionService _introDetectionService;
        
        public override string Name => "StrmAssistant for Jellyfin";
        
        public override Guid Id => Guid.Parse("63c322b7-a371-41a3-b11f-04f8418b37d9");
        
        public override string Description => "Jellyfin 媒体服务器功能增强插件 - 提供媒体信息提取、片头检测、多版本合并、中文搜索等22个功能";
        
        public static JellyfinPlugin Instance { get; private set; }
        
        public JellyfinPlugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILoggerFactory loggerFactory,
            ILibraryManager libraryManager,
            IServiceProvider serviceProvider)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = loggerFactory.CreateLogger<JellyfinPlugin>();
            _libraryManager = libraryManager;
            
            _logger.LogInformation("StrmAssistant for Jellyfin is initializing...");
            
            // 延迟初始化：等待服务注册完成后再获取服务
            // 使用 Task.Run 避免阻塞构造函数
            _ = Task.Run(() =>
            {
                try
                {
                    // 等待一小段时间确保服务已注册
                    Thread.Sleep(1000);
                    
                    // 从服务提供者获取服务
                    _mediaInfoService = serviceProvider.GetService(typeof(MediaInfoService)) as MediaInfoService;
                    _introDetectionService = serviceProvider.GetService(typeof(IntroDetectionService)) as IntroDetectionService;
                    
                    if (_mediaInfoService != null && _introDetectionService != null)
                    {
                        // 注册事件监听器
                        _libraryManager.ItemAdded += OnItemAdded;
                        _logger.LogInformation("ItemAdded event handler registered for automatic media info extraction and intro detection");
                    }
                    else
                    {
                        _logger.LogWarning("Failed to get required services for event handler registration");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize event handlers");
                }
            });
        }
        
        /// <summary>
        /// 获取 Web 页面
        /// </summary>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "StrmAssistant",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                }
            };
        }
        
        /// <summary>
        /// 处理新项添加事件 - 自动执行媒体信息提取和片头检测
        /// </summary>
        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (_mediaInfoService == null || _introDetectionService == null)
                {
                    // 服务尚未初始化，跳过
                    return;
                }
                
                var config = Instance?.Configuration;
                if (config == null)
                {
                    return;
                }
                
                // 1. 自动提取媒体信息（如果是视频）
                if (e.Item is Video video)
                {
                    // 检查是否启用了媒体信息提取
                    if (config.EnableMediaInfoExtraction)
                    {
                        _logger.LogInformation("Auto-extracting media info for new item: {ItemName}", video.Name);
                        
                        // 异步执行，不阻塞事件处理
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _mediaInfoService.ExtractAndPersistMediaInfoAsync(video, CancellationToken.None);
                                _logger.LogInformation("Auto-extracted media info completed for: {ItemName}", video.Name);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to auto-extract media info for: {ItemName}", video.Name);
                            }
                        });
                    }
                }
                
                // 2. 自动检测片头片尾（如果是剧集）
                if (e.Item is Episode episode)
                {
                    // 检查是否启用了片头检测
                    if (config.EnableIntroDetection)
                    {
                        _logger.LogInformation("Auto-detecting intro for new episode: {SeriesName} - S{Season:D2}E{Episode:D2}", 
                            episode.SeriesName, episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0);
                        
                        // 异步执行，不阻塞事件处理
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var result = await _introDetectionService.DetectIntroAsync(episode, CancellationToken.None);
                                if (result != null && result.HasIntro)
                                {
                                    _logger.LogInformation("Auto-detected intro completed for: {SeriesName} - S{Season:D2}E{Episode:D2}", 
                                        episode.SeriesName, episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to auto-detect intro for: {EpisodeName}", episode.Name);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnItemAdded event handler");
            }
        }
    }
    
    /// <summary>
    /// 插件服务注册器
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            // 注册适配器
            serviceCollection.AddSingleton<JellyfinVersionDetector>();
            serviceCollection.AddSingleton<LibraryManagerAdapter>();
            serviceCollection.AddSingleton<MediaEncoderAdapter>();
            serviceCollection.AddSingleton<MediaSegmentAdapter>();
            
            // 注册服务
            serviceCollection.AddSingleton<MediaInfoService>();
            serviceCollection.AddSingleton<ChromaprintService>();
            serviceCollection.AddSingleton<IntroDetectionService>();
            serviceCollection.AddSingleton<MergeVersionService>();
            serviceCollection.AddSingleton<SubtitleScanService>();
        }
    }
}
