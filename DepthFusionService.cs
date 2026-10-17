using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HalconDotNet;

namespace DepthFusionApp
{

/// <summary>
/// 景深融合结果（融合图 / 深度索引图 / 置信度图）。
/// </summary>
public sealed class FusionResult : IDisposable
{
    public HObject Fused { get; private set; }
    public HObject Depth { get; private set; }
    public HObject Confidence { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int ImageCount { get; private set; }
    public long ElapsedMs { get; private set; }

    public FusionResult(HObject fused, HObject depth, HObject confidence, int width, int height, int imageCount, long elapsedMs)
    {
        Fused = fused;
        Depth = depth;
        Confidence = confidence;
        Width = width;
        Height = height;
        ImageCount = imageCount;
        ElapsedMs = elapsedMs;
    }

    public void Dispose()
    {
        Fused?.Dispose();
        Depth?.Dispose();
        Confidence?.Dispose();
    }
}

/// <summary>
/// 由 景深融和测试.hdev 翻译而来的纯 HalconDotNet 实现，不依赖 HDevEngine：
///
///   read_image                         （多张不同对焦位置的图像）
///   compose3 / channels_to_image       （合并为多通道图像）
///   depth_from_focus                   （计算深度索引图与置信度图）
///   mean_image                         （平滑深度索引图，消除像素跳变/雪花）
///   select_grayvalues_from_channels    （逐像素选取最清晰的灰度值 -> 融合图）
///
/// 与原 hdev 的区别：用 channels_to_image 代替 compose3，因此支持任意张数（≥2），
/// 且自动把彩色图像转为单通道灰度图（depth_from_focus 要求各通道为单通道图像）。
/// </summary>
public static class DepthFusionService
{
    public static FusionResult Run(
        IReadOnlyList<string> imagePaths,
        string filter,
        int smoothSize,
        IProgress<string> progress = null)
    {
        if (imagePaths == null || imagePaths.Count < 2)
        {
            throw new ArgumentException("景深融合至少需要 2 张不同对焦位置的图像。", nameof(imagePaths));
        }

        // mean_image 的窗口宽高必须是正奇数
        if (smoothSize < 1)
        {
            smoothSize = 1;
        }
        if ((smoothSize & 1) == 0)
        {
            smoothSize += 1;
        }

        var sw = Stopwatch.StartNew();

        // ---- 1. 逐张读图并转为单通道灰度 ----
        HOperatorSet.GenEmptyObj(out HObject rawStack);
        HObject channelStack = null;
        HObject multi = null;
        HObject depth = null;
        HObject depthSmooth = null;
        HObject confidence = null;
        HObject fused = null;

        try
        {
            int width = int.MaxValue, height = int.MaxValue;
            var sizes = new (int W, int H)[imagePaths.Count];

            for (int i = 0; i < imagePaths.Count; i++)
            {
                progress?.Report($"正在读取第 {i + 1}/{imagePaths.Count} 张图像…");

                HOperatorSet.ReadImage(out HObject image, imagePaths[i]);
                HObject gray = ToSingleChannel(image);
                image.Dispose();

                HOperatorSet.GetImageSize(gray, out HTuple w, out HTuple h);
                sizes[i] = (w.I, h.I);
                width = Math.Min(width, w.I);
                height = Math.Min(height, h.I);

                HOperatorSet.ConcatObj(rawStack, gray, out HObject bigger);
                rawStack.Dispose();
                gray.Dispose();
                rawStack = bigger;
            }

            // ---- 1b. 尺寸不一致时，从左上角无损裁剪到共同尺寸（不做缩放插值） ----
            bool needCrop = sizes.Any(s => s.W != width || s.H != height);
            if (needCrop)
            {
                progress?.Report($"图像尺寸不完全一致，统一裁剪为 {width}×{height}…");
                HOperatorSet.GenEmptyObj(out channelStack);
                for (int i = 1; i <= imagePaths.Count; i++)
                {
                    HOperatorSet.SelectObj(rawStack, out HObject one, i);
                    HOperatorSet.CropPart(one, out HObject cropped, 0, 0, width, height);
                    one.Dispose();
                    HOperatorSet.ConcatObj(channelStack, cropped, out HObject bigger);
                    channelStack.Dispose();
                    cropped.Dispose();
                    channelStack = bigger;
                }
            }
            else
            {
                channelStack = rawStack.CopyObj(1, -1);
            }

            // ---- 2. 多张单通道图 -> 一张多通道图（对应原 compose3） ----
            progress?.Report("正在合成多通道图像…");
            HOperatorSet.ChannelsToImage(channelStack, out multi);

            // ---- 3. 景深计算 ----
            progress?.Report("正在计算深度图（depth_from_focus）…");
            HOperatorSet.DepthFromFocus(multi, out depth, out confidence, filter, "next_maximum");

            // ---- 4. 平滑深度索引图 ----
            progress?.Report($"正在平滑深度图（mean_image {smoothSize}×{smoothSize}）…");
            HOperatorSet.MeanImage(depth, out depthSmooth, smoothSize, smoothSize);

            // ---- 5. 按深度索引逐像素取最清晰灰度值 ----
            progress?.Report("正在合成全焦图像…");
            HOperatorSet.SelectGrayvaluesFromChannels(multi, depthSmooth, out fused);

            // depthSmooth 只是中间量，最终保留原始深度图用于显示/保存
            sw.Stop();
            progress?.Report($"融合完成，用时 {sw.ElapsedMilliseconds} ms。");

            return new FusionResult(
                fused, depth, confidence,
                width, height, imagePaths.Count, sw.ElapsedMilliseconds);
        }
        catch
        {
            fused?.Dispose();
            depth?.Dispose();
            confidence?.Dispose();
            throw;
        }
        finally
        {
            depthSmooth?.Dispose();
            multi?.Dispose();
            channelStack?.Dispose();
            rawStack?.Dispose();
        }
    }

    /// <summary>
    /// 保证图像为单通道：3 通道转灰度，其它情况取第 1 个通道。
    /// </summary>
    private static HObject ToSingleChannel(HObject image)
    {
        HOperatorSet.CountChannels(image, out HTuple channels);
        if (channels.I == 1)
        {
            return image.CopyObj(1, 1);
        }
        if (channels.I == 3)
        {
            HOperatorSet.Rgb1ToGray(image, out HObject gray);
            return gray;
        }

        HOperatorSet.AccessChannel(image, out HObject first, 1);
        return first;
    }
}
}
