using System.Text;
using ScreenshotCollector.Models;

namespace ScreenshotCollector.Services;

public static class ImageExportTemplateService
{
    public static readonly IReadOnlyList<string> Presets =
    [
        "%n",
        ImageExportOptions.DefaultTemplate,
        "%s - %02i",
        "%d %t - %n",
        "%e - %02i"
    ];

    public static string ToCompactTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return ImageExportOptions.DefaultTemplate;
        var compact = template
            .Replace("{original_name}", "%n", StringComparison.Ordinal)
            .Replace("{sequence}", "%i", StringComparison.Ordinal)
            .Replace("{scene_name}", "%s", StringComparison.Ordinal)
            .Replace("{created_date}", "%d", StringComparison.Ordinal)
            .Replace("{created_time}", "%t", StringComparison.Ordinal)
            .Replace("{element_type}", "%e", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(compact,
            "\\{sequence:(0[2-9]|1[0-2])\\}", "%$1i");
    }

    public static ImageExportPlan CreatePlan(ImageExportRequest request, ImageExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var directory = ValidateDirectory(options.Directory);
        var images = request.Snapshot.Document.Images
            .Where(item => request.SelectedIds is null || request.SelectedIds.Contains(item.Id))
            .OrderByDescending(item => item.ZIndex).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Concat(request.SelectedIds is null
                ? request.Snapshot.Document.Materials.OrderByDescending(m => m.SortOrder).ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => m.ToImage())
                : Enumerable.Empty<BoardItem>()).ToArray();
        if (images.Length == 0) throw new InvalidOperationException("当前范围内没有可导出的图片。");
        if (string.IsNullOrWhiteSpace(options.NamingTemplate)) throw new InvalidDataException("命名模板不能为空。");

