using System;
using Jellyfin.Data.Enums;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Adapters;
using MediaBrowser.Model.Configuration;

namespace StrmAssistant.Jellyfin.Services
{
    /// <summary>
    /// 片头检测服务 - 使用 Jellyfin 的 MediaSegment API
    /// </summary>
    public class IntroDetectionService
    {
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly ILogger<IntroDetectionService> _logger;
        private readonly JellyfinVersionDetector _versionDetector;
        private readonly IItemRepository _itemRepository;
        private readonly ILibraryManager _libraryManagerCore;
        private readonly IFileSystem _fileSystem;
        private readonly MediaSegmentAdapter _mediaSegmentAdapter;
        private const string MarkerSuffix = "#SA";
        
        public IntroDetectionService(
            LibraryManagerAdapter libraryManager,
            JellyfinVersionDetector versionDetector,
            IItemRepository itemRepository,
            ILibraryManager libraryManagerCore,
            IFileSystem fileSystem,
            MediaSegmentAdapter mediaSegmentAdapter,
            ILogger<IntroDetectionService> logger)
        {
            _libraryManager = libraryManager;
            _versionDetector = versionDetector;
            _itemRepository = itemRepository;
            _libraryManagerCore = libraryManagerCore;
            _fileSystem = fileSystem;
            _mediaSegmentAdapter = mediaSegmentAdapter;
            _logger = logger;
        }
        
        /// <summary>
        /// 检测剧集的片头片尾
        /// </summary>
        public async Task<IntroDetectionResult> DetectIntroAsync(Episode episode, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!_versionDetector.SupportsMediaSegments)
                {
                    _logger.LogWarning("MediaSegment API not supported in this Jellyfin version");
                    return null;
                }
                
                _logger.LogInformation("Detecting intro for episode: {SeriesName} - S{Season:D2}E{Episode:D2}", 
                    episode.SeriesName, episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0);
                
                // TODO: 实现音频指纹提取逻辑
                // 这里需要集成 Chromaprint 库来提取音频指纹
                // 参考 Media Analyzer 插件的实现
                
                var result = new IntroDetectionResult
                {
                    ItemId = episode.Id,
                    HasIntro = false,
                    IntroStart = TimeSpan.Zero,
                    IntroEnd = TimeSpan.Zero
                };
                
                // 如果检测到片头，保存到数据库和 JSON
                if (result.HasIntro)
                {
                    await SaveIntroMarkersAsync(episode, result, cancellationToken);
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting intro for {EpisodeName}", episode.Name);
                return null;
            }
        }
        
