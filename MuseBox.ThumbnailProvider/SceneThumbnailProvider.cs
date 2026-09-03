using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MuseBox.ThumbnailProvider;

internal enum WtsAlphaType
{
    Unknown = 0,
    Rgb = 1,
    Argb = 2
}

[ComImport]
[Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    [PreserveSig]
    int Initialize([MarshalAs(UnmanagedType.Interface)] IStream stream, uint mode);
}

[ComImport]
[Guid("E357FCCD-A995-4576-B01F-234630154E96")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IThumbnailProvider
{
    [PreserveSig]
    int GetThumbnail(uint size, out IntPtr bitmap, out WtsAlphaType alphaType);
}

[ComVisible(true)]
[Guid(SceneThumbnailProvider.ClassId)]
[ProgId("MuseBox.SceneThumbnailProvider")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SceneThumbnailProvider : IInitializeWithStream, IThumbnailProvider
{
    public const string ClassId = "6F67433A-1EA6-47D0-982B-30EFAE588F38";
    private const int Success = 0;
    private const int Failure = unchecked((int)0x80004005);
    private const int AlreadyInitialized = unchecked((int)0x800704DF);
    private const int MaxThumbnailBytes = 16 * 1024 * 1024;
    private const int MaxManifestBytes = 32 * 1024 * 1024;
    private IStream _stream;
    private static string _lastError = string.Empty;

    int IInitializeWithStream.Initialize(IStream stream, uint mode)
    {
        if (stream == null) return Failure;
        if (_stream != null) return AlreadyInitialized;
        _stream = stream;
        return Success;
    }

    int IThumbnailProvider.GetThumbnail(uint size, out IntPtr bitmap, out WtsAlphaType alphaType)
    {
        bitmap = IntPtr.Zero;
        alphaType = WtsAlphaType.Unknown;
        try
        {
            _lastError = string.Empty;
            if (_stream == null) return Fail("stream");
            using (var archiveStream = new ComReadStream(_stream))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, true))
            {
                var entry = archive.GetEntry("scene.json");
                if (entry == null || entry.Length <= 0 || entry.Length > MaxManifestBytes) return Fail("manifest");
                using (var manifest = new MemoryStream((int)entry.Length))
                {
                    using (var source = entry.Open()) CopyBounded(source, manifest, entry.Length, MaxManifestBytes);
                    var encoded = ReadThumbnailValue(Encoding.UTF8.GetString(manifest.ToArray()));
                    if (encoded.Length > (MaxThumbnailBytes * 4 / 3) + 8) return Fail("encoded-length");
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(encoded); }
                    catch (FormatException) { return Fail("base64"); }
                    if (bytes.Length <= 0 || bytes.Length > MaxThumbnailBytes) return Fail("decoded-length");
                    using (var imageStream = new MemoryStream(bytes, false))
                    using (var decoded = Image.FromStream(imageStream, true, true))
                    {
                        if (decoded.Width <= 0 || decoded.Height <= 0 ||
                            decoded.Width > 2048 || decoded.Height > 2048) return Fail("dimensions");
                        var edge = (int)Math.Max(16, Math.Min(size, 1024));
                        using (var rendered = new Bitmap(edge, edge, PixelFormat.Format32bppPArgb))
                        {
                            rendered.SetResolution(96, 96);
                            using (var graphics = Graphics.FromImage(rendered))
                            {
                                graphics.Clear(Color.Transparent);
                                graphics.CompositingMode = CompositingMode.SourceCopy;
                                graphics.CompositingQuality = CompositingQuality.HighQuality;
                                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                                graphics.DrawImage(decoded, new Rectangle(0, 0, edge, edge));
                            }
                            bitmap = DibSection.Create(rendered);
                        }
                    }
                }
            }
            if (bitmap == IntPtr.Zero) return Fail("bitmap");
            alphaType = WtsAlphaType.Argb;
            return Success;
        }
        catch (Exception error)
        {
            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
                bitmap = IntPtr.Zero;
            }
            return Fail(error.GetType().FullName + ": " + error.Message);
        }
    }

    private static int Fail(string error)
    {
        _lastError = error;
        return Failure;
    }

    private static string ReadThumbnailValue(string json)
    {
        const string property = "\"ThumbnailPng\"";
        var propertyIndex = json.IndexOf(property, StringComparison.Ordinal);
        if (propertyIndex < 0) return string.Empty;
        var colon = json.IndexOf(':', propertyIndex + property.Length);
        if (colon < 0) return string.Empty;
        var start = json.IndexOf('"', colon + 1);
        if (start < 0) return string.Empty;
        var end = json.IndexOf('"', start + 1);
        return end > start ? UnescapeJson(json.Substring(start + 1, end - start - 1)) : string.Empty;
    }

    private static string UnescapeJson(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\' || ++index >= value.Length) { result.Append(current); continue; }
            current = value[index];
            if (current == 'u' && index + 4 < value.Length)
            {
                var hex = value.Substring(index + 1, 4);
                if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var character))
                {
                    result.Append((char)character);
                    index += 4;
                    continue;
                }
            }
            switch (current)
            {
                case '"': result.Append('"'); break;
                case '\\': result.Append('\\'); break;
                case '/': result.Append('/'); break;
                case 'b': result.Append('\b'); break;
                case 'f': result.Append('\f'); break;
                case 'n': result.Append('\n'); break;
                case 'r': result.Append('\r'); break;
                case 't': result.Append('\t'); break;
                default: return string.Empty;
            }
        }
        return result.ToString();
    }

    private static void CopyBounded(Stream source, Stream target, long expected, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > expected || total > limit) throw new InvalidDataException();
            target.Write(buffer, 0, read);
        }
        if (total != expected) throw new InvalidDataException();
    }
}

internal sealed class ComReadStream : Stream
{
    private readonly IStream _stream;
    internal ComReadStream(IStream stream)
    {
        _stream = stream;
        Position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            _stream.Stat(out var stat, 1);
            return stat.cbSize;
        }
    }
    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (offset < 0 || count < 0 || buffer.Length - offset < count) throw new ArgumentOutOfRangeException();
        var temporary = offset == 0 ? buffer : new byte[count];
        var readPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            _stream.Read(temporary, count, readPointer);
            var read = Marshal.ReadInt32(readPointer);
            if (offset != 0 && read > 0) Buffer.BlockCopy(temporary, 0, buffer, offset, read);
            return read;
        }
        finally { Marshal.FreeCoTaskMem(readPointer); }
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var positionPointer = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            _stream.Seek(offset, (int)origin, positionPointer);
            return Marshal.ReadInt64(positionPointer);
        }
        finally { Marshal.FreeCoTaskMem(positionPointer); }
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class DibSection
{
    internal static IntPtr Create(Bitmap bitmap)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                Width = bitmap.Width,
                Height = -bitmap.Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)(bitmap.Width * bitmap.Height * 4)
            }
        };
        var handle = NativeMethods.CreateDIBSection(IntPtr.Zero, ref info, 0, out var pixels, IntPtr.Zero, 0);
        if (handle == IntPtr.Zero || pixels == IntPtr.Zero) throw new InvalidOperationException();
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            var row = new byte[rowBytes];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(pixels, y * rowBytes), rowBytes);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    private static class NativeMethods
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);
    }
}

internal static class NativeMethods
{
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr handle);
}
