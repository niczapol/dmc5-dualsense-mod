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
                "patch-prompts" => PatchPrompts(args),
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

    private static int PatchPrompts(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException(
                "patch-prompts requires: <ui0010-atlas.png> <output.png>");

        using var atlas = BuildPromptAtlas(LoadArgb(args[1]));
        SavePng(atlas, Path.GetFullPath(args[2]));
        return 0;
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
                "patch-markers requires: <controller-atlas.png> <dualsense-assets-directory> <output.png>");

        using var atlas = LoadArgb(args[1]);
        if (atlas.Width != atlas.Height || (atlas.Width != 512 && atlas.Width != 1024))
            throw new InvalidDataException(
                $"Controller marker atlas must be 512x512 or 1024x1024, got {atlas.Width}x{atlas.Height}.");

        var assets = Path.GetFullPath(args[2]);
        if (atlas.Width == 512)
        {
            ReplaceMarker(atlas, new Rectangle(288, 104, 52, 26),
                Path.Combine(assets, "DualSense_L2-Active.png"));
            ReplaceMarker(atlas, new Rectangle(340, 104, 52, 26),
                Path.Combine(assets, "DualSense_L1-Active.png"));
            ReplaceMarker(atlas, new Rectangle(392, 104, 104, 78),
                Path.Combine(assets, "DualSense_Touchpad-Click.png"));
        }
        else
        {
            // ui4002 (Settings) uses independent 128x64 shoulder cells. The
            // original PC cells contain Xbox silhouettes even after replacing
            // the controller artwork, so geometry changes alone cannot fix it.
            ReplaceMarker(atlas, new Rectangle(720, 256, 128, 64),
                Path.Combine(assets, "DualSense_L2-Active.png"));
            ReplaceMarker(atlas, new Rectangle(848, 256, 128, 64),
                Path.Combine(assets, "DualSense_L1-Active.png"));
            // The top Settings set ships with a blank Circle fill, while the
            // lower PlayStation set in the same atlas already has the exact
            // clean Circle marker used alongside Triangle/Cross/Square. Copy
            // that native cell 1:1 instead of drawing an approximate ellipse.
            CopyMarkerCell(atlas,
                new Rectangle(912, 468, 64, 64),
                new Rectangle(912, 0, 64, 64));
        }

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

    private static void CopyMarkerCell(
        Bitmap atlas, Rectangle sourceCell, Rectangle destinationCell)
    {
        if (sourceCell.Size != destinationCell.Size)
            throw new ArgumentException("Source and destination marker cells must have equal size.");
        using var marker = atlas.Clone(sourceCell, PixelFormat.Format32bppArgb);
        Clear(atlas, destinationCell);
        using var graphics = CreateGraphics(atlas);
        DrawPixelExact(graphics, marker, destinationCell.Location);
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

        // DMC5's stock 80x80 controller cells contain opaque cyan/white fringe
        // pixels outside the actual prompts. They become especially obvious in
        // Settings and the pause legend. Rebuild the complete controller block
        // on transparent cells instead of trying to colour-key compressed BC7
        // debris after the fact.
        var cells = new Dictionary<Point, Bitmap>
        {
            [new(0, 0)] = DrawFaceButton(FaceGlyph.Cross),
            [new(80, 0)] = DrawFaceButton(FaceGlyph.Circle),
            [new(160, 0)] = DrawFaceButton(FaceGlyph.Square),
            [new(240, 0)] = DrawFaceButton(FaceGlyph.Triangle),
            [new(320, 0)] = DrawDpadPrompt(),

            [new(0, 80)] = DrawDpadPrompt(Direction.Up),
            [new(80, 80)] = DrawDpadPrompt(Direction.Down),
            [new(160, 80)] = DrawDpadPrompt(Direction.Left),
            [new(240, 80)] = DrawDpadPrompt(Direction.Right),
            [new(320, 80)] = DrawDpadPrompt(Direction.Left | Direction.Right),
            [new(400, 80)] = DrawDpadPrompt(Direction.Up | Direction.Down),

            [new(0, 160)] = DrawStickPrompt("L"),
            [new(80, 160)] = DrawStickPrompt("R"),
            [new(160, 160)] = DrawShoulderPrompt("L1"),
            [new(240, 160)] = DrawShoulderPrompt("R1"),
            [new(320, 160)] = DrawSystemButton(create: false),
            [new(400, 160)] = DrawTouchpadButton(),

            [new(0, 240)] = DrawStickPrompt("L3"),
            [new(80, 240)] = DrawStickPrompt("R3"),
            [new(160, 240)] = DrawShoulderPrompt("L2", trigger: true),
            [new(240, 240)] = DrawShoulderPrompt("R2", trigger: true),

            [new(0, 320)] = DrawStickPrompt("L", Direction.Left | Direction.Right),
            [new(80, 320)] = DrawStickPrompt("L", Direction.Up | Direction.Down),
            [new(160, 320)] = DrawStickPrompt("L", Direction.Down),
            [new(240, 320)] = DrawStickPrompt("R", Direction.Down),
            [new(320, 320)] = DrawStickPrompt("L", Direction.Up),
            [new(400, 320)] = DrawStickPrompt("R", Direction.Up),

            [new(0, 400)] = DrawStickPrompt("R", Direction.Left | Direction.Right),
            [new(80, 400)] = DrawStickPrompt("R", Direction.Up | Direction.Down),
            [new(160, 400)] = DrawStickPrompt("L", Direction.Left),
            [new(240, 400)] = DrawStickPrompt("L", Direction.Right),
            [new(320, 400)] = DrawStickPrompt("R", Direction.Left),
            [new(400, 400)] = DrawStickPrompt("R", Direction.Right)
        };

        using var graphics = CreateGraphics(atlas);
        foreach (var pair in cells)
        {
            Clear(atlas, new Rectangle(pair.Key, new Size(80, 80)));
            DrawPixelExact(graphics, pair.Value, pair.Key);
            pair.Value.Dispose();
        }
        return atlas;
    }

    [Flags]
    private enum Direction
    {
        None = 0,
        Up = 1,
        Down = 2,
        Left = 4,
        Right = 8
    }

    private enum FaceGlyph
    {
        Cross,
        Circle,
        Square,
        Triangle
    }

    private static Bitmap DrawFaceButton(FaceGlyph glyph)
    {
        return DrawPromptCell((graphics, scale) =>
        {
            DrawRoundPromptBase(graphics, scale,
                new Rectangle(9 * scale, 9 * scale, 62 * scale, 62 * scale));
            var colour = glyph switch
            {
                FaceGlyph.Cross => Color.FromArgb(255, 126, 161, 222),
                FaceGlyph.Circle => Color.FromArgb(255, 230, 67, 105),
                FaceGlyph.Square => Color.FromArgb(255, 213, 142, 190),
                FaceGlyph.Triangle => Color.FromArgb(255, 165, 203, 169),
                _ => Color.White
            };
            using var pen = new Pen(colour, 3.0f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            switch (glyph)
            {
                case FaceGlyph.Cross:
                    graphics.DrawLine(pen, 27 * scale, 27 * scale, 53 * scale, 53 * scale);
                    graphics.DrawLine(pen, 53 * scale, 27 * scale, 27 * scale, 53 * scale);
                    break;
                case FaceGlyph.Circle:
                    graphics.DrawEllipse(pen, 25 * scale, 25 * scale, 30 * scale, 30 * scale);
                    break;
                case FaceGlyph.Square:
                    graphics.DrawRectangle(pen, 27 * scale, 27 * scale, 26 * scale, 26 * scale);
                    break;
                case FaceGlyph.Triangle:
                    using (var path = new GraphicsPath())
                    {
                        path.AddPolygon(
                        [
                            new PointF(40 * scale, 24 * scale),
                            new PointF(56 * scale, 54 * scale),
                            new PointF(24 * scale, 54 * scale)
                        ]);
                        graphics.DrawPath(pen, path);
                    }
                    break;
            }
        });
    }

    private static Bitmap DrawDpadPrompt(Direction selected = Direction.None)
    {
        return DrawPromptCell((graphics, scale) =>
        {
            using var fill = new SolidBrush(Color.FromArgb(255, 28, 29, 31));
            using var rim = new Pen(Color.FromArgb(255, 57, 62, 64), 1.5f * scale)
            {
                LineJoin = LineJoin.Round
            };
            foreach (var direction in new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right })
            {
                using var path = DpadArm(direction, scale);
                graphics.FillPath(fill, path);
                graphics.DrawPath(rim, path);
            }

            using var hub = new SolidBrush(Color.FromArgb(255, 39, 42, 45));
            graphics.FillEllipse(hub, 35 * scale, 35 * scale, 10 * scale, 10 * scale);
            foreach (var direction in new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right })
                if (selected.HasFlag(direction)) DrawArrow(graphics, scale, direction, insideDpad: true);
        });
    }

    private static GraphicsPath DpadArm(Direction direction, int scale)
    {
        PointF[] points = direction switch
        {
            Direction.Up =>
            [
                new(32 * scale, 36 * scale), new(32 * scale, 18 * scale),
                new(40 * scale, 10 * scale), new(48 * scale, 18 * scale),
                new(48 * scale, 36 * scale)
            ],
            Direction.Down =>
            [
                new(32 * scale, 44 * scale), new(48 * scale, 44 * scale),
                new(48 * scale, 62 * scale), new(40 * scale, 70 * scale),
                new(32 * scale, 62 * scale)
            ],
            Direction.Left =>
            [
                new(36 * scale, 32 * scale), new(36 * scale, 48 * scale),
                new(18 * scale, 48 * scale), new(10 * scale, 40 * scale),
                new(18 * scale, 32 * scale)
            ],
            Direction.Right =>
            [
                new(44 * scale, 32 * scale), new(62 * scale, 32 * scale),
                new(70 * scale, 40 * scale), new(62 * scale, 48 * scale),
                new(44 * scale, 48 * scale)
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
        var path = new GraphicsPath();
        path.AddPolygon(points);
        path.CloseFigure();
        return path;
    }

    private static Bitmap DrawShoulderPrompt(string label, bool trigger = false)
    {
        return DrawPromptCell((graphics, scale) =>
        {
            using var fill = new SolidBrush(Color.FromArgb(255, 29, 30, 32));
            using var rim = new Pen(Color.FromArgb(255, 83, 88, 88), 1.75f * scale);
            var bounds = trigger
                ? new Rectangle(12 * scale, 19 * scale, 56 * scale, 42 * scale)
                : new Rectangle(9 * scale, 24 * scale, 62 * scale, 32 * scale);
            graphics.FillRoundedRectangle(fill, bounds, (trigger ? 9 : 7) * scale);
            graphics.DrawRoundedRectangle(rim, bounds, (trigger ? 9 : 7) * scale);
            DrawPromptText(graphics, scale, label, trigger ? 19.0f : 20.0f,
                new RectangleF(8 * scale, 15 * scale, 64 * scale, 50 * scale));
        });
    }

    private static Bitmap DrawStickPrompt(string label, Direction arrows = Direction.None)
    {
        return DrawPromptCell((graphics, scale) =>
        {
            var hasArrows = arrows != Direction.None;
            var bounds = hasArrows
                ? new Rectangle(13 * scale, 13 * scale, 54 * scale, 54 * scale)
                : new Rectangle(9 * scale, 9 * scale, 62 * scale, 62 * scale);
            DrawRoundPromptBase(graphics, scale, bounds);
            DrawPromptText(graphics, scale, label, label.Length > 1 ? 17.0f : 23.0f,
                new RectangleF(bounds.X, bounds.Y - 1 * scale, bounds.Width, bounds.Height));
            foreach (var direction in new[] { Direction.Up, Direction.Down, Direction.Left, Direction.Right })
                if (arrows.HasFlag(direction)) DrawArrow(graphics, scale, direction, insideDpad: false);
        });
    }

    private static void DrawRoundPromptBase(Graphics graphics, int scale, Rectangle bounds)
    {
        using var fill = new SolidBrush(Color.FromArgb(255, 28, 29, 31));
        using var rim = new Pen(Color.FromArgb(255, 69, 74, 75), 2.0f * scale);
        graphics.FillEllipse(fill, bounds);
        graphics.DrawEllipse(rim, bounds);
    }

    private static void DrawPromptText(
        Graphics graphics, int scale, string text, float pointSize, RectangleF bounds)
    {
        using var font = new Font(FontFamily.GenericSansSerif, pointSize * scale,
            FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(255, 198, 199, 190));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static void DrawArrow(Graphics graphics, int scale, Direction direction, bool insideDpad)
    {
        var centre = insideDpad
            ? direction switch
            {
                Direction.Up => new PointF(40 * scale, 23 * scale),
                Direction.Down => new PointF(40 * scale, 57 * scale),
                Direction.Left => new PointF(23 * scale, 40 * scale),
                Direction.Right => new PointF(57 * scale, 40 * scale),
                _ => PointF.Empty
            }
            : direction switch
            {
                Direction.Up => new PointF(40 * scale, 7 * scale),
                Direction.Down => new PointF(40 * scale, 73 * scale),
                Direction.Left => new PointF(7 * scale, 40 * scale),
                Direction.Right => new PointF(73 * scale, 40 * scale),
                _ => PointF.Empty
            };
        var radius = (insideDpad ? 6.0f : 5.0f) * scale;
        PointF[] points = direction switch
        {
            Direction.Up =>
            [new(centre.X, centre.Y - radius), new(centre.X - radius, centre.Y + radius),
             new(centre.X + radius, centre.Y + radius)],
            Direction.Down =>
            [new(centre.X, centre.Y + radius), new(centre.X - radius, centre.Y - radius),
             new(centre.X + radius, centre.Y - radius)],
            Direction.Left =>
            [new(centre.X - radius, centre.Y), new(centre.X + radius, centre.Y - radius),
             new(centre.X + radius, centre.Y + radius)],
            Direction.Right =>
            [new(centre.X + radius, centre.Y), new(centre.X - radius, centre.Y - radius),
             new(centre.X - radius, centre.Y + radius)],
            _ => []
        };
        using var brush = new SolidBrush(Color.FromArgb(255, 187, 188, 179));
        graphics.FillPolygon(brush, points);
    }

    private static Bitmap DrawPromptCell(Action<Graphics, int> draw)
    {
        const int scale = 4;
        using var highResolution = new Bitmap(80 * scale, 80 * scale, PixelFormat.Format32bppArgb);
        using (var graphics = CreateGraphics(highResolution)) draw(graphics, scale);
        var result = new Bitmap(80, 80, PixelFormat.Format32bppArgb);
        using var output = CreateGraphics(result);
        output.DrawImage(highResolution, new Rectangle(0, 0, 80, 80));
        CleanTransparentFringe(result);
        return result;
    }

    private static Bitmap DrawTouchpadButton()
    {
        const int scale = 4;
        using var highResolution = new Bitmap(80 * scale, 80 * scale, PixelFormat.Format32bppArgb);
        using (var graphics = CreateGraphics(highResolution))
        using (var cyan = new Pen(Color.FromArgb(255, 112, 216, 228), 2 * scale))
        using (var light = new Pen(Color.FromArgb(255, 202, 207, 207), 1 * scale))
        using (var dark = new SolidBrush(Color.FromArgb(255, 31, 31, 34)))
        {
            var pad = new Rectangle(8 * scale, 20 * scale, 64 * scale, 40 * scale);
            graphics.FillRoundedRectangle(dark, pad, 8 * scale);
            graphics.DrawRoundedRectangle(cyan, pad, 8 * scale);
            // A restrained inner top edge makes the horizontal touch surface
            // readable at the game's small prompt size without resembling
            // either Create or Options.
            graphics.DrawLine(light, 17 * scale, 26 * scale, 63 * scale, 26 * scale);
        }

        var result = new Bitmap(80, 80, PixelFormat.Format32bppArgb);
        using var output = CreateGraphics(result);
        output.DrawImage(highResolution, new Rectangle(0, 0, 80, 80));
        CleanTransparentFringe(result);
        return result;
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
        CleanTransparentFringe(result);
        return result;
    }

    private static void CleanTransparentFringe(Bitmap bitmap, byte alphaCutoff = 24)
    {
        // GDI+ retains RGB values on nearly transparent antialias samples.
        // BC7 may promote those samples into visible coloured pinpricks. Drop
        // only the imperceptible tail and zero its RGB channels before encode;
        // the useful antialiased edge above the cutoff remains intact.
        var rectangle = new Rectangle(Point.Empty, bitmap.Size);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            for (var y = 0; y < data.Height; y++)
            for (var x = 0; x < data.Width; x++)
            {
                var offset = y * data.Stride + x * 4;
                if (bytes[offset + 3] >= alphaCutoff) continue;
                bytes[offset] = 0;
                bytes[offset + 1] = 0;
                bytes[offset + 2] = 0;
                bytes[offset + 3] = 0;
            }
            Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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
            "  patch-prompts <ui0010-atlas.png> <output.png>\n" +
            "  patch-markers <controller-atlas.png> <dualsense-assets-directory> <output.png>\n" +
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
