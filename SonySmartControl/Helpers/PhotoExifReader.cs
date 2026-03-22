using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace SonySmartControl.Helpers;

/// <summary>从照片文件读取 EXIF；RAW 容器无标准 EXIF 时从内嵌 JPEG 解析（多段合并：大图预览与缩略图常分别带部分字段）。</summary>
public static class PhotoExifReader
{
    public sealed record Snapshot(
        string CameraModel,
        string LensModel,
        string Iso,
        string Aperture,
        string ExposureTime,
        string ExposureCompensation,
        string FocalLength,
        string CaptureTime);

    public static Snapshot Empty { get; } = new("—", "—", "—", "—", "—", "—", "—", "—");

    /// <summary>无法读取或文件不存在时返回 <see cref="Empty"/>。</summary>
    public static Snapshot TryRead(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Empty;

        Snapshot? fromFile = null;
        try
        {
            using (var stream = File.OpenRead(path))
                fromFile = BuildSnapshot(ImageMetadataReader.ReadMetadata(stream));
        }
        catch
        {
            fromFile = Empty;
        }

        fromFile ??= Empty;

        var best = fromFile;
        foreach (var seg in EmbeddedJpegPreviewLoader.GetEmbeddedJpegSegmentsForMetadata(path))
        {
            try
            {
                using var ms = new MemoryStream(seg, writable: false);
                var fromEmbedded = BuildSnapshot(ImageMetadataReader.ReadMetadata(ms));
                best = MergePreferNonEmpty(best, fromEmbedded);
            }
            catch
            {
                // 忽略单段损坏
            }
        }

        return best;
    }

    private static Snapshot MergePreferNonEmpty(Snapshot a, Snapshot b)
    {
        static string Pick(string x, string y) => x != "—" ? x : y;

        return new Snapshot(
            Pick(a.CameraModel, b.CameraModel),
            Pick(a.LensModel, b.LensModel),
            Pick(a.Iso, b.Iso),
            Pick(a.Aperture, b.Aperture),
            Pick(a.ExposureTime, b.ExposureTime),
            Pick(a.ExposureCompensation, b.ExposureCompensation),
            Pick(a.FocalLength, b.FocalLength),
            Pick(a.CaptureTime, b.CaptureTime));
    }

    private static Snapshot BuildSnapshot(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        if (directories == null || directories.Count == 0)
            return Empty;

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

        var make = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagMake));
        var model = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagModel));
        var camera = CombineMakeModel(make, model);

        var lens = FirstNonEmpty(
            Clean(sub?.GetDescription(ExifDirectoryBase.TagLensModel)),
            Clean(sub?.GetDescription(ExifDirectoryBase.TagLensSpecification)),
            FirstDescription(directories, ExifDirectoryBase.TagLensModel, ExifDirectoryBase.TagLensSpecification));

        var iso = FirstNonEmpty(
            Clean(sub?.GetDescription(ExifDirectoryBase.TagIsoEquivalent)),
            FirstDescription(directories, ExifDirectoryBase.TagIsoEquivalent));

        var aperture = FirstNonEmpty(
            Clean(sub?.GetDescription(ExifDirectoryBase.TagFNumber)),
            FirstDescription(directories, ExifDirectoryBase.TagFNumber));

        var shutter = FirstNonEmpty(
            Clean(sub?.GetDescription(ExifDirectoryBase.TagExposureTime)),
            FirstDescription(directories, ExifDirectoryBase.TagExposureTime));

        var ev = FirstNonEmpty(
            Clean(sub?.GetDescription(ExifDirectoryBase.TagExposureBias)),
            FirstDescription(directories, ExifDirectoryBase.TagExposureBias));

        var focal = FirstNonEmpty(
            Clean(sub?.GetDescription(ExifDirectoryBase.TagFocalLength)),
            FirstDescription(directories, ExifDirectoryBase.TagFocalLength, ExifDirectoryBase.Tag35MMFilmEquivFocalLength));

        var dt = FirstNonEmpty(
            FormatExifDateTime(sub?.GetDescription(ExifDirectoryBase.TagDateTimeOriginal)),
            FormatExifDateTime(sub?.GetDescription(ExifDirectoryBase.TagDateTimeDigitized)),
            Clean(ifd0?.GetDescription(ExifDirectoryBase.TagDateTime)),
            FirstDescription(directories, ExifDirectoryBase.TagDateTimeOriginal, ExifDirectoryBase.TagDateTimeDigitized));

        return new Snapshot(
            string.IsNullOrEmpty(camera) ? "—" : camera,
            string.IsNullOrEmpty(lens) ? "—" : lens,
            string.IsNullOrEmpty(iso) ? "—" : iso,
            string.IsNullOrEmpty(aperture) ? "—" : aperture,
            string.IsNullOrEmpty(shutter) ? "—" : shutter,
            string.IsNullOrEmpty(ev) ? "—" : ev,
            string.IsNullOrEmpty(focal) ? "—" : focal,
            string.IsNullOrEmpty(dt) ? "—" : dt);
    }

    /// <summary>同一标签可能在 IFD0、SubIFD、厂商目录等多次出现（内嵌 JPEG 尤甚）。</summary>
    private static string? FirstDescription(IReadOnlyList<MetadataExtractor.Directory> dirs, params int[] tagTypes)
    {
        foreach (var tagType in tagTypes)
        {
            foreach (var d in dirs)
            {
                if (!d.ContainsTag(tagType))
                    continue;
                var s = Clean(d.GetDescription(tagType));
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        return null;
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        return s.Trim();
    }

    private static string CombineMakeModel(string? make, string? model)
    {
        if (string.IsNullOrEmpty(model))
            return make ?? "";
        if (string.IsNullOrEmpty(make))
            return model;
        if (model.Contains(make, StringComparison.OrdinalIgnoreCase))
            return model;
        return $"{make} {model}";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v!;
        }

        return "";
    }

    private static string? FormatExifDateTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = s.Trim();
        if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var t))
            return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t))
            return t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return s;
    }
}
