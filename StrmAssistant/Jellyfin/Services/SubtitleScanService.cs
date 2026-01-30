using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Adapters;

namespace StrmAssistant.Jellyfin.Services
{
    /// <summary>
    /// 字幕扫描服务 - 独立扫描外挂字幕
    /// </summary>
    public class SubtitleScanService
    {
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<SubtitleScanService> _logger;
        
        private static readonly string[] SubtitleExtensions = { ".srt", ".ass", ".ssa", ".sub", ".vtt" };
        
        public SubtitleScanService(
            LibraryManagerAdapter libraryManager,
            IMediaStreamRepository mediaStreamRepository,
            IFileSystem fileSystem,
            ILogger<SubtitleScanService> logger)
        {
            _libraryManager = libraryManager;
            _mediaStreamRepository = mediaStreamRepository;
            _fileSystem = fileSystem;
            _logger = logger;
        }
        
        /// <summary>
        /// 扫描视频的外挂字幕
        /// </summary>
        public async Task<int> ScanSubtitlesAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrEmpty(item.Path))
                    return 0;
                
                // 处理 .strm 文件：获取实际媒体文件所在目录
                string actualPath = item.Path;
                if (item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // 读取 .strm 文件内容获取实际媒体路径
                        var strmContent = await File.ReadAllTextAsync(item.Path, cancellationToken);
                        var mediaUrl = strmContent.Trim();
                        
                        // 如果是本地文件路径，使用该路径；否则使用 .strm 文件所在目录
                        if (!string.IsNullOrWhiteSpace(mediaUrl) && 
                            !mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                            !mediaUrl.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) &&
                            !mediaUrl.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
                        {
                            // 尝试绝对路径
                            if (File.Exists(mediaUrl))
                            {
                                actualPath = mediaUrl;
                            }
                            else
                            {
                                // 尝试相对路径
                                var strmDir = Path.GetDirectoryName(item.Path);
                                var relativePath = Path.Combine(strmDir ?? "", mediaUrl);
                                if (File.Exists(relativePath))
                                {
                                    actualPath = relativePath;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to read .strm file {item.Path}: {ex.Message}, using .strm file directory");
                    }
                }
                
                var directory = Path.GetDirectoryName(actualPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    _logger.LogWarning($"Directory not found for {item.Name}: {directory}");
                    return 0;
                }
                
                var videoFileName = Path.GetFileNameWithoutExtension(actualPath);
                
                _logger.LogDebug($"Scanning subtitles for: {item.Name} in {directory}");
                
                // 1. 查找字幕文件
                var subtitleFiles = _fileSystem.GetFiles(directory, SubtitleExtensions, false, false)
                    .Where(f => IsMatchingSubtitle(f.Name, videoFileName))
                    .ToList();
                
                if (!subtitleFiles.Any())
                {
                    _logger.LogDebug($"No external subtitles found for {item.Name}");
                    return 0;
                }
                
                _logger.LogInformation($"Found {subtitleFiles.Count} subtitle files for {item.Name}");
                
                // 2. 从 item 获取现有的媒体流
                // Jellyfin 10.11+ GetMediaStreams() 返回 IReadOnlyList<MediaStream>
                var streams = item.GetMediaStreams();
                var existingStreams = streams?.ToList() ?? new List<MediaStream>();
                
                // 3. 检查哪些字幕文件已经存在（避免重复添加）
                var existingSubtitlePaths = existingStreams
                    .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal)
                    .Select(s => s.Path)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                
                // 4. 过滤掉已存在的字幕文件
                var newSubtitleFiles = subtitleFiles
                    .Where(f => !existingSubtitlePaths.Contains(f.FullName))
                    .ToList();
                
                if (!newSubtitleFiles.Any())
                {
                    _logger.LogDebug($"All subtitle files already exist for {item.Name}");
                    return 0;
                }
                
                // 5. 解析新字幕信息
                var subtitleStreams = newSubtitleFiles.Select(f => ParseSubtitleFile(f, item)).ToList();
                
                // 6. 计算下一个可用的字幕流索引（确保不冲突）
                // 获取所有现有流的索引（包括视频、音频、字幕等）
                var existingIndices = existingStreams
                    .Select(s => s.Index)
                    .ToHashSet();
                
                // 找到第一个可用的索引
                int nextIndex = 0;
                while (existingIndices.Contains(nextIndex))
                {
                    nextIndex++;
                }
                
                // 为新字幕流分配索引
                foreach (var subStream in subtitleStreams)
                {
                    subStream.Index = nextIndex;
                    existingStreams.Add(subStream);
                    existingIndices.Add(nextIndex);
                    
                    // 找到下一个可用索引
                    while (existingIndices.Contains(nextIndex))
                    {
                        nextIndex++;
                    }
                }
                
                // 7. 保存 MediaStreams 到数据库
                await SaveMediaStreamsToDatabaseAsync(item, existingStreams, cancellationToken);
                
                // 8. 持久化
                await _libraryManager.UpdateItemAsync(item, ItemUpdateType.MetadataEdit, cancellationToken);
                
                _logger.LogInformation($"Added {subtitleStreams.Count} new subtitle streams for {item.Name}");
                return subtitleStreams.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning subtitles for {ItemName}", item.Name);
                return 0;
            }
        }
        
        /// <summary>
        /// 批量扫描字幕（支持并发处理）
        /// </summary>
        public async Task<int> BatchScanSubtitlesAsync(
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting batch subtitle scan");
                
                // 获取所有视频项
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    Recursive = true,
                    IsVirtualItem = false
                };
                
                var items = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var itemList = items.ToList();
                var totalCount = itemList.Count;
                
                if (totalCount == 0)
                {
                    _logger.LogInformation("No items to scan for subtitles");
                    return 0;
                }
                
                // 从配置中获取并发数（复用 MediaInfoConcurrency 配置）
                var config = JellyfinPlugin.Instance?.Configuration;
                var concurrency = config?.MediaInfoConcurrency ?? 2;
                
                // 确保并发数在合理范围内（1-10）
                concurrency = Math.Max(1, Math.Min(10, concurrency));
                
                _logger.LogInformation("Starting batch subtitle scan for {Count} items with concurrency: {Concurrency}", 
                    totalCount, concurrency);
                
                // 使用线程安全的计数器
                var scannedCount = 0;
                var processedCount = 0;
                var lockObject = new object();
                
                // 使用 Parallel.ForEachAsync 进行并发处理
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = cancellationToken
                };
                
                await Parallel.ForEachAsync(
                    itemList,
                    parallelOptions,
                    async (item, ct) =>
                    {
                        try
                        {
                            var count = await ScanSubtitlesAsync(item, ct);
                            
                            lock (lockObject)
                            {
                                scannedCount += count;
                                processedCount++;
                                var progressPercent = (double)processedCount / totalCount * 100;
                                progress?.Report(progressPercent);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing item {ItemName} in batch subtitle scan", item.Name);
                            
                            lock (lockObject)
                            {
                                processedCount++;
                                var progressPercent = (double)processedCount / totalCount * 100;
                                progress?.Report(progressPercent);
                            }
                        }
                    });
                
                _logger.LogInformation("Batch subtitle scan completed: {ScannedCount} subtitles found (Concurrency: {Concurrency})", 
                    scannedCount, concurrency);
                return scannedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch subtitle scan: {Message}", ex.Message);
                return 0;
            }
        }
        
        /// <summary>
        /// 判断字幕文件是否匹配视频
        /// </summary>
        private bool IsMatchingSubtitle(string subtitleFileName, string videoFileName)
        {
            var subBaseName = Path.GetFileNameWithoutExtension(subtitleFileName);
            
            // 完全匹配
            if (subBaseName.Equals(videoFileName, StringComparison.OrdinalIgnoreCase))
                return true;
            
            // 带语言后缀匹配：movie.zh.srt, movie.eng.srt
            if (subBaseName.StartsWith(videoFileName, StringComparison.OrdinalIgnoreCase))
                return true;
            
            return false;
        }
        
        /// <summary>
        /// 解析字幕文件信息
        /// </summary>
        private MediaStream ParseSubtitleFile(FileSystemMetadata file, BaseItem item)
        {
            var language = ExtractLanguage(file.Name);
            var codec = Path.GetExtension(file.Name).TrimStart('.').ToLower();
            
            return new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Path = file.FullName,
                IsExternal = true,
                Language = language,
                Codec = codec,
                // IsTextSubtitleStream = IsTextSubtitle(codec)
            };
        }
        
        /// <summary>
        /// 从文件名提取语言代码
        /// </summary>
        private string ExtractLanguage(string fileName)
        {
            var languageMap = new Dictionary<string, string>
            {
                { ".zh", "chi" },
                { ".chs", "chi" },
                { ".cht", "chi" },
                { ".zh-cn", "chi" },
                { ".zh-tw", "chi" },
                { ".eng", "eng" },
                { ".en", "eng" },
                { ".ja", "jpn" },
                { ".jp", "jpn" },
                { ".ko", "kor" },
                { ".kr", "kor" }
            };
            
            foreach (var kvp in languageMap)
            {
                if (fileName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            
            return "und"; // undefined
        }
        
        /// <summary>
        /// 判断是否为文本字幕
        /// </summary>
        private bool IsTextSubtitle(string codec)
        {
            return codec switch
            {
                "srt" => true,
                "ass" => true,
                "ssa" => true,
                "vtt" => true,
                "sub" => false, // 可能是图形字幕
                _ => false
            };
        }
        
        /// <summary>
        /// 保存媒体流信息到数据库
        /// </summary>
        private Task SaveMediaStreamsToDatabaseAsync(BaseItem item, List<MediaStream> streams, CancellationToken cancellationToken)
        {
            try
            {
                // Jellyfin 10.11+ 使用 IMediaStreamRepository.SaveMediaStreams
                // 方法签名: void SaveMediaStreams(Guid id, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken)
                _mediaStreamRepository.SaveMediaStreams(item.Id, streams, cancellationToken);
                
                _logger.LogDebug("Successfully saved {Count} MediaStreams for {ItemName}", 
                    streams.Count, item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save MediaStreams for {ItemName}", item.Name);
            }
            
            return Task.CompletedTask;
        }
    }
}
