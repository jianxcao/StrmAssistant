using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Adapters;

namespace StrmAssistant.Jellyfin.Services
{
    /// <summary>
    /// 媒体信息提取服务 - 使用 FFProbe 提取和持久化媒体流信息
    /// </summary>
    public class MediaInfoService
    {
        private readonly MediaEncoderAdapter _mediaEncoder;
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly IMediaStreamRepository _mediaStreamRepository;
        private readonly ILogger<MediaInfoService> _logger;
        
        public MediaInfoService(
            MediaEncoderAdapter mediaEncoder,
            LibraryManagerAdapter libraryManager,
            IMediaStreamRepository mediaStreamRepository,
            ILogger<MediaInfoService> logger)
        {
            _mediaEncoder = mediaEncoder;
            _libraryManager = libraryManager;
            _mediaStreamRepository = mediaStreamRepository;
            _logger = logger;
        }
        
        /// <summary>
        /// 提取并持久化媒体信息
        /// </summary>
        public async Task<bool> ExtractAndPersistMediaInfoAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Extracting media info for: {ItemName}", item.Name);
                
                // 1. 检查是否可以从 JSON 文件加载（智能缓存）
                var config = JellyfinPlugin.Instance?.Configuration;
                if (config != null && config.EnableMediaInfoPersistence)
                {
                    var jsonPath = GetMediaInfoJsonPath(item, config.MediaInfoJsonRootFolder);
                    if (File.Exists(jsonPath))
                    {
                        // 检查文件内容是否变化（使用哈希值，避免文件重新创建但内容相同的情况）
                        var currentFileHash = await CalculateFileContentHashAsync(item.Path);
                        var cachedInfo = await LoadCachedMediaInfoAsync(item, jsonPath, cancellationToken);
                        
                        if (cachedInfo != null && cachedInfo.FileContentHash == currentFileHash)
                        {
                            _logger.LogDebug("JSON cache exists and file content unchanged for {ItemName}, skipping extraction", item.Name);
                            
                            // 保存到数据库
                            await SaveMediaStreamsToDatabaseAsync(item, cachedInfo.MediaInfo, cancellationToken);
                            
                            // 更新 Item 元数据
                            await _libraryManager.UpdateItemAsync(item, ItemUpdateType.MetadataEdit, cancellationToken);
                            
                            _logger.LogInformation("Loaded media info from JSON cache for {ItemName}", item.Name);
                            return true;
                        }
                        else if (cachedInfo != null)
                        {
                            _logger.LogDebug("JSON cache exists but file content has changed (hash mismatch), will re-extract");
                        }
                    }
                }
                
                // 2. 使用 FFProbe 提取媒体信息
                var mediaInfo = await _mediaEncoder.ExtractMediaInfoAsync(item, cancellationToken);
                if (mediaInfo == null)
                {
                    _logger.LogWarning("Failed to extract media info for {ItemName}", item.Name);
                    return false;
                }
                
                // 3. 保存媒体流信息到数据库
                if (mediaInfo.MediaStreams != null && mediaInfo.MediaStreams.Any())
                {
                    await SaveMediaStreamsToDatabaseAsync(item, mediaInfo, cancellationToken);
                    
                    // 4. 保存到 JSON 文件（如果启用，包含文件内容哈希）
                    await SaveMediaInfoToJsonAsync(item, mediaInfo, cancellationToken);
                    
                    // 5. 更新 Item 元数据
                    await _libraryManager.UpdateItemAsync(item, ItemUpdateType.MetadataEdit, cancellationToken);
                    
                    _logger.LogInformation("Successfully extracted {Count} streams for {ItemName}", 
                        mediaInfo.MediaStreams.Count, item.Name);
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting media info for {ItemName}", item.Name);
                return false;
            }
        }
        
