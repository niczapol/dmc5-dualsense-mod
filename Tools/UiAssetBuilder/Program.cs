using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DMC5DualSense.UiAssetBuilder;

internal static class Program
{
    private const int DdsDx10PayloadOffset = 148;
    private const uint DxgiBc7Srgb = 99;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            return args[0].ToLowerInvariant() switch
            {
                "build" => Build(args),
                "patch-markers" => PatchMarkers(args),
                "patch-bc7" => PatchBc7(args),
                "tex-to-dds" => TexToDds(args),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Build(string[] args)
    {
        if (args.Length != 6)
            throw new ArgumentException(
                "build requires: <controller.png> <ui0010-base.png> <ui4002-base.png> " +
                "<ui8013-base.png> <output-directory>");

        var outputDirectory = Path.GetFullPath(args[5]);
        Directory.CreateDirectory(outputDirectory);

        using var sourceController = LoadArgb(args[1]);
        using var controller = TintController(sourceController);
        using var prompts = BuildPromptAtlas(LoadArgb(args[2]));
        using var large = BuildControllerAtlas(
            LoadArgb(args[3]), controller,
            new Rectangle(0, 0, 720, 452),
            new Rectangle(0, 468, 720, 452),
            new Rectangle(8, 24, 700, 389),
            new Rectangle(8, 492, 700, 389),
            outlineRadius: 3);
        using var small = BuildControllerAtlas(
            LoadArgb(args[4]), controller,
            new Rectangle(0, 0, 288, 180),
            new Rectangle(0, 184, 288, 180),
            new Rectangle(3, 12, 280, 156),
            new Rectangle(3, 196, 280, 156),
            outlineRadius: 2);

        SavePng(prompts, Path.Combine(outputDirectory, "ui0010_iam.png"));
        SavePng(large, Path.Combine(outputDirectory, "ui4002_00_iam.png"));
        SavePng(small, Path.Combine(outputDirectory, "ui8013_iam.png"));
        return 0;
    }

    private static int PatchBc7(string[] args)
    {
        if (args.Length < 5)
            throw new ArgumentException(
                "patch-bc7 requires: <base.tex> <encoded.dds> <output.tex> <left,top,right,bottom> [...]");

        var baseBytes = File.ReadAllBytes(args[1]);
        var ddsBytes = File.ReadAllBytes(args[2]);
        if (ddsBytes.Length < DdsDx10PayloadOffset ||
            BitConverter.ToUInt32(ddsBytes, 0) != 0x20534444 ||
            System.Text.Encoding.ASCII.GetString(ddsBytes, 84, 4) != "DX10")
            throw new InvalidDataException("Input DDS is not a DX10 DDS file.");

        var height = checked((int)BitConverter.ToUInt32(ddsBytes, 12));
        var width = checked((int)BitConverter.ToUInt32(ddsBytes, 16));
        var mipCount = BitConverter.ToUInt32(ddsBytes, 28);
        var format = BitConverter.ToUInt32(ddsBytes, 128);
        if (width % 4 != 0 || height % 4 != 0 || mipCount != 1 || format != DxgiBc7Srgb)
            throw new InvalidDataException(
                $"Expected one-mip BC7_UNORM_SRGB DDS, got {width}x{height}, mips={mipCount}, DXGI={format}.");

        var payloadLength = checked(width * height);
        if (ddsBytes.Length != DdsDx10PayloadOffset + payloadLength)
            throw new InvalidDataException("Unexpected DDS payload length.");
        var texHeaderLength = baseBytes.Length - payloadLength;
        if (texHeaderLength != 48)
            throw new InvalidDataException(
                $"Expected a 48-byte RE Engine TEX header, got {texHeaderLength} bytes.");

        for (var index = 4; index < args.Length; index++)
        {
            var rectangle = ParseRectangle(args[index], width, height);
            CopyBc7Blocks(ddsBytes, DdsDx10PayloadOffset, baseBytes, texHeaderLength,
                width, rectangle);
        }

        var outputPath = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, baseBytes);
        Console.WriteLine($"Patched {args.Length - 4} BC7 region(s): {outputPath}");
        return 0;
    }

