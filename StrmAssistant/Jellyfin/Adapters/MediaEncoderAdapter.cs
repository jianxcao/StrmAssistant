using System;
using System.IO;
using Jellyfin.Data.Enums;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace StrmAssistant.Jellyfin.Adapters
{
    /// <summary>
    /// 媒体编码器适配器 - 统一媒体信息提取接口
    /// </summary>
    public class MediaEncoderAdapter
    {
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger<MediaEncoderAdapter> _logger;
        
        public MediaEncoderAdapter(IMediaEncoder mediaEncoder, ILogger<MediaEncoderAdapter> logger)
        {
            _mediaEncoder = mediaEncoder;
            _logger = logger;
        }
        
        /// <summary>
        /// 提取媒体信息（使用 FFProbe）
        /// </summary>
        public async Task<MediaSourceInfo> ExtractMediaInfoAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            try
            {
                var path = item.Path;
                if (string.IsNullOrEmpty(path))
                {
                    _logger.LogWarning("Item {ItemName} has no valid path", item.Name);
                    return null;
                }
                
                // 处理 .strm 文件：读取实际媒体 URL/路径
                string actualMediaPath = path;
                MediaProtocol protocol = MediaProtocol.File;
                
                if (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Detected .strm file: {Path}, reading actual media URL", path);
                    
                    try
                    {
                        // 读取 .strm 文件内容（通常只有一行 URL 或路径）
                        var strmContent = await File.ReadAllTextAsync(path, cancellationToken);
                        var mediaUrl = strmContent.Trim();
                        
                        if (string.IsNullOrWhiteSpace(mediaUrl))
                        {
                            _logger.LogWarning(".strm file is empty: {Path}", path);
                            return null;
                        }
                        
                        // 判断是 URL 还是本地路径
                        if (mediaUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            mediaUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                            mediaUrl.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
                            mediaUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                        {
                            actualMediaPath = mediaUrl;
                            protocol = MediaProtocol.Http;
                            _logger.LogDebug("Using HTTP protocol for URL: {Url}", mediaUrl);
                        }
                        else if (File.Exists(mediaUrl))
                        {
                            actualMediaPath = mediaUrl;
                            protocol = MediaProtocol.File;
                            _logger.LogDebug("Using file protocol for path: {Path}", mediaUrl);
                        }
                        else
                        {
                            // 尝试相对路径（相对于 .strm 文件所在目录）
                            var strmDirectory = Path.GetDirectoryName(path);
                            var relativePath = Path.Combine(strmDirectory ?? "", mediaUrl);
                            
                            if (File.Exists(relativePath))
                            {
                                actualMediaPath = relativePath;
                                protocol = MediaProtocol.File;
                                _logger.LogDebug("Using relative file path: {Path}", relativePath);
                            }
                            else
                            {
                                _logger.LogWarning("Media file not found for .strm: {StrmPath}, content: {MediaUrl}", path, mediaUrl);
                                // 仍然尝试使用原始 URL（可能是网络路径）
                                actualMediaPath = mediaUrl;
                                protocol = MediaProtocol.Http;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to read .strm file: {Path}", path);
                        return null;
                    }
                }
                
                // 创建媒体信息请求
                var request = new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo
                    {
                        Path = actualMediaPath,
                        Protocol = protocol,
                        Id = item.Id.ToString("N")
                    },
                    MediaType = DlnaProfileType.Video
                };
                
                // 使用 IMediaEncoder 获取媒体信息
                var mediaInfo = await _mediaEncoder.GetMediaInfo(request, cancellationToken);
                
                _logger.LogDebug("Extracted media info for {ItemName}: {StreamCount} streams", 
                    item.Name, mediaInfo.MediaStreams?.Count ?? 0);
                
                return mediaInfo;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract media info for {ItemName}", item.Name);
                return null;
            }
        }
        
        /// <summary>
        /// 获取 FFProbe 路径
        /// </summary>
        public string GetFFProbePath()
        {
            return _mediaEncoder.ProbePath;
        }
        
        /// <summary>
        /// 检查编码器是否可用
        /// </summary>
        public bool IsAvailable()
        {
            try
            {
                return !string.IsNullOrEmpty(_mediaEncoder.EncoderPath);
            }
            catch
            {
                return false;
            }
        }
    }
}
