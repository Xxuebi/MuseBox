namespace ScreenshotCollector.Services;

public static class ImageLinkService
{
    public static string NormalizeWeb(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "";
        if (!value.Contains("://") && !value.Contains(':')) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("网页地址必须是有效的 http 或 https 地址。");
        return uri.AbsoluteUri;
    }

    public static string NormalizeFile(string value)
    {
        value = value.Trim().Trim('"');
        if (value.Length == 0) return "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile) value = uri.LocalPath;
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("请选择文件或文件夹，或填写完整路径。");
        return Path.GetFullPath(value);
    }
}