    private static int PatchMarkers(string[] args)
    {
        if (args.Length != 4)
            throw new ArgumentException(
                "patch-markers requires: <ui8013-atlas.png> <dualsense-assets-directory> <output.png>");

        using var atlas = LoadArgb(args[1]);
        if (atlas.Width != 512 || atlas.Height != 512)
            throw new InvalidDataException(
                $"ui8013 marker atlas must be 512x512, got {atlas.Width}x{atlas.Height}.");

        var assets = Path.GetFullPath(args[2]);
        ReplaceMarker(atlas, new Rectangle(288, 104, 52, 26),
            Path.Combine(assets, "DualSense_L2-Active.png"));
        ReplaceMarker(atlas, new Rectangle(340, 104, 52, 26),
            Path.Combine(assets, "DualSense_L1-Active.png"));
        ReplaceMarker(atlas, new Rectangle(392, 104, 104, 78),
            Path.Combine(assets, "DualSense_Touchpad-Click.png"));

        var output = Path.GetFullPath(args[3]);
        SavePng(atlas, output);
        return 0;
    }

    private static void ReplaceMarker(Bitmap atlas, Rectangle uvCell, string assetPath)
    {
        using var asset = LoadArgb(assetPath);
        using var scaled = Scale(asset, uvCell.Size);
        Clear(atlas, uvCell);
        using var graphics = CreateGraphics(atlas);
        DrawPixelExact(graphics, scaled, uvCell.Location);
    }