        /// <summary>
        /// 批量提取媒体信息（支持并发处理）
        /// </summary>
        public async Task<int> BatchExtractMediaInfoAsync(
            IEnumerable<BaseItem> items,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            var itemList = items.ToList();
            var totalCount = itemList.Count;
            
            if (totalCount == 0)
            {
                _logger.LogInformation("No items to process");
                return 0;
            }
            
            // 从配置中获取并发数
            var config = JellyfinPlugin.Instance?.Configuration;
            var concurrency = config?.MediaInfoConcurrency ?? 2;
            
            // 确保并发数在合理范围内（1-10）
            concurrency = Math.Max(1, Math.Min(10, concurrency));
            
            _logger.LogInformation("Starting batch media info extraction for {Count} items with concurrency: {Concurrency}", 
                totalCount, concurrency);
            
            // 使用线程安全的计数器
            var successCount = 0;
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
                        var success = await ExtractAndPersistMediaInfoAsync(item, ct);
                        
                        lock (lockObject)
                        {
                            if (success)
                                successCount++;
                            
                            processedCount++;
                            var progressPercent = (double)processedCount / totalCount * 100;
                            progress?.Report(progressPercent);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing item {ItemName} in batch extraction", item.Name);
                        
                        lock (lockObject)
                        {
                            processedCount++;
                            var progressPercent = (double)processedCount / totalCount * 100;
                            progress?.Report(progressPercent);
                        }
                    }
                });
            
            _logger.LogInformation("Batch extraction completed: {Success}/{Total} successful (Concurrency: {Concurrency})", 
                successCount, totalCount, concurrency);
            return successCount;
        }
        
        /// <summary>
        /// 检查是否需要提取媒体信息
        /// </summary>
        public bool NeedsExtraction(BaseItem item, bool forceReExtract = false)
        {
            // 如果强制重新提取，直接返回 true
            if (forceReExtract)
            {
                _logger.LogDebug("Force re-extract enabled for {ItemName}", item.Name);
                return true;
            }
            
            // 注意：JSON 缓存检查在 ExtractAndPersistMediaInfoAsync 中进行
            // 这里只检查数据库中的流信息，避免在 LINQ 中使用异步方法
            
            // Jellyfin 10.11+ GetMediaStreams() 返回 IReadOnlyList<MediaStream>
            var dbStreams = item.GetMediaStreams();
            
            // 如果没有媒体流信息，则需要提取
            if (dbStreams == null || !dbStreams.Any())
                return true;
            
            // 如果是视频但没有视频流，需要重新提取
            if (item is Video && !dbStreams.Any(s => s.Type == MediaStreamType.Video))
                return true;
            
            return false;
        }
        
        /// <summary>
        /// 缓存的媒体信息（包含文件内容哈希）
        /// </summary>
        private class CachedMediaInfo
        {
            public string FileContentHash { get; set; }
            public DateTime FileLastWriteTime { get; set; }
            public MediaBrowser.Model.Dto.MediaSourceInfo MediaInfo { get; set; }
            
            /// <summary>
            /// 片头信息（由 IntroDetectionService 填充）
            /// </summary>
            public IntroInfo Intro { get; set; }
        }
        
        /// <summary>
        /// 片头信息
        /// </summary>
        public class IntroInfo
        {
            public bool HasIntro { get; set; }
            public double IntroStartSeconds { get; set; }
            public double IntroEndSeconds { get; set; }
            public double Confidence { get; set; }
            public DateTime DetectedAt { get; set; }
        }
        
        /// <summary>
        /// 保存媒体信息到 JSON 文件（包含文件内容哈希）
        /// </summary>
        private async Task SaveMediaInfoToJsonAsync(BaseItem item, MediaBrowser.Model.Dto.MediaSourceInfo mediaInfo, CancellationToken cancellationToken)
        {
            try
            {
                // 检查是否启用了 JSON 持久化
                var config = JellyfinPlugin.Instance?.Configuration;
                if (config == null || !config.EnableMediaInfoPersistence)
                {
                    _logger.LogDebug("JSON persistence is disabled, skipping JSON file creation");
                    return;
                }
                
                // 生成 JSON 文件路径
                var jsonPath = GetMediaInfoJsonPath(item, config.MediaInfoJsonRootFolder);
                
                // 确保目录存在
                var directory = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}", directory);
                }
                
                // 计算文件内容哈希
                var fileHash = await CalculateFileContentHashAsync(item.Path);
                var fileInfo = new FileInfo(item.Path);
                
                // 创建缓存对象（包含哈希和媒体信息）
                var cachedInfo = new CachedMediaInfo
                {
                    FileContentHash = fileHash,
                    FileLastWriteTime = fileInfo.LastWriteTime,
                    MediaInfo = mediaInfo
                };
                
                // 序列化到 JSON
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                
                var jsonContent = JsonSerializer.Serialize(cachedInfo, jsonOptions);
                await File.WriteAllTextAsync(jsonPath, jsonContent, cancellationToken);
                
                _logger.LogInformation("Saved media info to JSON (hash: {Hash}): {JsonPath}", fileHash.Substring(0, 8), jsonPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save media info to JSON for {ItemName}", item.Name);
            }
        }
        
        /// <summary>
        /// 保存媒体流信息到数据库
        /// </summary>
        private Task SaveMediaStreamsToDatabaseAsync(BaseItem item, MediaBrowser.Model.Dto.MediaSourceInfo mediaInfo, CancellationToken cancellationToken)
        {
            try
            {
                // Jellyfin 10.11+ 使用 IMediaStreamRepository.SaveMediaStreams
                // 方法签名: void SaveMediaStreams(Guid id, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken)
                _mediaStreamRepository.SaveMediaStreams(item.Id, mediaInfo.MediaStreams, cancellationToken);
                
                _logger.LogDebug("Successfully saved {Count} MediaStreams for {ItemName}", 
                    mediaInfo.MediaStreams.Count, item.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save MediaStreams for {ItemName}", item.Name);
            }
            
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// 获取媒体信息 JSON 路径
        /// </summary>
        public string GetMediaInfoJsonPath(BaseItem item)
        {
            var config = JellyfinPlugin.Instance?.Configuration;
            if (config == null || !config.EnableMediaInfoPersistence)
            {
                return null;
            }
            
            return GetMediaInfoJsonPath(item, config.MediaInfoJsonRootFolder);
        }
        
        /// <summary>
        /// 读取媒体信息 JSON 中的片头信息
        /// </summary>
        public async Task<IntroInfo> GetIntroInfoFromJsonAsync(BaseItem item, CancellationToken cancellationToken = default)
        {
            try
            {
                var jsonPath = GetMediaInfoJsonPath(item);
                if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
                {
                    return null;
                }
                
                var cachedInfo = await LoadCachedMediaInfoAsync(item, jsonPath, cancellationToken);
                return cachedInfo?.Intro;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get intro info from JSON for {ItemName}", item.Name);
                return null;
            }
        }
        
        /// <summary>
        /// 更新媒体信息 JSON 中的片头信息
        /// </summary>
        public async Task<bool> UpdateIntroInfoInJsonAsync(BaseItem item, IntroInfo introInfo, CancellationToken cancellationToken = default)
        {
            try
            {
                var jsonPath = GetMediaInfoJsonPath(item);
                if (string.IsNullOrEmpty(jsonPath))
                {
                    _logger.LogDebug("Media info persistence not enabled, skipping intro info update");
                    return false;
                }
                
                // 如果 JSON 文件不存在，先提取媒体信息
                if (!File.Exists(jsonPath))
                {
                    _logger.LogInformation("Media info JSON not found, extracting media info first for {ItemName}", item.Name);
                    var success = await ExtractAndPersistMediaInfoAsync(item, cancellationToken);
                    if (!success)
                    {
                        _logger.LogWarning("Failed to extract media info for {ItemName}", item.Name);
                        return false;
                    }
                }
                
                // 读取现有 JSON
                var cachedInfo = await LoadCachedMediaInfoAsync(item, jsonPath, cancellationToken);
                if (cachedInfo == null)
                {
                    _logger.LogWarning("Failed to load cached media info for {ItemName}", item.Name);
                    return false;
                }
                
                // 更新片头信息
                cachedInfo.Intro = introInfo;
                
                // 保存回 JSON
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                
                var jsonContent = JsonSerializer.Serialize(cachedInfo, jsonOptions);
                await File.WriteAllTextAsync(jsonPath, jsonContent, cancellationToken);
                
                _logger.LogInformation("Updated intro info in JSON for {ItemName}: {HasIntro}", 
                    item.Name, introInfo.HasIntro);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update intro info in JSON for {ItemName}", item.Name);
                return false;
            }
        }
        
        /// <summary>
        /// 从 JSON 文件加载缓存的媒体信息（包含哈希）
        /// </summary>
        private async Task<CachedMediaInfo> LoadCachedMediaInfoAsync(BaseItem item, string jsonPath, CancellationToken cancellationToken)
        {
            try
            {
                if (!File.Exists(jsonPath))
                {
                    return null;
                }
                
                var jsonContent = await File.ReadAllTextAsync(jsonPath, cancellationToken);
                
                // 尝试加载新的缓存格式（包含哈希）
                try
                {
                    var cachedInfo = JsonSerializer.Deserialize<CachedMediaInfo>(jsonContent);
                    if (cachedInfo != null && cachedInfo.MediaInfo != null && cachedInfo.MediaInfo.MediaStreams != null && cachedInfo.MediaInfo.MediaStreams.Any())
                    {
                        _logger.LogDebug("Successfully loaded cached media info (hash: {Hash}) from JSON for {ItemName}", 
                            cachedInfo.FileContentHash?.Substring(0, 8) ?? "unknown", item.Name);
                        return cachedInfo;
                    }
                }
                catch
                {
                    // 如果解析失败，可能是旧格式的 JSON（只有 MediaSourceInfo）
                    // 尝试向后兼容
                    try
                    {
                        var mediaInfo = JsonSerializer.Deserialize<MediaBrowser.Model.Dto.MediaSourceInfo>(jsonContent);
                        if (mediaInfo != null && mediaInfo.MediaStreams != null && mediaInfo.MediaStreams.Any())
                        {
                            _logger.LogDebug("Loaded legacy format JSON (no hash), will calculate hash for comparison");
                            // 计算当前文件的哈希，创建缓存对象
                            var fileHash = await CalculateFileContentHashAsync(item.Path);
                            return new CachedMediaInfo
                            {
                                FileContentHash = fileHash,
                                FileLastWriteTime = new FileInfo(item.Path).LastWriteTime,
                                MediaInfo = mediaInfo
                            };
                        }
                    }
                    catch
                    {
                        // 忽略解析错误
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached media info from JSON file: {JsonPath}", jsonPath);
                return null;
            }
        }
        
        /// <summary>
        /// 计算文件内容哈希值（用于检测文件内容是否变化）
        /// </summary>
        private async Task<string> CalculateFileContentHashAsync(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return string.Empty;
                }
                
                // 对于 .strm 文件，读取其内容（URL/路径）来计算哈希
                // 对于普通媒体文件，使用文件大小和修改时间的组合（避免读取大文件）
                if (filePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    // .strm 文件通常很小，直接读取内容
                    var content = await File.ReadAllTextAsync(filePath);
                    using var sha256 = SHA256.Create();
                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
                    return Convert.ToHexString(hashBytes);
                }
                else
                {
                    // 对于普通媒体文件，使用文件大小 + 修改时间 + 文件名的组合
                    // 这样可以避免读取大文件，同时能检测到文件变化
                    var fileInfo = new FileInfo(filePath);
                    var hashInput = $"{fileInfo.Length}_{fileInfo.LastWriteTime:O}_{Path.GetFileName(filePath)}";
                    using var sha256 = SHA256.Create();
                    var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
                    return Convert.ToHexString(hashBytes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate file hash for {FilePath}", filePath);
                // 如果计算失败，返回空字符串，这样会触发重新提取
                return string.Empty;
            }
        }
        
        /// <summary>
        /// 获取媒体信息 JSON 文件路径
        /// </summary>
        private string GetMediaInfoJsonPath(BaseItem item, string jsonRootFolder)
        {
            const string MediaInfoFileExtension = "-mediainfo.json";
            
            var relativePath = item.ContainingFolderPath;
            if (!string.IsNullOrEmpty(jsonRootFolder) && Path.IsPathRooted(item.ContainingFolderPath))
            {
                relativePath = Path.GetRelativePath(Path.GetPathRoot(item.ContainingFolderPath)!, 
                    item.ContainingFolderPath);
            }
            
            var mediaInfoJsonPath = !string.IsNullOrEmpty(jsonRootFolder)
                ? Path.Combine(jsonRootFolder, relativePath, item.FileNameWithoutExtension + MediaInfoFileExtension)
                : Path.Combine(item.ContainingFolderPath!, item.FileNameWithoutExtension + MediaInfoFileExtension);
            
            return mediaInfoJsonPath;
        }
    }
}
