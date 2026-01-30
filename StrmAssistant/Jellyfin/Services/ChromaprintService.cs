using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StrmAssistant.Jellyfin.Common;

namespace StrmAssistant.Jellyfin.Services
{
    /// <summary>
    /// Chromaprint 音频指纹提取服务
    /// 使用 FFmpeg 的 chromaprint 功能提取音频指纹
    /// </summary>
    public class ChromaprintService
    {
        private readonly ILogger<ChromaprintService> _logger;
        private readonly string _ffmpegPath;
        
        public ChromaprintService(ILogger<ChromaprintService> logger)
        {
            _logger = logger;
            
            // 查找 FFmpeg 路径
            _ffmpegPath = FindFFmpegPath();
            
            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                _logger.LogWarning("FFmpeg not found in PATH");
            }
            else
            {
                // 启动时检查 chromaprint 支持
                Task.Run(async () => 
                {
                    var supported = await IsChromaprintSupportedAsync(CancellationToken.None);
                    if (!supported)
                    {
                        _logger.LogError(
                            "FFmpeg does not support chromaprint. " +
                            "Please install jellyfin-ffmpeg7 (version 7.1.1-7 or newer). " +
                            "See: https://github.com/intro-skipper/intro-skipper/wiki/Custom-FFMPEG-(MacOS)");
                    }
                    else
                    {
                        _logger.LogInformation("FFmpeg chromaprint support verified successfully");
                    }
                });
            }
        }
        
        /// <summary>
        /// 提取音频指纹
        /// </summary>
        /// <param name="videoPath">视频文件路径</param>
        /// <param name="duration">提取时长（默认 5 分钟）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>音频指纹字符串</returns>
        public async Task<string> ExtractFingerprintAsync(
            string videoPath, 
            TimeSpan? duration = null, 
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                throw new InvalidOperationException("FFmpeg not available");
            }
            
            if (string.IsNullOrEmpty(videoPath))
            {
                throw new ArgumentException("Video path cannot be empty", nameof(videoPath));
            }
            
            // 只对本地文件进行存在性检查，HTTP/RTSP 等 URL 不需要
            if (!StrmFileHelper.IsStreamingUrl(videoPath) && !File.Exists(videoPath))
            {
                throw new FileNotFoundException("Video file not found", videoPath);
            }
            
            var extractDuration = duration ?? TimeSpan.FromMinutes(5);
            
