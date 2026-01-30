using MediaBrowser.Model.Plugins;
using Jellyfin.Data.Enums;

namespace StrmAssistant.Jellyfin
{
    /// <summary>
    /// Jellyfin 插件配置类
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// 启用媒体信息提取
        /// </summary>
        public bool EnableMediaInfoExtraction { get; set; } = true;
        
        /// <summary>
        /// 启用片头检测
        /// </summary>
        public bool EnableIntroDetection { get; set; } = true;
        
        /// <summary>
        /// 启用多版本合并
        /// </summary>
        public bool EnableMultiVersionMerge { get; set; } = true;
        
        /// <summary>
        /// 启用中文搜索增强
        /// </summary>
        public bool EnableChineseSearch { get; set; } = true;
        
        /// <summary>
        /// 启用字幕扫描
        /// </summary>
        public bool EnableSubtitleScan { get; set; } = true;
        
        /// <summary>
        /// 启用拼音排序
        /// </summary>
        public bool EnablePinyinSort { get; set; } = true;
        
        /// <summary>
        /// 调试模式
        /// </summary>
        public bool DebugMode { get; set; } = false;
        
        /// <summary>
        /// 片头检测超时（秒）
        /// </summary>
        public int IntroDetectionTimeout { get; set; } = 300;
        
        /// <summary>
        /// 媒体信息提取并发数
        /// </summary>
        public int MediaInfoConcurrency { get; set; } = 2;
        
        /// <summary>
        /// 片头检测并发数（同时处理的剧集数）
        /// 建议设置为 1-3，避免网络拥堵
        /// </summary>
        public int IntroDetectionConcurrency { get; set; } = 2;
        
        /// <summary>
        /// 指纹提取时长（分钟）
        /// 从视频开头提取多长时间的音频用于指纹分析
        /// </summary>
        public int IntroFingerprintDurationMinutes { get; set; } = 5;
        
        /// <summary>
        /// 启用指纹缓存
        /// 缓存已提取的指纹，避免重复处理
        /// </summary>
        public bool EnableFingerprintCache { get; set; } = true;
        
        /// <summary>
        /// 启用媒体信息持久化到 JSON 文件
        /// </summary>
        public bool EnableMediaInfoPersistence { get; set; } = false;
        
        /// <summary>
        /// 媒体信息 JSON 文件根目录
        /// 为空时，JSON 文件将保存在媒体文件所在的同一目录下
        /// 设置后，JSON 文件将保存在此根目录下，保持与媒体文件相同的相对路径结构
        /// </summary>
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;
        
        /// <summary>
        /// 强制重新提取所有媒体信息
        /// 启用后，即使已有媒体信息也会重新提取（下次任务执行后自动关闭）
        /// </summary>
        public bool ForceReExtractMediaInfo { get; set; } = false;
        
        /// <summary>
        /// 强制重新检测所有片头片尾
        /// 启用后，即使已有检测结果也会重新检测（下次任务执行后自动关闭）
        /// </summary>
        public bool ForceReDetectIntro { get; set; } = false;
    }
}
