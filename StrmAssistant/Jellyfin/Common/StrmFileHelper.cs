using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace StrmAssistant.Jellyfin.Common
{
    /// <summary>
    /// STRM 文件处理工具类
    /// 提供统一的 .strm 文件解析逻辑
    /// </summary>
    public static class StrmFileHelper
    {
        /// <summary>
        /// 获取实际的媒体路径
        /// 对于 .strm 文件，读取文件内容获取实际的媒体 URL 或路径
        /// 对于普通文件，直接返回原路径
        /// </summary>
        /// <param name="item">媒体项</param>
        /// <param name="logger">日志记录器</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>实际的媒体路径或 URL</returns>
        public static async Task<string> GetActualMediaPathAsync(
            BaseItem item, 
            ILogger logger = null,
            CancellationToken cancellationToken = default)
        {
            if (item == null || string.IsNullOrEmpty(item.Path))
            {
                return null;
            }
            
            // 如果不是 .strm 文件，直接返回原路径
            if (!item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return item.Path;
            }
            
            try
            {
                // 读取 .strm 文件内容
                var strmContent = await File.ReadAllTextAsync(item.Path, cancellationToken);
                var mediaUrl = strmContent.Trim();
                
                if (string.IsNullOrWhiteSpace(mediaUrl))
                {
                    logger?.LogWarning("STRM file is empty: {Path}", item.Path);
                    return null;
                }
                
                logger?.LogDebug("STRM file content: {Content}", mediaUrl);
                
                // 如果是 HTTP/RTMP/RTSP URL，直接返回
                if (IsStreamingUrl(mediaUrl))
                {
                    return mediaUrl;
                }
                
                // 如果是本地文件路径，尝试解析
                return ResolveLocalPath(mediaUrl, item.Path, logger);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to read STRM file: {Path}", item.Path);
                return null;
            }
        }
        
        /// <summary>
        /// 判断是否为流媒体 URL
        /// </summary>
        public static bool IsStreamingUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }
            
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// 解析本地文件路径（绝对或相对）
        /// </summary>
        private static string ResolveLocalPath(string mediaPath, string strmFilePath, ILogger logger)
        {
            // 尝试绝对路径
            if (File.Exists(mediaPath))
            {
                logger?.LogDebug("Resolved as absolute path: {Path}", mediaPath);
                return mediaPath;
            }
            
            // 尝试相对于 .strm 文件的相对路径
            var strmDir = Path.GetDirectoryName(strmFilePath);
            if (!string.IsNullOrEmpty(strmDir))
            {
                var relativePath = Path.Combine(strmDir, mediaPath);
                if (File.Exists(relativePath))
                {
                    logger?.LogDebug("Resolved as relative path: {Path}", relativePath);
                    return relativePath;
                }
            }
            
            // 无法解析，返回原始内容
            logger?.LogWarning("Cannot resolve local path from STRM: {MediaPath}", mediaPath);
            return mediaPath;
        }
        
        /// <summary>
        /// 检查是否为 .strm 文件
        /// </summary>
        public static bool IsStrmFile(string path)
        {
            return !string.IsNullOrEmpty(path) && 
                   path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// 检查是否为 .strm 文件
        /// </summary>
        public static bool IsStrmFile(BaseItem item)
        {
            return item != null && IsStrmFile(item.Path);
        }
    }
}
