using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using Microsoft.Extensions.Logging;

namespace StrmAssistant.Jellyfin.Adapters
{
    /// <summary>
    /// Jellyfin 版本检测器
    /// </summary>
    public class JellyfinVersionDetector
    {
        private readonly ILogger<JellyfinVersionDetector> _logger;
        private readonly Version _serverVersion;
        
        public JellyfinVersionDetector(IApplicationHost applicationHost, ILogger<JellyfinVersionDetector> logger)
        {
            _logger = logger;
            _serverVersion = applicationHost.ApplicationVersion;
            _logger.LogInformation("Detected Jellyfin version: {Version}", _serverVersion);
        }
        
        /// <summary>
        /// 检查是否满足最低版本要求
        /// </summary>
        public bool IsVersionAtLeast(int major, int minor, int build = 0)
        {
            var requiredVersion = new Version(major, minor, build);
            return _serverVersion >= requiredVersion;
        }
        
        /// <summary>
        /// 检查是否支持 MediaSegment API (10.10.0+)
        /// </summary>
        public bool SupportsMediaSegments => IsVersionAtLeast(10, 10, 0);
        
        /// <summary>
        /// 检查是否支持增强的媒体信息 API
        /// </summary>
        public bool SupportsEnhancedMediaInfo => IsVersionAtLeast(10, 8, 0);
        
        /// <summary>
        /// 检查是否支持自定义搜索引擎
        /// </summary>
        public bool SupportsCustomSearchEngine => IsVersionAtLeast(10, 9, 0);
        
        /// <summary>
        /// 获取当前版本字符串
        /// </summary>
        public string GetVersionString() => _serverVersion.ToString();
    }
}
