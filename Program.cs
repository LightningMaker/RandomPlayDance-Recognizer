using NAudio.Wave;
using SoundFingerprinting.Audio;
using SoundFingerprinting.Builder;
using SoundFingerprinting.Data;
using SoundFingerprinting.InMemory;
using SoundFingerprinting.Query;
using System.Reflection.PortableExecutable;
using System.Linq;

namespace RandomPlayDance_Recognizer
{
    internal class Program
    {
        static readonly string LibraryPath = "MusicLibrary";
        static readonly string InputFolder = "Input";
        static readonly string ConvertedFolder = "ConvertedWav";

        static async Task Main(string[] args)
        {
            try
            {
                Directory.CreateDirectory(ConvertedFolder);

                Console.WriteLine("===== 已启动随舞音频识别器 =====\n");

                var modelService = new InMemoryModelService();
                var audioService = new SoundFingerprintingAudioService();

                // ========= 1️⃣ 构建特征库 =========
                Console.WriteLine("正在构建音频特征库...\n");

                var musicFiles = Directory.GetFiles(LibraryPath)
                                          .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));

                foreach (var file in musicFiles)
                {
                    Console.WriteLine($"正在添加：{Path.GetFileName(file)}");

                    // 统一转换为 WAV（PCM16, 44.1kHz，保留声道数），返回转换后的文件路径（位于 ConvertedWav）
                    var fileToUse = EnsureConvertedToWav(file);

                    var track = new TrackInfo(
                        Path.GetFileNameWithoutExtension(file),
                        Path.GetFileName(file),
                        fileToUse);

                    var hashes = await FingerprintCommandBuilder.Instance
                        .BuildFingerprintCommand()
                        .From(fileToUse)
                        .UsingServices(audioService)
                        .Hash();

                    modelService.Insert(track, hashes);
                }

                Console.WriteLine("\n音频特征库构建完成。\n");

                // 选择 Input 文件夹下最新的音频文件
                var inputAudioPath = GetLatestAudioFile(InputFolder);
                if (inputAudioPath == null)
                {
                    Console.WriteLine($"文件夹 '{InputFolder}' 中未找到 .mp3 或 .wav 文件。");
                    Console.ReadLine();
                    return;
                }

                // 将输入也统一转换为 WAV
                inputAudioPath = EnsureConvertedToWav(inputAudioPath);

                Console.WriteLine($"使用输入音频：{Path.GetFileName(inputAudioPath)}");

                // ========= 2️⃣ 识别 =========
                Console.WriteLine("开始识别...\n");

                int secondsToAnalyze = 10; // 每次分析的音频时长（可调）
                int startAtSecond = 0; // 从音频的起始位置开始

                var lastDetectedTime = new Dictionary<string, TimeSpan>();
                var displayedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 记录已显示的歌曲（去重）
                TimeSpan duplicateThreshold = TimeSpan.FromSeconds(30);

                // 重要：不要在识别循环外持续打开同一文件（会与后续 Query 的文件访问冲突）
                // 先打开一次读取总时长后立即释放句柄，之后让 SoundFingerprinting 在需要时自行打开文件
                double totalSeconds;
                using (var durationReader = new AudioFileReader(inputAudioPath))
                {
                    totalSeconds = durationReader.TotalTime.TotalSeconds;
                }

