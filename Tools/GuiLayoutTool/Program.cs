using System.Numerics;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Common;
using ReeLib.Gui;

namespace DMC5DualSense.GuiLayoutTool;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, Vector2> LargePositions =
        new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase)
        {
            ["BtnU"] = new(585.5f, 185.0f),
            ["BtnD"] = new(582.5f, 272.0f),
            ["BtnL"] = new(531.0f, 230.0f),
            ["BtnR"] = new(638.0f, 229.0f),
            ["DirU"] = new(132.5f, 195.0f),
            ["DirD"] = new(132.5f, 266.0f),
            ["DirL"] = new(87.0f, 231.0f),
            ["DirR"] = new(180.5f, 231.0f),
            ["LT"] = new(137.0f, 51.0f),
            ["LB"] = new(137.0f, 79.0f),
            ["RT"] = new(579.0f, 51.0f),
            ["RB"] = new(579.0f, 79.0f),
            ["LStP"] = new(244.0f, 317.0f),
            ["RStP"] = new(473.0f, 317.0f),
            ["CenL"] = new(359.0f, 171.0f),
            ["CenR"] = new(530.0f, 158.0f)
        };

    private sealed record SmallMarker(Vector2 Position, uint Pattern, Vector2? Size = null);

    private static readonly IReadOnlyDictionary<string, SmallMarker> SmallMarkers =
        new Dictionary<string, SmallMarker>(StringComparer.OrdinalIgnoreCase)
        {
            ["c_BtnU"] = new(new(234.0f, 76.5f), 2),
            ["c_BtnD"] = new(new(233.0f, 111.5f), 3),
            ["c_BtnL"] = new(new(212.0f, 94.5f), 4),
            ["c_BtnR"] = new(new(255.0f, 94.0f), 5),
            ["c_DirU"] = new(new(53.0f, 80.5f), 6),
            ["c_DirD"] = new(new(53.0f, 109.0f), 7),
            ["c_DirL"] = new(new(35.0f, 95.0f), 8),
            ["c_DirR"] = new(new(72.0f, 95.0f), 9),
            ["c_LT"] = new(new(54.5f, 23.0f), 11, new(52, 26)),
            ["c_LB"] = new(new(54.5f, 34.0f), 12, new(52, 26)),
            ["c_RT"] = new(new(231.5f, 23.0f), 11, new(-52, 26)),
            ["c_RB"] = new(new(231.5f, 34.0f), 12, new(-52, 26)),
            ["c_LS"] = new(new(97.5f, 129.5f), 13),
            ["c_RS"] = new(new(189.0f, 129.5f), 13),
            ["c_CenL"] = new(new(143.0f, 71.0f), 37, new(120, 56)),
            ["c_CenR"] = new(new(212.0f, 66.0f), 10)
        };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0].Equals("inspect", StringComparison.OrdinalIgnoreCase))
                return Inspect(args[1]);

            if (args.Length != 3 ||
                (!args[0].Equals("large", StringComparison.OrdinalIgnoreCase) &&
                 !args[0].Equals("small", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(
                    "Usage:\n" +
                    "  <large|small> <input.gui.*> <output.gui.*>\n" +
                    "  inspect <input.gui.*>");
                return 2;
            }

            using var gui = new GuiFile(new FileHandler(args[1]));
            if (!gui.Read()) throw new InvalidDataException("Failed to read GUI file.");

            var changed = args[0].Equals("large", StringComparison.OrdinalIgnoreCase)
                ? AlignLarge(gui)
                : AlignSmall(gui);
            if (changed == 0) throw new InvalidDataException("No DualSense layout values were patched.");

            var output = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (!gui.WriteTo(output)) throw new IOException("Failed to write GUI file.");
            Console.WriteLine($"Patched {changed} DualSense layout values: {output}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int Inspect(string path)
    {
        using var gui = new GuiFile(new FileHandler(path));
        if (!gui.Read()) throw new InvalidDataException("Failed to read GUI file.");

        foreach (var container in gui.Containers)
        {
            if (!container.Info.Name.Equals("s_c_XB1", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var clip in container.Clips.Where(item =>
                         LargePositions.ContainsKey(item.name) ||
                         item.name.Contains("Cen", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"CLIP {clip.name}");
                if (clip.clip is null) continue;
                foreach (var track in clip.clip.Tracks)
                {
                    Console.WriteLine($"  TRACK {track.Name}");
                    foreach (var property in track.Properties) DumpProperty(property, "    ");
                }
            }
        }

        if (gui.RootView is not null)
        {
            var smallRoot = FindDisplay(gui.RootView, "c_XB1") ?? gui.RootView;
            foreach (var name in SmallMarkers.Keys)
            {
                var display = FindDisplay(smallRoot, name);
                if (display is null) continue;
                DumpDisplay(display, "");
            }
        }
        foreach (var attribute in gui.AttributeOverrides.Where(item =>
                     SmallMarkers.Keys.Any(name =>
                         item.TargetPath.Contains(name, StringComparison.OrdinalIgnoreCase))))
            Console.WriteLine($"OVERRIDE {attribute.TargetPath} | {attribute.Name} " +
                              $"({attribute.PropertyType}) = {attribute.Value}");
        return 0;
    }

    private static void DumpDisplay(DisplayElement display, string indent)
    {
        Console.WriteLine($"{indent}DISPLAY {display.Element.Name} [{display.Element.ClassName}]");
        foreach (var attribute in display.Element.Attributes)
            Console.WriteLine($"{indent}  A {attribute.Name} ({attribute.PropertyType}) = {attribute.Value}");
        foreach (var attribute in display.Element.ExtraAttributes)
            Console.WriteLine($"{indent}  X {attribute.Name} ({attribute.PropertyType}) = {attribute.Value}");
        foreach (var child in display.Children) DumpDisplay(child, indent + "  ");
    }

    private static void DumpProperty(Property property, string indent)
    {
        Console.WriteLine($"{indent}{property.Info.FunctionName} ({property.Info.DataType})");
        if (property.Keys is not null)
            foreach (var key in property.Keys) Console.WriteLine($"{indent}  {key}");
        if (property.ChildProperties is not null)
            foreach (var child in property.ChildProperties) DumpProperty(child, indent + "  ");
    }

    private static int AlignLarge(GuiFile gui)
    {
        var container = gui.Containers.FirstOrDefault(item =>
            item.Info.Name.Equals("s_c_XB1", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("s_c_XB1 clip container was not found.");

        var changed = 0;
        foreach (var clip in container.Clips)
        {
            if (clip.clip is null || !LargePositions.TryGetValue(clip.name, out var position)) continue;
            foreach (var track in clip.clip.Tracks.Where(track =>
                         track.Name.StartsWith("t_btn_active", StringComparison.OrdinalIgnoreCase)))
            {
                var property = track.Properties.FirstOrDefault(item =>
                    item.Info.FunctionName.Equals("Position", StringComparison.OrdinalIgnoreCase));
                if (property?.ChildProperties is null) continue;
                changed += SetCoordinate(property, "x", position.X);
                changed += SetCoordinate(property, "y", position.Y);
            }
        }
        return changed;
    }

    private static int SetCoordinate(Property parent, string name, double value)
    {
        var property = parent.ChildProperties?.FirstOrDefault(item =>
            item.Info.FunctionName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property?.Keys is null) return 0;

        var changed = 0;
        foreach (var key in property.Keys.OfType<NoHermiteKey>())
        {
            key.Value = value;
            changed++;
        }
        return changed;
    }

    private static int AlignSmall(GuiFile gui)
    {
        if (gui.RootView is null) throw new InvalidDataException("GUI root view was not found.");
        var xbox = FindDisplay(gui.RootView, "c_XB1")
            ?? throw new InvalidDataException("c_XB1 display was not found.");

        var changed = SetBoolean(xbox.Element.Attributes, "ResolutionAdjust", false);
        foreach (var pair in SmallMarkers)
        {
            var display = FindDisplay(xbox, pair.Key);
            if (display is null) continue;
            changed += SetPosition(display.Element.Attributes, pair.Value.Position);
            changed += SetPosition(display.Element.ExtraAttributes, pair.Value.Position);
            changed += SetBoolean(display.Element.Attributes, "ResolutionAdjust", false);

            var targetPath = "/c_top/c_image/c_XB1/" + pair.Key + "/t_btn";
            changed += SetOverride(gui, targetPath, "UVPatternNo", pair.Value.Pattern);
            if (pair.Value.Size is Vector2 size)
                changed += SetOverride(gui, targetPath, "Size",
                    new ReeLib.via.Size { w = size.X, h = size.Y });
        }
        return changed;
    }

    private static int SetBoolean(
        List<ReeLib.Gui.Attribute> attributes,
        string name,
        bool value)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null) return 0;
        attribute.Value = value;
        return 1;
    }

    private static int SetOverride(
        GuiFile gui,
        string targetPath,
        string name,
        object value)
    {
        var attribute = gui.AttributeOverrides.FirstOrDefault(item =>
            item.TargetPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) &&
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
        {
            var template = gui.AttributeOverrides.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (template is null) return 0;
            attribute = template.DeepClone<AttributeOverride>()!;
            attribute.TargetPath = targetPath;
            gui.AttributeOverrides.Add(attribute);
        }
        attribute.Value = value;
        return 1;
    }

    private static int SetPosition(List<ReeLib.Gui.Attribute> attributes, Vector2 position)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals("Position", StringComparison.OrdinalIgnoreCase));
        if (attribute?.Value is not Vector3 current) return 0;
        attribute.Value = new Vector3(position, current.Z);
        return 1;
    }

    private static DisplayElement? FindDisplay(DisplayElement root, string name)
    {
        if (root.Element.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return root;
        foreach (var child in root.Children)
        {
            var match = FindDisplay(child, name);
            if (match is not null) return match;
        }
        return null;
    }
}