        var sequenceDigits = Math.Max(1, images.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        var entries = new List<ImageExportPlanEntry>(images.Length);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < images.Length; index++)
        {
            var item = images[index];
            if (!request.Snapshot.AssetPaths.TryGetValue(item.AssetId, out var sourcePath) ||
                !File.Exists(sourcePath) || ImageFileFormatService.FromFile(sourcePath) is not { } sourceExtension)
                throw new FileNotFoundException($"图片“{DisplayName(item)}”的原始文件不可用。", sourcePath);
            var extension = Extension(options.Format, sourceExtension);
            extensions.Add(extension);
            var name = Expand(options.NamingTemplate, item, request.Snapshot.Document.Name,
                request.ElementTypeName, index + 1, sequenceDigits);
            ValidateFileName(name);
            var destination = Path.Combine(directory, name + extension);
            if (!destinations.Add(destination))
                throw new InvalidDataException($"命名模板会生成重复文件名：“{name + extension}”。请加入序号或其他区分字段。");
            entries.Add(new ImageExportPlanEntry(item, sourcePath, destination, extension, index + 1));
        }
        var conflicts = entries.Select(entry => entry.DestinationPath).Where(File.Exists).ToArray();
        return new ImageExportPlan(entries, conflicts, options.Format == ImageExportFormat.Original && extensions.Count > 1);
    }

    public static string Preview(ImageExportRequest request, ImageExportOptions options, out bool mixedOriginalExtensions)
    {
        var plan = CreatePlan(request, options);
        mixedOriginalExtensions = plan.HasMixedOriginalExtensions;
        return Path.GetFileName(plan.Entries[0].DestinationPath);
    }

    public static string Expand(string template, BoardItem item, string sceneName, string elementType,
        int sequence, int batchDigits)
    {
        var result = new StringBuilder(template.Length + 32);
        for (var index = 0; index < template.Length;)
        {
            if (template[index] == '%')
            {
                if (index + 1 >= template.Length) throw new InvalidDataException("命名模板末尾的“%”不完整；普通百分号请写成“%%”。");
                if (template[index + 1] == '%') { result.Append('%'); index += 2; continue; }
                if (template[index + 1] == '0')
                {
                    var end = index + 2;
                    while (end < template.Length && char.IsDigit(template[end])) end++;
                    if (end >= template.Length || template[end] != 'i' ||
                        !int.TryParse(template[(index + 2)..end], out var width) || width is < 2 or > 12)
                        throw new InvalidDataException("补零序号格式无效，请使用 %02i、%03i 等格式。");
                    result.Append(sequence.ToString("D" + Math.Max(batchDigits, width),
                        System.Globalization.CultureInfo.InvariantCulture));
                    index = end + 1;
                    continue;
                }
                result.Append(template[index + 1] switch
                {
                    'n' => SafeValue(DisplayName(item)),
                    'i' => sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    's' => SafeValue(string.IsNullOrWhiteSpace(sceneName) ? "未命名" : sceneName.Trim()),
                    'd' => item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd"),
                    't' => item.CreatedUtc.ToLocalTime().ToString("HH-mm-ss"),
                    'e' => SafeValue(elementType),
                    _ => throw new InvalidDataException($"未知的命名符号“%{template[index + 1]}”。")
                });
                index += 2;
                continue;
            }
            if (template[index] == '{')
            {
                if (index + 1 < template.Length && template[index + 1] == '{')
                { result.Append('{'); index += 2; continue; }
                var end = template.IndexOf('}', index + 1);
                if (end < 0) throw new InvalidDataException("命名模板中存在未闭合的“{”。");
                var key = template[(index + 1)..end];
                result.Append(Value(key, item, sceneName, elementType, sequence, batchDigits));
                index = end + 1;
                continue;
            }
            if (template[index] == '}')
            {
                if (index + 1 < template.Length && template[index + 1] == '}')
                { result.Append('}'); index += 2; continue; }
                throw new InvalidDataException("命名模板中存在未转义的“}”；普通花括号请写成“}}”。");
            }
            result.Append(template[index++]);
        }
        return result.ToString().Trim();
    }

    private static string Value(string key, BoardItem item, string sceneName, string elementType,
        int sequence, int batchDigits)
    {
        if (key == "original_name") return SafeValue(DisplayName(item));
        if (key == "sequence") return sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (key == "scene_name") return SafeValue(string.IsNullOrWhiteSpace(sceneName) ? "未命名" : sceneName.Trim());
        if (key == "created_date") return item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd");
        if (key == "created_time") return item.CreatedUtc.ToLocalTime().ToString("HH-mm-ss");
        if (key == "element_type") return SafeValue(elementType);
        if (key.StartsWith("sequence:", StringComparison.Ordinal))
        {
            var format = key[9..];
            if (format.Length < 2 || format[0] != '0' ||
                !int.TryParse(format, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var width) || width is < 2 or > 12)
                throw new InvalidDataException($"序号格式“{{{key}}}”无效，请使用 {{sequence:02}}、{{sequence:03}} 等格式。");
            return sequence.ToString("D" + Math.Max(batchDigits, width), System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new InvalidDataException($"未知的命名字段“{{{key}}}”。");
    }

    private static string Extension(ImageExportFormat format, string original) => format switch
    {
        ImageExportFormat.Original => original == ".jpeg" ? ".jpg" : original,
        ImageExportFormat.Png => ".png",
        ImageExportFormat.Jpg => ".jpg",
        ImageExportFormat.Bmp => ".bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    private static string DisplayName(BoardItem item) => string.IsNullOrWhiteSpace(item.LayerName)
        ? $"图片 {item.CreatedUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}" : item.LayerName.Trim();
    private static string SafeValue(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character));
    }
    private static string ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("请选择保存位置。");
        try { return Path.GetFullPath(directory.Trim()); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("保存位置不是有效的文件夹路径。"); }
    }
    private static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("命名模板生成了空文件名。");
        if (name.Length > 180) throw new InvalidDataException("命名模板生成的文件名过长。");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new InvalidDataException($"命名模板生成了非法文件名：“{name}”。");
        var reserved = new HashSet<string>(["CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"], StringComparer.OrdinalIgnoreCase);
        if (reserved.Contains(name.Split('.')[0])) throw new InvalidDataException($"“{name}”是 Windows 保留文件名。");
    }
}
