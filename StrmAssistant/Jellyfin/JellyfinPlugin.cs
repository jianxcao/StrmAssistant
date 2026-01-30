using System;
using Jellyfin.Data.Enums;
using System.Collections.Generic;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
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
        
        public override string Name => "StrmAssistant for Jellyfin";
        
        public override Guid Id => Guid.Parse("63c322b7-a371-41a3-b11f-04f8418b37d9");
        
        public override string Description => "Jellyfin 媒体服务器功能增强插件 - 提供媒体信息提取、片头检测、多版本合并、中文搜索等22个功能";
        
        public static JellyfinPlugin Instance { get; private set; }
        
        public JellyfinPlugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILoggerFactory loggerFactory)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = loggerFactory.CreateLogger<JellyfinPlugin>();
            
            _logger.LogInformation("StrmAssistant for Jellyfin is initializing...");
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
            
            // 注册服务
            serviceCollection.AddSingleton<MediaInfoService>();
            serviceCollection.AddSingleton<IntroDetectionService>();
            serviceCollection.AddSingleton<MergeVersionService>();
            serviceCollection.AddSingleton<SubtitleScanService>();
        }
    }
}
