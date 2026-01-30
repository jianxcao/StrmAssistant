using System;
using Jellyfin.Data.Enums;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Adapters;

namespace StrmAssistant.Jellyfin.Services
{
    /// <summary>
    /// 多版本合并服务 - 自动合并同目录的重复影片
    /// </summary>
    public class MergeVersionService
    {
        private readonly LibraryManagerAdapter _libraryManager;
        private readonly ILogger<MergeVersionService> _logger;
        
        public MergeVersionService(LibraryManagerAdapter libraryManager, ILogger<MergeVersionService> logger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
        }
        
        /// <summary>
        /// 查找并合并重复电影
        /// </summary>
        public async Task<int> MergeDuplicateMoviesAsync(
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting duplicate movie merge process");
                
                // 1. 获取所有电影
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive = true
                };
                
                var movies = await _libraryManager.GetItemsAsync(query, cancellationToken);
                var movieList = movies.OfType<Movie>().ToList();
                
                // 2. 按目录分组
                var groupedByDirectory = movieList
                    .Where(m => !string.IsNullOrEmpty(m.Path))
                    .GroupBy(m => Path.GetDirectoryName(m.Path))
                    .Where(g => g.Count() > 1)
                    .ToList();
                
                _logger.LogInformation($"Found {groupedByDirectory.Count} directories with multiple movies");
                
                var mergedCount = 0;
                var totalGroups = groupedByDirectory.Count;
                
                // 3. 处理每个分组
                for (var i = 0; i < totalGroups; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var group = groupedByDirectory[i];
                    var moviesInGroup = group.ToList();
                    
                    // 检查是否为同一部电影的不同版本
                    if (IsSameMovieDifferentVersions(moviesInGroup))
                    {
                        var primary = SelectPrimaryVersion(moviesInGroup);
                        var alternatives = moviesInGroup.Where(m => m.Id != primary.Id).ToArray();
                        
                        _logger.LogInformation($"Merging {alternatives.Length} versions into {primary.Name}");
                        
                        var success = await _libraryManager.MergeItemsAsync(
                            primary.Id,
                            alternatives.Select(a => a.Id).ToArray(),
                            cancellationToken);
                        
                        if (success)
                            mergedCount++;
                    }
                    
                    progress?.Report((double)(i + 1) / totalGroups * 100);
                }
                
                _logger.LogInformation($"Merge completed: {mergedCount} groups merged");
                return mergedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error merging duplicate movies: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// 判断是否为同一部电影的不同版本
        /// </summary>
        private bool IsSameMovieDifferentVersions(List<Movie> movies)
        {
            if (movies.Count < 2)
                return false;
            
            // 检查电影名称相似度
            var firstMovie = movies[0];
            var baseName = GetBaseMovieName(firstMovie.Name);
            
            return movies.All(m => GetBaseMovieName(m.Name).Equals(baseName, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// 获取电影基础名称（去除版本标识）
        /// </summary>
        private string GetBaseMovieName(string name)
        {
            // 移除常见的版本标识：4K, 1080p, 720p, BluRay, WEB-DL 等
            var versionPatterns = new[] { "4K", "1080p", "720p", "BluRay", "WEB-DL", "WEBRip", "HDRip", "BDRip" };
            var baseName = name;
            
            foreach (var pattern in versionPatterns)
            {
                baseName = baseName.Replace(pattern, "", StringComparison.OrdinalIgnoreCase);
            }
            
            return baseName.Trim();
        }
        
        /// <summary>
        /// 选择主版本（优先选择质量最高的）
        /// </summary>
        private Movie SelectPrimaryVersion(List<Movie> movies)
        {
            // 优先级：4K > 1080p > 720p > 其他
            var priorities = new Dictionary<string, int>
            {
                { "4K", 4 },
                { "1080p", 3 },
                { "720p", 2 },
                { "BluRay", 1 }
            };
            
            return movies
                .OrderByDescending(m =>
                {
                    var name = m.Name.ToUpper();
                    return priorities.Where(p => name.Contains(p.Key)).Select(p => p.Value).FirstOrDefault();
                })
                .ThenByDescending(m => m.DateCreated)
                .First();
        }
    }
}
