using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class IconResourceChecks
{
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private static readonly IntPtr RtIcon = new(3);
    private static readonly IntPtr RtGroupIcon = new(14);
    private static readonly int[] RequiredSizes = [16, 24, 32, 48, 64, 128, 256];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void VerifySourceAndWpfResource(string sourceIconPath)
    {
        var sourceBytes = File.ReadAllBytes(sourceIconPath);
        _ = ReadIcoFrames(sourceBytes, sourceIconPath);
        VerifyDecodedIco(sourceBytes, sourceIconPath);

        var resourceUri = new Uri(
            "pack://application:,,,/Sidetalk;component/Assets/Sidetalk.ico",
            UriKind.Absolute);
        var resource = Application.GetResourceStream(resourceUri)
            ?? throw new InvalidOperationException("Sidetalk.ico is not available as a WPF Resource.");
        byte[] resourceBytes;
        using (resource.Stream)
        using (var buffer = new MemoryStream())
        {
            resource.Stream.CopyTo(buffer);
            resourceBytes = buffer.ToArray();
        }

        if (!resourceBytes.AsSpan().SequenceEqual(sourceBytes))
            throw new InvalidOperationException("WPF pack resource differs from the source ICO.");

        VerifyDecodedIco(resourceBytes, "WPF pack resource");
    }

    public static void VerifyPublishedExecutable(string publishedExecutablePath, string? sourceIconPath = null)
    {
        if (!File.Exists(publishedExecutablePath))
            throw new InvalidOperationException($"Published executable not found: {publishedExecutablePath}");

        var sourceFrames = sourceIconPath is null
            ? null
            : ReadIcoFrames(File.ReadAllBytes(sourceIconPath), sourceIconPath);

        var module = LoadLibraryEx(
            publishedExecutablePath,
            IntPtr.Zero,
            LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
            throw new InvalidOperationException("Could not load the published executable as a read-only image resource.");

        try
        {
            var found = false;
            EnumResourceNameDelegate callback = (_, _, resourceName, _) =>
            {
                if (found)
                    return false;

                var group = TryReadResource(module, resourceName, RtGroupIcon);
                if (group is not null && IsMatchingGroupIcon(module, group, sourceFrames))
                {
                    found = true;
                    return false;
                }

                return true;
            };

            _ = EnumResourceNames(module, RtGroupIcon, callback, IntPtr.Zero);
            if (!found)
                throw new InvalidOperationException(
                    "Published executable has no RT_GROUP_ICON containing the expected seven SIDETALK frames.");
        }
        finally
        {
            _ = FreeLibrary(module);
        }
    }

    private static Dictionary<int, byte[]> ReadIcoFrames(byte[] bytes, string label)
    {
        if (bytes.Length < 6)
            throw new InvalidOperationException($"{label}: ICO header is truncated.");

        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)) != 1)
            throw new InvalidOperationException($"{label}: invalid ICO signature.");

        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        if (count != RequiredSizes.Length)
            throw new InvalidOperationException($"{label}: expected {RequiredSizes.Length} icon frames, got {count}.");

        var expected = new HashSet<int>(RequiredSizes);
        var actual = new Dictionary<int, byte[]>();
        for (var index = 0; index < count; index++)
        {
            var entryOffset = 6 + (index * 16);
            if (entryOffset + 16 > bytes.Length)
                throw new InvalidOperationException($"{label}: frame directory is truncated.");

            var width = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
            var height = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
            if (width != height || !expected.Contains(width) || !actual.TryAdd(width, []))
                throw new InvalidOperationException($"{label}: unexpected frame size {width}x{height}.");

            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 8, 4));
            var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entryOffset + 12, 4));
            if (payloadLength < 33 ||
                payloadOffset > (uint)bytes.Length ||
                payloadLength > (uint)(bytes.Length - (int)payloadOffset))
                throw new InvalidOperationException($"{label}: invalid PNG payload for {width}x{height}.");

            var png = bytes.AsSpan(checked((int)payloadOffset), checked((int)payloadLength));
            VerifyPngFrame(png, width, height, label);
            actual[width] = png.ToArray();
        }

        if (actual.Count != expected.Count)
            throw new InvalidOperationException($"{label}: one or more required icon sizes are missing.");
        return actual;
    }

    private static void VerifyPngFrame(ReadOnlySpan<byte> png, int width, int height, string label)
    {
        if (png.Length < 33 ||
            !png[..8].SequenceEqual(PngSignature) ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidOperationException($"{label}: frame {width}x{height} is not PNG data.");

        var pngWidth = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var pngHeight = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        var colorType = png[25];
        if (pngWidth != width || pngHeight != height || colorType != 6)
            throw new InvalidOperationException($"{label}: frame {width}x{height} is not RGBA PNG data.");
    }

    private static void VerifyDecodedIco(byte[] bytes, string label)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = new IconBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        if (decoder.Frames.Count != RequiredSizes.Length)
            throw new InvalidOperationException(
                $"{label}: WPF decoded {decoder.Frames.Count} frames instead of {RequiredSizes.Length}.");

        var expected = new HashSet<int>(RequiredSizes);
        var actual = new HashSet<int>();
        foreach (var frame in decoder.Frames)
        {
            if (!expected.Contains(frame.PixelWidth) ||
                frame.PixelWidth != frame.PixelHeight ||
                !actual.Add(frame.PixelWidth))
                throw new InvalidOperationException(
                    $"{label}: WPF decoded an unexpected frame size {frame.PixelWidth}x{frame.PixelHeight}.");

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);

            var hasTransparentPixel = false;
            for (var offset = 3; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset] < byte.MaxValue)
                {
                    hasTransparentPixel = true;
                    break;
                }
            }

            if (!hasTransparentPixel)
                throw new InvalidOperationException($"{label}: frame {frame.PixelWidth}x{frame.PixelHeight} lost transparency.");
        }
    }

    private static bool IsMatchingGroupIcon(
        IntPtr module,
        byte[] group,
        IReadOnlyDictionary<int, byte[]>? sourceFrames)
    {
        if (group.Length < 6 ||
            BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(0, 2)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(2, 2)) != 1)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4, 2));
        if (count != RequiredSizes.Length)
            return false;

        var expected = new HashSet<int>(RequiredSizes);
        var actual = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var entryOffset = 6 + (index * 14);
            if (entryOffset + 14 > group.Length)
                return false;

            var width = group[entryOffset] == 0 ? 256 : group[entryOffset];
            var height = group[entryOffset + 1] == 0 ? 256 : group[entryOffset + 1];
            var iconId = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(entryOffset + 12, 2));
            if (width != height || !expected.Contains(width) || !actual.Add(width))
                return false;

            var payload = TryReadResource(module, new IntPtr(iconId), RtIcon);
            if (payload is null)
                return false;

            if (sourceFrames is not null &&
                (!sourceFrames.TryGetValue(width, out var expectedPayload) ||
                 !payload.AsSpan().SequenceEqual(expectedPayload)))
                return false;
        }

        return actual.SetEquals(expected);
    }

    private static byte[]? TryReadResource(IntPtr module, IntPtr name, IntPtr type)
    {
        var resource = FindResource(module, name, type);
        if (resource == IntPtr.Zero)
            return null;

        var size = SizeofResource(module, resource);
        var loaded = LoadResource(module, resource);
        var pointer = LockResource(loaded);
        if (size == 0 || loaded == IntPtr.Zero || pointer == IntPtr.Zero)
            return null;

        var data = new byte[checked((int)size)];
        Marshal.Copy(pointer, data, 0, data.Length);
        return data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate bool EnumResourceNameDelegate(
        IntPtr module,
        IntPtr type,
        IntPtr name,
        IntPtr parameter);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNames(
        IntPtr module,
        IntPtr type,
        EnumResourceNameDelegate callback,
        IntPtr parameter);

    [DllImport("kernel32.dll", EntryPoint = "FindResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", EntryPoint = "SizeofResource", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", EntryPoint = "LoadResource", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", EntryPoint = "LockResource", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr resource);
}
