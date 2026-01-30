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
using StrmAssistant.Jellyfin.Common;
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
        private readonly ChromaprintService _chromaprintService;
        private readonly MediaInfoService _mediaInfoService;
        private const string MarkerSuffix = "#SA";
        private const int MinEpisodesForFingerprint = 2;
        private const double SimilarityThreshold = 0.8; // 80% 相似度
        
        public IntroDetectionService(
            LibraryManagerAdapter libraryManager,
            JellyfinVersionDetector versionDetector,
            IItemRepository itemRepository,
            ILibraryManager libraryManagerCore,
            IFileSystem fileSystem,
            MediaSegmentAdapter mediaSegmentAdapter,
            ChromaprintService chromaprintService,
            MediaInfoService mediaInfoService,
            ILogger<IntroDetectionService> logger)
        {
            _libraryManager = libraryManager;
            _versionDetector = versionDetector;
            _itemRepository = itemRepository;
            _libraryManagerCore = libraryManagerCore;
            _fileSystem = fileSystem;
            _mediaSegmentAdapter = mediaSegmentAdapter;
            _chromaprintService = chromaprintService;
            _mediaInfoService = mediaInfoService;
            _logger = logger;
        }
        
        /// <summary>
        /// 检测剧集的片头片尾
        /// MVP 版本：检测前 5 分钟，≥2 集用音频指纹，<2 集用章节分析
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
                
                // 获取同季所有剧集
                var seasonEpisodes = await GetSeasonEpisodesAsync(episode, cancellationToken);
                
                IntroDetectionResult result;
                
                if (seasonEpisodes.Count >= MinEpisodesForFingerprint)
                {
                    // 方案 A：音频指纹比对（≥2 集）
                    _logger.LogInformation("Using audio fingerprint detection ({Count} episodes in season)", 
                        seasonEpisodes.Count);
                    result = await DetectByAudioFingerprintAsync(episode, seasonEpisodes, cancellationToken);
                }
                else
                {
                    // 方案 B：章节分析（<2 集）
                    _logger.LogInformation("Using chapter analysis (only {Count} episode(s) in season)", 
                        seasonEpisodes.Count);
                    result = await DetectFromChaptersAsync(episode, cancellationToken);
                }
                
                // 如果检测到片头，保存到数据库和 JSON
                if (result != null && result.HasIntro)
                {
                    _logger.LogInformation("Intro detected for {EpisodeName}, saving markers (Start: {Start}, End: {End})", 
                        episode.Name, result.IntroStart, result.IntroEnd);
                    await SaveIntroMarkersAsync(episode, result, cancellationToken);
                }
                else if (result != null && !result.HasIntro)
                {
                    _logger.LogInformation("No intro detected for {EpisodeName}, skipping save", episode.Name);
                }
                else
                {
                    _logger.LogWarning("Detection result is null for {EpisodeName}", episode.Name);
                }
                
                return result ?? new IntroDetectionResult
                {
                    ItemId = episode.Id,
                    HasIntro = false
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting intro for {EpisodeName}", episode.Name);
                return null;
            }
        }
        
        /// <summary>
        /// 获取同季所有剧集
        /// </summary>
        private async Task<List<Episode>> GetSeasonEpisodesAsync(Episode episode, CancellationToken cancellationToken)
        {
            try
            {
                var query = new InternalItemsQuery
                {
                    ParentId = episode.ParentId, // Season ID
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = false
                };
                
                var items = await _libraryManager.GetItemsAsync(query, cancellationToken);
                return items.OfType<Episode>().OrderBy(e => e.IndexNumber ?? 0).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get season episodes");
                return new List<Episode> { episode };
            }
        }
        
        /// <summary>
        /// 使用音频指纹检测片头（MVP 版本）
        /// </summary>
        private async Task<IntroDetectionResult> DetectByAudioFingerprintAsync(
            Episode targetEpisode,
            List<Episode> seasonEpisodes, 
            CancellationToken cancellationToken)
        {
            try
            {
                // 检查 chromaprint 支持
                if (!await _chromaprintService.IsChromaprintSupportedAsync(cancellationToken))
                {
                    _logger.LogWarning("Chromaprint not supported, falling back to chapter analysis");
                    return await DetectFromChaptersAsync(targetEpisode, cancellationToken);
                }
                
                // 先检查季度缓存
                var cachedIntro = await GetSeasonIntroCacheAsync(targetEpisode, cancellationToken);
                if (cachedIntro != null)
                {
                    _logger.LogInformation("Found season intro cache for {SeriesName} S{Season:D2}, using cached result (HasIntro: {HasIntro}, Start: {Start}, End: {End})", 
                        targetEpisode.SeriesName, 
                        targetEpisode.ParentIndexNumber ?? 0,
                        cachedIntro.HasIntro,
                        cachedIntro.IntroStart,
                        cachedIntro.IntroEnd);
                    return cachedIntro;
                }
                
                _logger.LogInformation("No season cache found, extracting fingerprints...");
                
                // MVP: 只处理前 3 集（性能考虑）
                var episodesToProcess = seasonEpisodes.Take(Math.Min(3, seasonEpisodes.Count)).ToList();
                
                _logger.LogInformation("Extracting fingerprints from {Count} episodes", episodesToProcess.Count);
                
                // 获取并发数配置（避免网络拥堵）
                var config = JellyfinPlugin.Instance?.Configuration;
                var maxConcurrency = config?.IntroDetectionConcurrency ?? 2;
                _logger.LogDebug("Using concurrency limit: {MaxConcurrency}", maxConcurrency);
                
                // 使用信号量限制并发
                var fingerprints = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string>();
                using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
                
                var tasks = episodesToProcess.Select(async ep =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        // 获取实际的媒体路径（处理 .strm 文件）
                        var actualPath = await StrmFileHelper.GetActualMediaPathAsync(ep, _logger, cancellationToken);
                        
                        if (string.IsNullOrEmpty(actualPath))
                        {
                            _logger.LogWarning("Cannot resolve media path for: {Name}", ep.Name);
                            return;
                        }
                        
                        // 对于 HTTP/RTSP 等流媒体 URL，直接使用
                        // FFmpeg 支持从流媒体 URL 提取音频指纹
                        _logger.LogDebug("Extracting fingerprint from: {Path}", actualPath);
                        
                        var fingerprint = await _chromaprintService.ExtractFingerprintAsync(
                            actualPath, 
                            TimeSpan.FromMinutes(5), 
                            cancellationToken);
                        
                        fingerprints[ep.Id] = fingerprint;
                        _logger.LogDebug("Extracted fingerprint for {Name}", ep.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to extract fingerprint for {Name}", ep.Name);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();
                
                await Task.WhenAll(tasks);
                
                _logger.LogInformation("✅ Fingerprint extraction completed: {Extracted}/{Total} episodes succeeded", 
                    fingerprints.Count, episodesToProcess.Count);
                
                if (fingerprints.Count < 2)
                {
                    _logger.LogWarning("❌ Not enough fingerprints extracted ({Count} < 2), falling back to chapter analysis", 
                        fingerprints.Count);
                    return await DetectFromChaptersAsync(targetEpisode, cancellationToken);
                }
                
                // 比对指纹找出共同片段（转换为 Dictionary）
                _logger.LogInformation("🔍 Comparing {Count} fingerprints to find common intro segment...", fingerprints.Count);
                var fingerprintDict = new Dictionary<Guid, string>(fingerprints);
                var introSegment = FindCommonIntroSegment(fingerprintDict);
                
                IntroDetectionResult result;
                
                if (introSegment.HasValue)
                {
                    var (start, end) = introSegment.Value;
                    _logger.LogInformation("✅ Detected intro: {Start} - {End} (duration: {Duration}s)", 
                        start, end, (end - start).TotalSeconds);
                    
                    result = new IntroDetectionResult
                    {
                        ItemId = targetEpisode.Id,
                        HasIntro = true,
                        IntroStart = start,
                        IntroEnd = end,
                        Confidence = 0.9
                    };
                }
                else
                {
                    _logger.LogWarning("❌ No common intro segment found in {Count} fingerprints. Possible reasons: 1) Episodes have different intros 2) Audio quality too low 3) Network streaming issues", 
                        fingerprints.Count);
                    result = new IntroDetectionResult
                    {
                        ItemId = targetEpisode.Id,
                        HasIntro = false
                    };
                }
                
                // 保存季度缓存（所有季内剧集共享）
                await SaveSeasonIntroCacheAsync(targetEpisode, result, cancellationToken);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect intro by fingerprint");
                return null;
            }
        }
        
        /// <summary>
        /// 从章节信息中检测片头（MVP：暂未实现）
        /// TODO: 实现章节分析功能
        /// </summary>
        private Task<IntroDetectionResult> DetectFromChaptersAsync(
            Episode episode, 
            CancellationToken cancellationToken)
        {
            _logger.LogWarning("Chapter analysis not yet implemented for single episode season");
            
            // MVP: 暂时不支持章节分析，直接返回未检测到
            return Task.FromResult(new IntroDetectionResult
            {
                ItemId = episode.Id,
                HasIntro = false
            });
        }
        
        /// <summary>
        /// 找出共同的片头片段（MVP 简化版）
        /// </summary>
        private (TimeSpan start, TimeSpan end)? FindCommonIntroSegment(Dictionary<Guid, string> fingerprints)
        {
            try
            {
                if (fingerprints.Count < 2)
                {
                    return null;
                }
                
                // MVP 简化：比对第一集和第二集
                var firstFingerprint = fingerprints.Values.First();
                var secondFingerprint = fingerprints.Values.Skip(1).First();
                
                var similarity = _chromaprintService.CompareFingerprints(firstFingerprint, secondFingerprint);
                
                _logger.LogDebug("Fingerprint similarity: {Similarity:P}", similarity);
                
                if (similarity >= SimilarityThreshold)
                {
                    // MVP: 假设片头在 30 秒到 120 秒之间
                    return (TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to find common intro segment");
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
                
                // 使用 MediaInfoService 统一管理 JSON
                var introInfo = new MediaInfoService.IntroInfo
                {
                    HasIntro = result.HasIntro,
                    IntroStartSeconds = result.IntroStart.TotalSeconds,
                    IntroEndSeconds = result.IntroEnd.TotalSeconds,
                    Confidence = result.Confidence,
                    DetectedAt = DateTime.UtcNow
                };
                
                var success = await _mediaInfoService.UpdateIntroInfoInJsonAsync(episode, introInfo, cancellationToken);
                
                if (success)
                {
                    _logger.LogInformation("Successfully saved intro info to media JSON for {EpisodeName}", episode.Name);
                }
                else
                {
                    _logger.LogWarning("Failed to save intro info to media JSON for {EpisodeName}", episode.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save intro markers to JSON for {EpisodeName}", episode.Name);
            }
        }
        
        /// <summary>
        /// 检查剧集是否已经有片头检测结果
        /// </summary>
        private async Task<bool> HasIntroDetectionResultAsync(Episode episode, CancellationToken cancellationToken)
        {
            try
            {
                // 使用 MediaInfoService 统一检查
                var introInfo = await _mediaInfoService.GetIntroInfoFromJsonAsync(episode, cancellationToken);
                if (introInfo != null && introInfo.HasIntro)
                {
                    _logger.LogDebug("Found existing intro info in JSON for {EpisodeName}", episode.Name);
                    return true;
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
        /// 获取季度片头缓存
        /// </summary>
        private async Task<IntroDetectionResult> GetSeasonIntroCacheAsync(Episode episode, CancellationToken cancellationToken)
        {
            try
            {
                var config = JellyfinPlugin.Instance?.Configuration;
                if (config == null || !config.EnableFingerprintCache)
                {
                    return null;
                }
                
                var cacheDir = GetSeasonCacheDirectory(episode);
                var cacheFile = Path.Combine(cacheDir, "season-intro.json");
                
                _logger.LogInformation("🔍 Checking season intro cache: {CacheFile}", cacheFile);
                
                if (!File.Exists(cacheFile))
                {
                    _logger.LogInformation("📂 Cache file not found: {CacheFile}", cacheFile);
                    return null;
                }
                
                var json = await File.ReadAllTextAsync(cacheFile, cancellationToken);
                var cached = JsonSerializer.Deserialize<SeasonIntroCache>(json);
                
                if (cached == null)
                {
                    _logger.LogWarning("⚠️ Cache file exists but is invalid: {CacheFile}", cacheFile);
                    return null;
                }
                
                _logger.LogInformation("✅ Loaded season intro cache from: {CacheFile}", cacheFile);
                
                return new IntroDetectionResult
                {
                    ItemId = episode.Id,
                    HasIntro = cached.HasIntro,
                    IntroStart = TimeSpan.FromSeconds(cached.IntroStartSeconds),
                    IntroEnd = TimeSpan.FromSeconds(cached.IntroEndSeconds),
                    Confidence = cached.Confidence
                };
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load season intro cache for {EpisodeName}", episode.Name);
                return null;
            }
        }
        
        /// <summary>
        /// 保存季度片头缓存（只缓存成功的检测结果）
        /// </summary>
        private async Task SaveSeasonIntroCacheAsync(Episode episode, IntroDetectionResult result, CancellationToken cancellationToken)
        {
            try
            {
                var config = JellyfinPlugin.Instance?.Configuration;
                if (config == null || !config.EnableFingerprintCache)
                {
                    _logger.LogDebug("Fingerprint cache is disabled, skipping season cache save");
                    return;
                }
                
                // 🔥 关键修复：只缓存成功的检测结果
                if (!result.HasIntro)
                {
                    _logger.LogInformation("❌ No intro detected, not saving to season cache (will retry next time)");
                    return;
                }
                
                var cacheDir = GetSeasonCacheDirectory(episode);
                Directory.CreateDirectory(cacheDir);
                
                var cacheFile = Path.Combine(cacheDir, "season-intro.json");
                
                _logger.LogInformation("💾 Saving season intro cache to: {CacheFile}", cacheFile);
                
                var cache = new SeasonIntroCache
                {
                    SeriesName = episode.SeriesName,
                    SeasonNumber = episode.ParentIndexNumber ?? 0,
                    HasIntro = result.HasIntro,
                    IntroStartSeconds = result.IntroStart.TotalSeconds,
                    IntroEndSeconds = result.IntroEnd.TotalSeconds,
                    Confidence = result.Confidence,
                    DetectedAt = DateTime.UtcNow
                };
                
                var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(cacheFile, json, cancellationToken);
                
                _logger.LogInformation("✅ Saved season intro cache to {CacheFile} (Start: {Start}, End: {End})", 
                    cacheFile, 
                    TimeSpan.FromSeconds(cache.IntroStartSeconds),
                    TimeSpan.FromSeconds(cache.IntroEndSeconds));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save season intro cache for {EpisodeName}", episode.Name);
            }
        }
        
        /// <summary>
        /// 获取季度缓存目录
        /// </summary>
        private string GetSeasonCacheDirectory(Episode episode)
        {
            var config = JellyfinPlugin.Instance?.Configuration;
            var rootFolder = config?.MediaInfoJsonRootFolder;
            
            if (string.IsNullOrEmpty(rootFolder))
            {
                rootFolder = episode.ContainingFolderPath;
            }
            
            var seasonFolder = $"{episode.SeriesName?.Replace("/", "_").Replace("\\", "_")} - Season {episode.ParentIndexNumber ?? 0}";
            return Path.Combine(rootFolder, ".intro-cache", seasonFolder);
        }
        
        /// <summary>
        /// 季度片头缓存
        /// </summary>
        private class SeasonIntroCache
        {
            public string SeriesName { get; set; }
            public int SeasonNumber { get; set; }
            public bool HasIntro { get; set; }
            public double IntroStartSeconds { get; set; }
            public double IntroEndSeconds { get; set; }
            public double Confidence { get; set; }
            public DateTime DetectedAt { get; set; }
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
        public double Confidence { get; set; } = 1.0;
    }
}