                while (true)
                {
                    var result = await QueryCommandBuilder.Instance
                        .BuildQueryCommand()
                        .From(inputAudioPath, secondsToAnalyze, startAtSecond)
                        .UsingServices(modelService, audioService)
                        .Query();

                    if (result.ContainsMatches)
                    {
                        var match = result.BestMatch;

                        if (match == null || match.Audio == null)
                        {
                            Console.WriteLine("该片段未找到匹配。");
                            startAtSecond += secondsToAnalyze;
                            continue;
                        }

                        if (match.Audio.Confidence > 0.8) // 过滤低置信度
                        {
                            // 注意：QueryMatchStartsAt 是相对于当前查询片段（startAtSecond）的偏移（秒）
                            // 因此需要加上 startAtSecond 得到在整个音频文件中的绝对时间
                            double queryStartSeconds = match.Audio.QueryMatchStartsAt;
                            TimeSpan preciseTime = TimeSpan.FromSeconds(startAtSecond + queryStartSeconds);
                            var title = match.Audio.Track.Title ?? match.Audio.Track.Id.ToString();

                            // 如果该歌曲已经显示过（HashSet 中存在），跳过后续显示 —— 实现“有重复则只显示第一个”的需求
                            if (displayedTitles.Contains(title))
                            {
                                // 已显示过，直接跳过记录与输出
                                startAtSecond += secondsToAnalyze;
                                if (startAtSecond >= totalSeconds)
                                    break;
                                continue;
                            }

                            bool shouldRecord = true;

                            if (lastDetectedTime.ContainsKey(title))
                            {
                                if ((preciseTime - lastDetectedTime[title]) < duplicateThreshold)
                                    shouldRecord = false;
                            }

                            if (shouldRecord)
                            {
                                lastDetectedTime[title] = preciseTime;

                                // 首次显示该标题，加入已显示集合，后续遇到相同标题将被忽略
                                displayedTitles.Add(title);

                                Console.ForegroundColor = ConsoleColor.Green;
                                Console.WriteLine(
                                    $"找到音频：{title}  |  开始时间：{preciseTime:hh\\:mm\\:ss}");
                                Console.ResetColor();
                            }
                        }
                    }

                    startAtSecond += secondsToAnalyze; // 移动到下一个片段
                    if (startAtSecond >= totalSeconds)
                        break; // 如果超出音频总时长，结束识别
                }


                Console.WriteLine("\n===== 识别完成 =====");
                Console.ReadLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"错误：{ex.Message}");
                Console.ReadLine();
            }
        }

        static string? GetLatestAudioFile(string folder)
        {
            if (!Directory.Exists(folder))
                return null;

            var files = Directory.GetFiles(folder)
                                 .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));

            return files.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        }

        // 统一转换为 WAV（PCM16, 44.1kHz），并输出到 ConvertedWav 目录
        // - 如果 ConvertedWav 中已有且比源文件新，直接重用
        // - 保留原文件不做删除/替换，避免破坏原始库
        static string EnsureConvertedToWav(string inputFile)
        {
            Directory.CreateDirectory(ConvertedFolder);

            string baseName = Path.GetFileNameWithoutExtension(inputFile);
            string convertedFile = Path.Combine(ConvertedFolder, baseName + ".wav");

            try
            {
                // 若已存在且比源文件新，直接返回
                if (File.Exists(convertedFile) && File.GetLastWriteTimeUtc(convertedFile) >= File.GetLastWriteTimeUtc(inputFile))
                {
                    //Console.WriteLine($"重用已存在的转换 WAV：{Path.GetFileName(convertedFile)}");
                    return convertedFile;
                }

                // 使用 MediaFoundationReader 更鲁棒地读取源（支持 mp3/wav 等）
                using var reader = new MediaFoundationReader(inputFile);
                var inFormat = reader.WaveFormat;
                Console.WriteLine($"正在转换 {Path.GetFileName(inputFile)} -> {Path.GetFileName(convertedFile)}。源格式：{inFormat.SampleRate}Hz, {inFormat.BitsPerSample}bit, {inFormat.Channels}ch, {inFormat.Encoding}");

                // 目标格式：PCM 16-bit, 44100 Hz, 保留声道数
                var targetFormat = new NAudio.Wave.WaveFormat(44100, 16, inFormat.Channels);

                using var resampler = new MediaFoundationResampler(reader, targetFormat)
                {
                    ResamplerQuality = 60
                };

                // 写出 WAV（PCM16）
                WaveFileWriter.CreateWaveFile(convertedFile, resampler);
                Console.WriteLine($"已转换：{Path.GetFileName(convertedFile)}");

                return convertedFile;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"转换失败：'{inputFile}'，原因：{ex.Message}");

                // 回退：尝试用 AudioFileReader 写出 WAV
                try
                {
                    using var afr = new AudioFileReader(inputFile);
                    var targetFormat = new NAudio.Wave.WaveFormat(44100, 16, afr.WaveFormat.Channels);
                    using var conv = new WaveFormatConversionStream(targetFormat, afr);
                    WaveFileWriter.CreateWaveFile(convertedFile, conv);
                    Console.WriteLine($"回退转换成功：{Path.GetFileName(convertedFile)}");
                    return convertedFile;
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine($"回退转换失败：{fallbackEx.Message}");
                    // 若都失败则返回原文件以避免阻塞流程
                    return inputFile;
                }
            }
        }
    }
}