            try
            {
                _logger.LogDebug("Extracting fingerprint from {Path} (duration: {Duration})", 
                    videoPath, extractDuration);
                
                // FFmpeg 命令：提取音频并生成 chromaprint 指纹
                // 参考 intro-skipper 的实现
                // -hide_banner -loglevel warning: 减少日志输出
                // -ss 0: 从开始位置提取
                // -i: 输入文件
                // -to: 持续时间（秒）
                // -ac 2: 双声道（intro-skipper 使用 2，chromaprint 推荐）
                // -f chromaprint: 使用 chromaprint 格式
                // -fp_format raw: 输出二进制原始指纹
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-hide_banner -loglevel warning -ss 0 -i \"{videoPath}\" -to {extractDuration.TotalSeconds} -ac 2 -f chromaprint -fp_format raw -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = new Process { StartInfo = startInfo };
                process.Start();
                
                // 读取二进制输出（chromaprint raw 格式）
                var outputStream = process.StandardOutput.BaseStream;
                using var memoryStream = new MemoryStream();
                await outputStream.CopyToAsync(memoryStream, cancellationToken);
                var rawBytes = memoryStream.ToArray();
                
                // 读取错误输出
                var errorOutput = await process.StandardError.ReadToEndAsync();
                
                await process.WaitForExitAsync(cancellationToken);
                
                if (process.ExitCode != 0)
                {
                    _logger.LogError("FFmpeg failed with exit code {ExitCode}: {Error}", 
                        process.ExitCode, errorOutput);
                    throw new Exception($"FFmpeg failed: {errorOutput}");
                }
                
                if (rawBytes.Length == 0)
                {
                    throw new Exception("No fingerprint generated");
                }
                
                // 将二进制数据编码为 Base64 字符串
                var fingerprint = Convert.ToBase64String(rawBytes);
                
                _logger.LogDebug("Successfully extracted fingerprint ({Bytes} bytes, {Length} base64 chars)", 
                    rawBytes.Length, fingerprint.Length);
                
                return fingerprint;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract fingerprint from {Path}", videoPath);
                throw;
            }
        }
        
        /// <summary>
        /// 比对两个指纹的相似度
        /// 使用汉明距离算法（Hamming Distance）
        /// </summary>
        /// <param name="fingerprint1">指纹 1（原始二进制字符串）</param>
        /// <param name="fingerprint2">指纹 2（原始二进制字符串）</param>
        /// <returns>相似度（0.0-1.0）</returns>
        public double CompareFingerprints(string fingerprint1, string fingerprint2)
        {
            if (string.IsNullOrEmpty(fingerprint1) || string.IsNullOrEmpty(fingerprint2))
            {
                return 0.0;
            }
            
            try
            {
                // Chromaprint 的 raw 格式是二进制数据
                // 需要将其解析为 uint32 数组进行比对
                var bytes1 = Convert.FromBase64String(fingerprint1);
                var bytes2 = Convert.FromBase64String(fingerprint2);
                
                return CompareFingerprints(bytes1, bytes2);
            }
            catch
            {
                // 如果不是 Base64，尝试直接字符串比较（向后兼容）
                return CompareStringsSimple(fingerprint1, fingerprint2);
            }
        }
        
        /// <summary>
        /// 比对两个指纹字节数组的相似度
        /// 使用汉明距离算法
        /// </summary>
        private double CompareFingerprints(byte[] fingerprint1, byte[] fingerprint2)
        {
            if (fingerprint1 == null || fingerprint2 == null || fingerprint1.Length == 0 || fingerprint2.Length == 0)
            {
                return 0.0;
            }
            
            // 确保两个指纹长度相同（填充或截断）
            var minLength = Math.Min(fingerprint1.Length, fingerprint2.Length);
            var alignedLength = (minLength / 4) * 4; // 对齐到 4 字节（uint32）
            
            if (alignedLength < 4)
            {
                return 0.0;
            }
            
            int totalBits = alignedLength * 8;
            int matchingBits = 0;
            
            // 每 4 字节作为一个 uint32 进行比对
            for (int i = 0; i < alignedLength; i += 4)
            {
                uint value1 = BitConverter.ToUInt32(fingerprint1, i);
                uint value2 = BitConverter.ToUInt32(fingerprint2, i);
                
                // XOR 运算，相同位为 0，不同位为 1
                uint diff = value1 ^ value2;
                
                // 计算 diff 中有多少位是 0（匹配）
                int bitCount = 32 - CountBits(diff);
                matchingBits += bitCount;
            }
            
            // 相似度 = 匹配位数 / 总位数
            return (double)matchingBits / totalBits;
        }
        
        /// <summary>
        /// 计算一个 uint32 中有多少位是 1
        /// 使用 Brian Kernighan 算法
        /// </summary>
        private static int CountBits(uint value)
        {
            int count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }
            return count;
        }
        
        /// <summary>
        /// 简单字符串比较（向后兼容）
        /// </summary>
        private double CompareStringsSimple(string str1, string str2)
        {
            var minLength = Math.Min(str1.Length, str2.Length);
            var maxLength = Math.Max(str1.Length, str2.Length);
            
            if (maxLength == 0) return 0.0;
            
            int matches = 0;
            for (int i = 0; i < minLength; i++)
            {
                if (str1[i] == str2[i])
                {
                    matches++;
                }
            }
            
            return (double)matches / maxLength;
        }
        
        /// <summary>
        /// 查找 FFmpeg 路径
        /// </summary>
        private string FindFFmpegPath()
        {
            try
            {
                // 尝试 Jellyfin 的 FFmpeg
                var jellyfinPaths = new[]
                {
                    "/usr/lib/jellyfin-ffmpeg/ffmpeg",
                    "/usr/bin/ffmpeg",
                    "ffmpeg"
                };
                
                foreach (var path in jellyfinPaths)
                {
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = path,
                            Arguments = "-version",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        
                        using var process = Process.Start(startInfo);
                        if (process != null)
                        {
                            process.WaitForExit(1000);
                            if (process.ExitCode == 0)
                            {
                                _logger.LogInformation("Found FFmpeg at: {Path}", path);
                                return path;
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
                
                return "ffmpeg"; // 回退到 PATH 中的 ffmpeg
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to find FFmpeg");
                return null;
            }
        }
        
        /// <summary>
        /// 检查 FFmpeg 是否支持 chromaprint
        /// 参考 intro-skipper 的检查方式
        /// </summary>
        public async Task<bool> IsChromaprintSupportedAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_ffmpegPath))
            {
                return false;
            }
            
            try
            {
                // 检查 1: 是否支持 chromaprint muxer（复用器）
                var startInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-muxers",  // chromaprint 是 muxer，不是 filter
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = new Process { StartInfo = startInfo };
                process.Start();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);
                
                if (!output.Contains("chromaprint", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "FFmpeg does not support chromaprint muxer. " +
                        "Please install jellyfin-ffmpeg7 (version 7.1.1-7 or newer). " +
                        "Found FFmpeg at: {FFmpegPath}", _ffmpegPath);
                    return false;
                }
                
                // 检查 2: 是否支持 raw binary fingerprint 格式
                var startInfo2 = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-h muxer=chromaprint",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process2 = new Process { StartInfo = startInfo2 };
                process2.Start();
                
                var output2 = await process2.StandardOutput.ReadToEndAsync();
                await process2.WaitForExitAsync(cancellationToken);
                
                if (!output2.Contains("binary raw", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "FFmpeg chromaprint does not support raw binary format. " +
                        "Please install jellyfin-ffmpeg7 (version 7.1.1-7 or newer)");
                    return false;
                }
                
                _logger.LogInformation("FFmpeg chromaprint support verified successfully (path: {FFmpegPath})", _ffmpegPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check chromaprint support");
                return false;
            }
        }
    }
}