        /// <summary>
        /// 批量检测季内所有剧集的片头
        /// </summary>
        public async Task<int> BatchDetectIntrosAsync(
            Season season,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation($"Starting batch intro detection for season: {season.SeriesName} - Season {season.IndexNumber}");
                
                // 获取配置
                var config = JellyfinPlugin.Instance?.Configuration;
                var forceReDetect = config?.ForceReDetectIntro ?? false;
                
                if (forceReDetect)
                {
                    _logger.LogWarning("🔄 Force re-detect is enabled - all episodes will be re-detected");
                }
                
                // 获取季内所有剧集
                var query = new InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode }
                    // OrderBy 在 Jellyfin 10.11 中可能需要不同的类型，暂时移除
                };
                
                var episodes = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var episodeList = episodes.OfType<Episode>().ToList();
                
                var successCount = 0;
                var skippedCount = 0;
                
                for (var i = 0; i < episodeList.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var episode = episodeList[i];
                    
                    // 如果不是强制重新检测，先检查是否已经有检测结果
                    if (!forceReDetect && await HasIntroDetectionResultAsync(episode, cancellationToken))
                    {
                        _logger.LogDebug("Skipping {EpisodeName} - already has intro detection result", episode.Name);
                        skippedCount++;
                        progress?.Report((double)(i + 1) / episodeList.Count * 100);
                        continue;
                    }
                    
                    var result = await DetectIntroAsync(episode, cancellationToken);
                    if (result != null && result.HasIntro)
                        successCount++;
                    
                    progress?.Report((double)(i + 1) / episodeList.Count * 100);
                }
                
                _logger.LogInformation(
                    "Batch intro detection completed: {SuccessCount}/{TotalCount} intros detected, {SkippedCount} skipped", 
                    successCount, episodeList.Count, skippedCount);
                    
                // 如果启用了强制重新检测，任务完成后自动关闭该选项
                if (forceReDetect && config != null)
                {
                    config.ForceReDetectIntro = false;
                    JellyfinPlugin.Instance?.SaveConfiguration();
                    _logger.LogInformation("✅ Force re-detect option has been automatically disabled");
                }
                
                return successCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch intro detection");
                return 0;
            }
        }
        
        /// <summary>
        /// 保存片头片尾标记到 Jellyfin 数据库和 JSON 文件
        /// </summary>
        private async Task SaveIntroMarkersAsync(Episode episode, IntroDetectionResult result, CancellationToken cancellationToken)
        {
            try
            {
                // 1. 保存到 Jellyfin 数据库（章节）
                await SaveChaptersToDatabaseAsync(episode, result, cancellationToken);
                
                // 2. 保存到 JSON 文件
                await SaveIntroMarkersToJsonAsync(episode, result, cancellationToken);
                
                _logger.LogInformation("Successfully saved intro markers for {EpisodeName}", episode.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save intro markers for {EpisodeName}", episode.Name);
            }
        }
        
        /// <summary>
        /// 保存片头片尾到 Jellyfin 数据库（使用 MediaSegment API）
        /// Jellyfin 10.10+ 使用 MediaSegment API 来标记片头片尾
        /// 这样 Jellyfin 的播放器就能显示"跳过片头"按钮
        /// </summary>
        private async Task SaveChaptersToDatabaseAsync(Episode episode, IntroDetectionResult result, CancellationToken cancellationToken)
        {
            try
            {
                // 检查 MediaSegment API 是否可用
                if (!_mediaSegmentAdapter.IsSupported)
                {
                    _logger.LogWarning(
                        "MediaSegment API is not available. Intro/outro segments will only be saved to JSON. " +
                        "Please upgrade to Jellyfin 10.10+ for full intro skip support.");
                    return;
                }
                
                _logger.LogInformation("Creating MediaSegments for {ItemName} using Jellyfin API", episode.Name);
                
                // 先删除旧的片头片尾 segments（避免重复）
                await _mediaSegmentAdapter.DeleteSegmentsByTypeAsync(episode.Id, "Intro", cancellationToken);
                
                // 创建新的片头片尾 MediaSegments
                var success = await _mediaSegmentAdapter.CreateIntroOutroSegmentsAsync(
                    episode,
                    result.IntroStart,
                    result.IntroEnd,
                    outroStart: null, // 暂时只处理片头，片尾检测留待后续实现
                    outroEnd: null,
                    cancellationToken);
                
                if (success)
                {
                    _logger.LogInformation(
                        "Successfully created Intro MediaSegment for {ItemName}: {Start} - {End}",
                        episode.Name,
                        result.IntroStart,
                        result.IntroEnd);
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to create MediaSegment for {ItemName}. Intro info is still saved to JSON.",
                        episode.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error creating MediaSegment for {ItemName}. Intro info is still saved to JSON.", 
                    episode.Name);
            }
        }
        
        /// <summary>
        /// 保存片头片尾标记到 JSON 文件
        /// </summary>
        private async Task SaveIntroMarkersToJsonAsync(Episode episode, IntroDetectionResult result, CancellationToken cancellationToken)
        {
            try
            {
                // 检查是否启用了 JSON 持久化
                var config = JellyfinPlugin.Instance?.Configuration;
                if (config == null || !config.EnableMediaInfoPersistence)
                {
                    _logger.LogDebug("JSON persistence is disabled, skipping JSON file update");
                    return;
                }
                
                var jsonPath = GetMediaInfoJsonPath(episode, config.MediaInfoJsonRootFolder);
                
                // 如果 JSON 文件不存在，需要先创建（包含媒体源信息）
                if (!File.Exists(jsonPath))
                {
                    _logger.LogDebug("MediaInfo JSON file does not exist for {EpisodeName}, creating new file", episode.Name);
                    await CreateMediaInfoJsonWithIntroMarkersAsync(episode, result, jsonPath, cancellationToken);
                }
                else
                {
                    // 更新现有 JSON 文件中的章节信息
                    await UpdateIntroMarkersInJsonAsync(episode, result, jsonPath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save intro markers to JSON for {EpisodeName}", episode.Name);
                throw;
            }
        }
        
        /// <summary>
        /// 创建包含片头标记的媒体信息 JSON 文件
        /// </summary>
        private async Task CreateMediaInfoJsonWithIntroMarkersAsync(
            Episode episode, 
            IntroDetectionResult result, 
            string jsonPath, 
            CancellationToken cancellationToken)
        {
            try
            {
                // Jellyfin 的 GetMediaSources 方法签名可能不同
                // 尝试获取媒体源，如果失败则创建简单的章节列表
                var mediaSources = episode.GetMediaSources(false);
                
                var introStartTicks = (long)result.IntroStart.TotalMilliseconds * 10000;
                var introEndTicks = (long)result.IntroEnd.TotalMilliseconds * 10000;
                
                // 注意：Jellyfin 的 ChapterInfo 可能不支持 MarkerType，使用名称来标识
                var introStartChapter = new ChapterInfo
                {
                    Name = "IntroStart" + MarkerSuffix,
                    StartPositionTicks = introStartTicks
                };
                
                var introEndChapter = new ChapterInfo
                {
                    Name = "IntroEnd" + MarkerSuffix,
                    StartPositionTicks = introEndTicks
                };
                
                var chapters = new List<ChapterInfo> { introStartChapter, introEndChapter };
                
                var mediaSourcesWithChapters = mediaSources.Select(mediaSource =>
                    new MediaSourceWithChapters
                    {
                        MediaSourceInfo = mediaSource,
                        Chapters = chapters
                    }).ToList();
                
                // 清理不需要的字段
                foreach (var jsonItem in mediaSourcesWithChapters)
                {
                    jsonItem.MediaSourceInfo.Id = null;
                    jsonItem.MediaSourceInfo.Path = null;
                    
                    // Jellyfin 的 MediaStream 可能没有 Protocol 属性，只处理外部字幕
                    foreach (var subtitle in jsonItem.MediaSourceInfo.MediaStreams.Where(m =>
                                 m.IsExternal && m.Type == MediaStreamType.Subtitle))
                    {
                        if (!string.IsNullOrEmpty(subtitle.Path))
                        {
                            subtitle.Path = _fileSystem.GetFileInfo(subtitle.Path).Name;
                        }
                    }
                    
                    foreach (var chapter in jsonItem.Chapters)
                    {
                        chapter.ImageTag = null;
                    }
                }
                
                // 确保目录存在
                var parentDirectory = Path.GetDirectoryName(jsonPath);
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }
                
                // 序列化到文件
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var jsonContent = JsonSerializer.Serialize(mediaSourcesWithChapters, jsonOptions);
                await File.WriteAllTextAsync(jsonPath, jsonContent, cancellationToken);
                
                _logger.LogInformation("Created MediaInfo JSON with intro markers: {JsonPath}", jsonPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create MediaInfo JSON with intro markers");
                throw;
            }
        }
        
        /// <summary>
        /// 更新 JSON 文件中的片头标记
        /// </summary>
        private async Task UpdateIntroMarkersInJsonAsync(
            Episode episode,
            IntroDetectionResult result,
            string jsonPath,
            CancellationToken cancellationToken)
        {
            try
            {
                // 读取现有 JSON
                var existingJsonContent = await File.ReadAllTextAsync(jsonPath, cancellationToken);
                var mediaSourcesWithChapters = JsonSerializer.Deserialize<List<MediaSourceWithChapters>>(existingJsonContent);
                
                if (mediaSourcesWithChapters == null || !mediaSourcesWithChapters.Any())
                {
                    _logger.LogWarning("MediaInfo JSON file is empty or invalid: {JsonPath}", jsonPath);
                    return;
                }
                
                var introStartTicks = (long)result.IntroStart.TotalMilliseconds * 10000;
                var introEndTicks = (long)result.IntroEnd.TotalMilliseconds * 10000;
                
                // 更新每个媒体源的章节信息
                foreach (var mediaSourceWithChapters in mediaSourcesWithChapters)
                {
                    // 移除旧的片头片尾标记（通过名称匹配，因为 Jellyfin 的 ChapterInfo 可能没有 MarkerType）
                    mediaSourceWithChapters.Chapters.RemoveAll(c =>
                        c.Name != null && (c.Name.Contains("IntroStart") || c.Name.Contains("IntroEnd")));
                    
                    // 添加新的片头片尾标记
                    // 注意：Jellyfin 的 ChapterInfo 可能不支持 MarkerType，使用名称来标识
                    var introStart = new ChapterInfo
                    {
                        Name = "IntroStart" + MarkerSuffix,
                        StartPositionTicks = introStartTicks
                    };
                    mediaSourceWithChapters.Chapters.Add(introStart);
                    
                    var introEnd = new ChapterInfo
                    {
                        Name = "IntroEnd" + MarkerSuffix,
                        StartPositionTicks = introEndTicks
                    };
                    mediaSourceWithChapters.Chapters.Add(introEnd);
                    
                    // 按时间排序
                    mediaSourceWithChapters.Chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                    
                    // 清理 ImageTag
                    foreach (var chapter in mediaSourceWithChapters.Chapters)
                    {
                        chapter.ImageTag = null;
                    }
                }
                
                // 保存回文件
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJsonContent = JsonSerializer.Serialize(mediaSourcesWithChapters, jsonOptions);
                await File.WriteAllTextAsync(jsonPath, updatedJsonContent, cancellationToken);
                
                _logger.LogInformation("Updated intro markers in JSON: {JsonPath}", jsonPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update intro markers in JSON");
                throw;
            }
        }
        
        /// <summary>
        /// 检查剧集是否已经有片头检测结果
        /// </summary>
        private async Task<bool> HasIntroDetectionResultAsync(Episode episode, CancellationToken cancellationToken)
        {
            try
            {
                // 方法 1: 检查 MediaSegment API（如果可用）
                if (_mediaSegmentAdapter.IsSupported)
                {
                    // 尝试通过反射获取 segments
                    // 这里简化处理，如果 MediaSegment API 可用，我们假设已经检测过
                    // 更精确的实现需要调用 GetSegmentsAsync 方法
                    _logger.LogDebug("MediaSegment API is available, checking for existing segments");
                }
                
                // 方法 2: 检查 JSON 文件中是否有片头标记
                var config = JellyfinPlugin.Instance?.Configuration;
                if (config != null && config.EnableMediaInfoPersistence)
                {
                    var jsonPath = GetMediaInfoJsonPath(episode, config.MediaInfoJsonRootFolder ?? string.Empty);
                    if (_fileSystem.FileExists(jsonPath))
                    {
                        try
                        {
                            var jsonContent = await File.ReadAllTextAsync(jsonPath, cancellationToken);
                            var mediaSourcesWithChapters = JsonSerializer.Deserialize<List<MediaSourceWithChapters>>(jsonContent);
                            
                            if (mediaSourcesWithChapters != null)
                            {
                                // 检查是否有片头标记
                                foreach (var mediaSource in mediaSourcesWithChapters)
                                {
                                    if (mediaSource.Chapters != null && 
                                        mediaSource.Chapters.Any(c => c.Name != null && 
                                            (c.Name.Contains("IntroStart") || c.Name.Contains("IntroEnd"))))
                                    {
                                        _logger.LogDebug("Found existing intro markers in JSON for {EpisodeName}", episode.Name);
                                        return true;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to check JSON for intro markers: {JsonPath}", jsonPath);
                        }
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking for existing intro detection result");
                return false; // 出错时认为没有检测过，继续检测
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
        
        /// <summary>
        /// 媒体源与章节信息（用于 JSON 序列化）
        /// </summary>
        private class MediaSourceWithChapters
        {
            public MediaSourceInfo MediaSourceInfo { get; set; }
            public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
            public bool? ZeroFingerprintConfidence { get; set; }
            public string EmbeddedImage { get; set; }
        }
    }
    
    /// <summary>
    /// 片头检测结果
    /// </summary>
    public class IntroDetectionResult
    {
        public Guid ItemId { get; set; }
        public bool HasIntro { get; set; }
        public TimeSpan IntroStart { get; set; }
        public TimeSpan IntroEnd { get; set; }
        public double Confidence { get; set; }
    }
}