    private static int TexToDds(string[] args)
    {
        if (args.Length != 4)
            throw new ArgumentException("tex-to-dds requires: <template.dds> <input.tex> <output.dds>");
        var ddsBytes = File.ReadAllBytes(args[1]);
        var texBytes = File.ReadAllBytes(args[2]);
        var payloadLength = ddsBytes.Length - DdsDx10PayloadOffset;
        if (payloadLength <= 0 || texBytes.Length != payloadLength + 48)
            throw new InvalidDataException("DDS and TEX payload lengths do not match.");
        Buffer.BlockCopy(texBytes, 48, ddsBytes, DdsDx10PayloadOffset, payloadLength);
        var output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, ddsBytes);
        return 0;
    }

    private static Rectangle ParseRectangle(string value, int width, int height)
    {
        var parts = value.Split(',');
        if (parts.Length != 4 || parts.Any(part => !int.TryParse(part, out _)))
            throw new ArgumentException($"Invalid rectangle: {value}");
        var coordinates = parts.Select(int.Parse).ToArray();
        var left = Math.Clamp(coordinates[0] / 4 * 4, 0, width);
        var top = Math.Clamp(coordinates[1] / 4 * 4, 0, height);
        var right = Math.Clamp((coordinates[2] + 3) / 4 * 4, 0, width);
        var bottom = Math.Clamp((coordinates[3] + 3) / 4 * 4, 0, height);
        if (right <= left || bottom <= top)
            throw new ArgumentException($"Empty rectangle: {value}");
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static void CopyBc7Blocks(
        byte[] source,
        int sourceOffset,
        byte[] destination,
        int destinationOffset,
        int width,
        Rectangle rectangle)
    {
        const int bytesPerBlock = 16;
        var blocksPerRow = width / 4;
        var leftBlock = rectangle.Left / 4;
        var rightBlock = rectangle.Right / 4;
        for (var blockY = rectangle.Top / 4; blockY < rectangle.Bottom / 4; blockY++)
        {
            var blockIndex = blockY * blocksPerRow + leftBlock;
            var byteCount = (rightBlock - leftBlock) * bytesPerBlock;
            Buffer.BlockCopy(source, sourceOffset + blockIndex * bytesPerBlock,
                destination, destinationOffset + blockIndex * bytesPerBlock, byteCount);
        }
    }

    private static Bitmap BuildPromptAtlas(Bitmap atlas)
    {
        if (atlas.Width != 1024 || atlas.Height != 2048)
            throw new InvalidDataException($"ui0010 must be 1024x2048, got {atlas.Width}x{atlas.Height}.");

        Clear(atlas, Rectangle.FromLTRB(312, 164, 480, 248));
        using var options = DrawSystemButton(create: false);
        using var create = DrawSystemButton(create: true);
        using var graphics = CreateGraphics(atlas);
        DrawPixelExact(graphics, options, new Point(320, 168));
        DrawPixelExact(graphics, create, new Point(400, 168));
        return atlas;
    }

    private static Bitmap DrawSystemButton(bool create)
    {
        const int scale = 4;
        using var highResolution = new Bitmap(80 * scale, 80 * scale, PixelFormat.Format32bppArgb);
        using (var graphics = CreateGraphics(highResolution))
        using (var cyan = new Pen(Color.FromArgb(255, 112, 216, 228), 2 * scale))
        using (var light = new Pen(Color.FromArgb(255, 202, 207, 207), 2 * scale))
        using (var dark = new SolidBrush(Color.FromArgb(255, 31, 31, 34)))
        {
            var state = graphics.Save();
            graphics.TranslateTransform(40 * scale, 43 * scale);
            graphics.RotateTransform(create ? -13 : 13);
            graphics.TranslateTransform(-40 * scale, -43 * scale);
            var pill = new Rectangle(31 * scale, 24 * scale, 17 * scale, 38 * scale);
            graphics.FillRoundedRectangle(dark, pill, 7 * scale);
            graphics.DrawRoundedRectangle(cyan, pill, 7 * scale);
            graphics.Restore(state);

            if (create)
            {
                foreach (var endpoint in new[] { new Point(40, 8), new Point(30, 12), new Point(50, 12) })
                    graphics.DrawLine(light, 40 * scale, 20 * scale,
                        endpoint.X * scale, endpoint.Y * scale);
            }
            else
            {
                foreach (var y in new[] { 11, 16, 21 })
                    graphics.DrawLine(light, 31 * scale, y * scale, 49 * scale, y * scale);
            }
        }

        var result = new Bitmap(80, 80, PixelFormat.Format32bppArgb);
        using var output = CreateGraphics(result);
        output.DrawImage(highResolution, new Rectangle(0, 0, 80, 80));
        return result;
    }

    private static Bitmap BuildControllerAtlas(
        Bitmap atlas,
        Bitmap controller,
        Rectangle clearTop,
        Rectangle clearBottom,
        Rectangle topPlacement,
        Rectangle bottomPlacement,
        int outlineRadius)
    {
        Clear(atlas, clearTop);
        Clear(atlas, clearBottom);

        using var top = Scale(controller, topPlacement.Size);
        using var bottom = Scale(controller, bottomPlacement.Size);
        Console.WriteLine(
            $"Atlas {atlas.Width}x{atlas.Height}: controller {controller.Width}x{controller.Height}, " +
            $"scaled {top.Width}x{top.Height}, top={topPlacement}, bottom={bottomPlacement}");
        using var outline = CreateCyanOutline(bottom, outlineRadius, 190);
        using var graphics = CreateGraphics(atlas);
        DrawPixelExact(graphics, top, topPlacement.Location);
        DrawPixelExact(graphics, outline, bottomPlacement.Location);
        DrawPixelExact(graphics, bottom, bottomPlacement.Location);
        return atlas;
    }

    private static Bitmap TintController(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = CreateGraphics(result)) DrawPixelExact(graphics, source, Point.Empty);

        var rectangle = new Rectangle(0, 0, result.Width, result.Height);
        var data = result.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (var y = 0; y < data.Height; y++)
            {
                for (var x = 0; x < data.Width; x++)
                {
                    var offset = y * data.Stride + x * 4;
                    if (pixels[offset + 3] == 0) continue;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    if (blue > red * 1.25 && blue > 70)
                    {
                        pixels[offset] = 190;
                        pixels[offset + 1] = 135;
                        pixels[offset + 2] = 24;
                    }
                    else
                    {
                        pixels[offset] = ClampByte(20 + blue * 0.35);
                        pixels[offset + 1] = ClampByte(17 + green * 0.35);
                        pixels[offset + 2] = ClampByte(18 + red * 0.35);
                    }
                }
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            result.UnlockBits(data);
        }
        return result;
    }

    private static Bitmap Scale(Bitmap source, Size size)
    {
        var result = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using var graphics = CreateGraphics(result);
        graphics.DrawImage(source, new Rectangle(Point.Empty, size),
            new Rectangle(Point.Empty, source.Size), GraphicsUnit.Pixel);
        return result;
    }

    private static Bitmap CreateCyanOutline(Bitmap source, int radius, byte opacity)
    {
        var width = source.Width;
        var height = source.Height;
        var alpha = ReadAlpha(source);
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rectangle = new Rectangle(0, 0, width, height);
        var data = result.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[Math.Abs(data.Stride) * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    byte maximum = 0;
                    byte minimum = 255;
                    for (var sampleY = Math.Max(0, y - radius);
                         sampleY <= Math.Min(height - 1, y + radius); sampleY++)
                    {
                        for (var sampleX = Math.Max(0, x - radius);
                             sampleX <= Math.Min(width - 1, x + radius); sampleX++)
                        {
                            var value = alpha[sampleY * width + sampleX];
                            maximum = Math.Max(maximum, value);
                            minimum = Math.Min(minimum, value);
                        }
                    }

                    var offset = y * data.Stride + x * 4;
                    pixels[offset] = 235;
                    pixels[offset + 1] = 174;
                    pixels[offset + 2] = 0;
                    pixels[offset + 3] = (byte)((maximum - minimum) * opacity / 255);
                }
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            result.UnlockBits(data);
        }
        return result;
    }

    private static byte[] ReadAlpha(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var alpha = new byte[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    alpha[y * bitmap.Width + x] = pixels[y * data.Stride + x * 4 + 3];
            return alpha;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap LoadArgb(string path)
    {
        using var source = new Bitmap(Path.GetFullPath(path));
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = CreateGraphics(result);
        DrawPixelExact(graphics, source, Point.Empty);
        return result;
    }

    private static void DrawPixelExact(Graphics graphics, Image image, Point location)
    {
        graphics.DrawImage(image,
            new Rectangle(location, image.Size),
            new Rectangle(Point.Empty, image.Size),
            GraphicsUnit.Pixel);
    }

    private static Graphics CreateGraphics(Image image)
    {
        var graphics = Graphics.FromImage(image);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        return graphics;
    }

    private static void Clear(Bitmap bitmap, Rectangle rectangle)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using var transparent = new SolidBrush(Color.Transparent);
        graphics.FillRectangle(transparent, rectangle);
    }

    private static void SavePng(Bitmap bitmap, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine(path);
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage:\n" +
            "  build <controller.png> <ui0010-base.png> <ui4002-base.png> <ui8013-base.png> <output-dir>\n" +
            "  patch-markers <ui8013-atlas.png> <dualsense-assets-directory> <output.png>\n" +
            "  patch-bc7 <base.tex> <encoded.dds> <output.tex> <left,top,right,bottom> [...]\n" +
            "  tex-to-dds <template.dds> <input.tex> <output.dds>");
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle rectangle, int radius)
    {
        using var path = RoundedRectangle(rectangle, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle rectangle, int radius)
    {
        using var path = RoundedRectangle(rectangle, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
