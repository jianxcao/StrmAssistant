using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace StrmAssistant.Jellyfin.Adapters
{
    /// <summary>
    /// MediaSegment 适配器 - 封装 Jellyfin 10.10+ 的 MediaSegment API
    /// </summary>
    public class MediaSegmentAdapter
    {
        private readonly ILogger<MediaSegmentAdapter> _logger;
        private readonly object? _mediaSegmentManager;
        private readonly bool _isSupported;
        
        public MediaSegmentAdapter(
            IServiceProvider serviceProvider,
            ILogger<MediaSegmentAdapter> logger)
        {
            _logger = logger;
            
            // 尝试动态获取 IMediaSegmentManager 服务
            // 使用反射以兼容不同版本的 Jellyfin
            try
            {
                var managerType = Type.GetType("MediaBrowser.Controller.MediaSegments.IMediaSegmentManager, MediaBrowser.Controller");
                if (managerType != null)
                {
                    _mediaSegmentManager = serviceProvider.GetService(managerType);
                    _isSupported = _mediaSegmentManager != null;
                    
                    if (_isSupported)
                    {
                        _logger.LogInformation("MediaSegment API is available and loaded successfully");
                    }
                    else
                    {
                        _logger.LogWarning("MediaSegment API type found but service not available");
                    }
                }
                else
                {
                    _logger.LogWarning("MediaSegment API not found in this Jellyfin version");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load MediaSegment API");
                _isSupported = false;
            }
        }
        
        /// <summary>
        /// 检查 MediaSegment API 是否可用
        /// </summary>
        public bool IsSupported => _isSupported;
        
        /// <summary>
        /// 创建或更新片头片尾 MediaSegment
        /// </summary>
        public async Task<bool> CreateIntroOutroSegmentsAsync(
            BaseItem item,
            TimeSpan introStart,
            TimeSpan introEnd,
            TimeSpan? outroStart = null,
            TimeSpan? outroEnd = null,
            CancellationToken cancellationToken = default)
        {
            if (!_isSupported || _mediaSegmentManager == null)
            {
                _logger.LogWarning("MediaSegment API is not supported, cannot create segments");
                return false;
            }
            
            try
            {
                // 使用反射调用 MediaSegment API
                var managerType = _mediaSegmentManager.GetType();
                
                // 1. 创建片头 MediaSegment
                if (introStart < introEnd)
                {
                    await CreateSegmentAsync(
                        managerType,
                        item.Id,
                        "Intro",
                        introStart,
                        introEnd,
                        cancellationToken);
                        
                    _logger.LogInformation(
                        "Created Intro segment for {ItemName}: {Start} - {End}",
                        item.Name,
                        introStart,
                        introEnd);
                }
                
                // 2. 创建片尾 MediaSegment（如果提供）
                if (outroStart.HasValue && outroEnd.HasValue && outroStart < outroEnd)
                {
                    await CreateSegmentAsync(
                        managerType,
                        item.Id,
                        "Outro",
                        outroStart.Value,
                        outroEnd.Value,
                        cancellationToken);
                        
                    _logger.LogInformation(
                        "Created Outro segment for {ItemName}: {Start} - {End}",
                        item.Name,
                        outroStart,
                        outroEnd);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create MediaSegments for {ItemName}", item.Name);
                return false;
            }
        }
        
        /// <summary>
        /// 使用反射创建单个 MediaSegment
        /// </summary>
        private async Task CreateSegmentAsync(
            Type managerType,
            Guid itemId,
            string segmentType,
            TimeSpan start,
            TimeSpan end,
            CancellationToken cancellationToken)
        {
            // 构造 MediaSegmentDto 对象
            var dtoType = Type.GetType("MediaBrowser.Model.MediaSegments.MediaSegmentDto, MediaBrowser.Model");
            if (dtoType == null)
            {
                _logger.LogError("MediaSegmentDto type not found");
                return;
            }
            
            // 创建 DTO 实例
            var dto = Activator.CreateInstance(dtoType);
            if (dto == null)
            {
                _logger.LogError("Failed to create MediaSegmentDto instance");
                return;
            }
            
            // 设置属性
            SetProperty(dto, "ItemId", itemId);
            SetProperty(dto, "Type", segmentType);
            SetProperty(dto, "StartTicks", start.Ticks);
            SetProperty(dto, "EndTicks", end.Ticks);
            
            // 调用 CreateSegmentAsync 方法
            var createMethod = managerType.GetMethod("CreateSegmentAsync");
            if (createMethod == null)
            {
                _logger.LogError("CreateSegmentAsync method not found on MediaSegmentManager");
                return;
            }
            
            var task = createMethod.Invoke(_mediaSegmentManager, new[] { dto, cancellationToken });
            if (task is Task asyncTask)
            {
                await asyncTask;
            }
        }
        
        /// <summary>
        /// 删除指定类型的所有 MediaSegment（用于更新前清理）
        /// </summary>
        public async Task<bool> DeleteSegmentsByTypeAsync(
            Guid itemId,
            string segmentType,
            CancellationToken cancellationToken = default)
        {
            if (!_isSupported || _mediaSegmentManager == null)
            {
                return false;
            }
            
            try
            {
                var managerType = _mediaSegmentManager.GetType();
                
                // 1. 获取现有的 segments
                var getMethod = managerType.GetMethod("GetSegmentsAsync");
                if (getMethod == null)
                {
                    _logger.LogError("GetSegmentsAsync method not found");
                    return false;
                }
                
                var getTask = getMethod.Invoke(_mediaSegmentManager, new object[] { itemId, null });
                if (getTask is Task<object> asyncGetTask)
                {
                    var segments = await asyncGetTask;
                    
                    // 2. 筛选并删除指定类型的 segments
                    if (segments is IEnumerable<object> segmentList)
                    {
                        foreach (var segment in segmentList)
                        {
                            var type = GetProperty(segment, "Type")?.ToString();
                            var segmentId = GetProperty(segment, "Id");
                            
                            if (type == segmentType && segmentId is Guid id)
                            {
                                var deleteMethod = managerType.GetMethod("DeleteSegmentAsync");
                                if (deleteMethod != null)
                                {
                                    var deleteTask = deleteMethod.Invoke(_mediaSegmentManager, new object[] { id });
                                    if (deleteTask is Task asyncDeleteTask)
                                    {
                                        await asyncDeleteTask;
                                        _logger.LogDebug("Deleted {Type} segment {Id}", segmentType, id);
                                    }
                                }
                            }
                        }
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete segments for item {ItemId}", itemId);
                return false;
            }
        }
        
        /// <summary>
        /// 使用反射设置对象属性
        /// </summary>
        private void SetProperty(object obj, string propertyName, object? value)
        {
            try
            {
                var property = obj.GetType().GetProperty(propertyName);
                property?.SetValue(obj, value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set property {PropertyName}", propertyName);
            }
        }
        
        /// <summary>
        /// 使用反射获取对象属性
        /// </summary>
        private object? GetProperty(object obj, string propertyName)
        {
            try
            {
                var property = obj.GetType().GetProperty(propertyName);
                return property?.GetValue(obj);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get property {PropertyName}", propertyName);
                return null;
            }
        }
    }
}
