using System.Numerics;
using ReeLib;
using ReeLib.Clip;
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

    private static readonly IReadOnlyDictionary<string, Vector2> SmallPositions =
        new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase)
        {
            ["c_BtnU"] = new(234.0f, 76.5f),
            ["c_BtnD"] = new(233.0f, 111.5f),
            ["c_BtnL"] = new(212.0f, 94.5f),
            ["c_BtnR"] = new(255.0f, 94.0f),
            ["c_DirU"] = new(53.0f, 80.5f),
            ["c_DirD"] = new(53.0f, 109.0f),
            ["c_DirL"] = new(35.0f, 95.0f),
            ["c_DirR"] = new(72.0f, 95.0f),
            ["c_LT"] = new(54.5f, 23.0f),
            ["c_LB"] = new(54.5f, 34.0f),
            ["c_RT"] = new(231.5f, 23.0f),
            ["c_RB"] = new(231.5f, 34.0f),
            ["c_LS"] = new(97.5f, 129.5f),
            ["c_RS"] = new(189.0f, 129.5f),
            ["c_CenL"] = new(143.0f, 71.0f),
            ["c_CenR"] = new(212.0f, 66.0f)
        };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3 ||
                (!args[0].Equals("large", StringComparison.OrdinalIgnoreCase) &&
                 !args[0].Equals("small", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine("Usage: <large|small> <input.gui.*> <output.gui.*>");
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

        var changed = 0;
        foreach (var pair in SmallPositions)
        {
            var display = FindDisplay(xbox, pair.Key);
            if (display is null) continue;
            changed += SetPosition(display.Element.Attributes, pair.Value);
            changed += SetPosition(display.Element.ExtraAttributes, pair.Value);
        }
        return changed;
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
