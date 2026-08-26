using System.Numerics;
using ReeLib;
using ReeLib.Clip;
using ReeLib.Common;
using ReeLib.Gui;
using ReeLib.Uvs;

namespace DMC5DualSense.GuiLayoutTool;

internal static class Program
{
    private static readonly string[] LargeControllerContainers = ["s_c_XB1", "s_c_PS4"];

    private sealed record LargeMarker(Vector2 Position, Vector2? Size = null);

    private static readonly IReadOnlyDictionary<string, LargeMarker> LargeMarkers =
        new Dictionary<string, LargeMarker>(StringComparer.OrdinalIgnoreCase)
        {
            ["BtnU"] = new(new(585.5f, 185.0f)),
            ["BtnD"] = new(new(582.5f, 272.0f)),
            ["BtnL"] = new(new(531.0f, 230.0f)),
            ["BtnR"] = new(new(638.0f, 229.0f)),
            // The four UV cells have different transparent/internal offsets.
            // These calibrated anchors put each cell's visible cyan shape on
            // the physical D-pad button; the parent controller panel then owns
            // all resolution/aspect scaling as a single unit.
            ["DirU"] = new(new(132.0f, 199.0f)),
            ["DirD"] = new(new(132.0f, 261.0f)),
            ["DirL"] = new(new(98.0f, 230.0f)),
            ["DirR"] = new(new(168.0f, 230.0f)),
            // Nero binds all four directions as one action. DirAll animates
            // the right marker directly and reveals three base display layers
            // for left/down/up; those defaults are aligned separately below.
            ["DirAll"] = new(new(168.0f, 230.0f)),
            // ui4002 uses the same artwork as ui8013 at 2.5x scale. Keep the
            // Settings shoulder overlays proportional to the corrected Void
            // overlays instead of stretching both buttons across a 128px cell.
            ["LT"] = new(new(135.5f, 60.0f), new(96.25f, 72.5f)),
            ["LB"] = new(new(133.0f, 91.5f), new(106.25f, 62.5f)),
            ["RT"] = new(new(580.5f, 60.0f), new(-96.25f, 72.5f)),
            ["RB"] = new(new(583.0f, 91.5f), new(-106.25f, 62.5f)),
            // Pattern 13 contains a 91x82 visible circle inside its 128x128 UV
            // cell. Scale the Settings markers to the complete movable stick
            // caps (centre plus rim), matching the visual coverage already used
            // by the smaller Void controller. These anchors are independent of
            // c_LS/c_RS below and are patched into both Settings timelines.
            ["LStP"] = new(new(244.0f, 316.0f), new(112.0f, 120.0f)),
            ["RStP"] = new(new(473.0f, 316.0f), new(112.0f, 120.0f)),
            ["CenL"] = new(new(359.0f, 171.0f)),
            ["CenR"] = new(new(530.0f, 158.0f))
        };

    private sealed record SmallMarker(Vector2 Position, uint Pattern, Vector2? Size = null);

    private static readonly IReadOnlyDictionary<string, SmallMarker> SmallMarkers =
        new Dictionary<string, SmallMarker>(StringComparer.OrdinalIgnoreCase)
        {
            ["c_BtnU"] = new(new(234.0f, 76.5f), 2),
            ["c_BtnD"] = new(new(233.0f, 111.5f), 3),
            ["c_BtnL"] = new(new(212.0f, 94.5f), 4),
            ["c_BtnR"] = new(new(255.0f, 94.0f), 5),
            // These are calibrated against the actual D-pad silhouettes in
            // the 1467x816 DualSense source after its 280x156 placement at
            // (3,12). The four UV cells have different internal alpha bounds,
            // so using the cell centre (or the old Xbox anchors) pushes Down,
            // Left and Right visibly away from their physical buttons.
            ["c_DirU"] = new(new(52.3f, 82.5f), 6),
            ["c_DirD"] = new(new(53.5f, 106.5f), 7),
            ["c_DirL"] = new(new(39.0f, 95.0f), 8),
            ["c_DirR"] = new(new(68.0f, 95.0f), 9),
            // Match the native 1467x816 DualSense artwork after it is scaled
            // into the 280x156 controller panel. L2/R2 are taller and narrower
            // than L1/R1; using the old common 52x26 rectangle made the two
            // shoulder highlights cover each other.
            ["c_LT"] = new(new(54.0f, 26.5f), 11, new(38.5f, 29.0f)),
            ["c_LB"] = new(new(53.0f, 39.0f), 12, new(42.5f, 25.0f)),
            ["c_RT"] = new(new(232.0f, 26.5f), 11, new(-38.5f, 29.0f)),
            ["c_RB"] = new(new(233.0f, 39.0f), 12, new(-42.5f, 25.0f)),
            ["c_LS"] = new(new(98.0f, 131.5f), 13, new(44.0f, 46.0f)),
            ["c_RS"] = new(new(189.5f, 131.5f), 13, new(44.0f, 46.0f)),
            ["c_CenL"] = new(new(143.0f, 72.0f), 37, new(118.5f, 61.5f)),
            ["c_CenR"] = new(new(212.0f, 66.0f), 10)
        };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0].Equals("inspect", StringComparison.OrdinalIgnoreCase))
                return Inspect(args[1]);
            if (args.Length == 2 && args[0].Equals("inspect-all", StringComparison.OrdinalIgnoreCase))
                return Inspect(args[1], inspectAll: true);
            if (args.Length == 2 && args[0].Equals("inspect-uvs", StringComparison.OrdinalIgnoreCase))
                return InspectUvs(args[1]);

            if (args.Length != 3 ||
                (!args[0].Equals("large", StringComparison.OrdinalIgnoreCase) &&
                 !args[0].Equals("small", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(
                    "Usage:\n" +
                    "  <large|small> <input.gui.*> <output.gui.*>\n" +
                    "  inspect <input.gui.*>\n" +
                    "  inspect-all <input.gui.*>\n" +
                    "  inspect-uvs <input.uvs.*>");
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

    private static int InspectUvs(string path)
    {
        using var uvs = new UvsFile(new FileHandler(path));
        if (!uvs.Read()) throw new InvalidDataException("Failed to read UVS file.");
        for (var sequenceIndex = 0; sequenceIndex < uvs.Sequences.Count; sequenceIndex++)
        {
            var sequence = uvs.Sequences[sequenceIndex];
            for (var patternIndex = 0; patternIndex < sequence.patterns.Count; patternIndex++)
            {
                UvsPattern pattern = sequence.patterns[patternIndex];
                Console.WriteLine(
                    $"S{sequenceIndex} P{patternIndex}: {pattern}; flags=0x{pattern.flags:X}; " +
                    $"cutout={pattern.cutoutUVCount}");
            }
        }
        return 0;
    }

    private static int Inspect(string path, bool inspectAll = false)
    {
        using var gui = new GuiFile(new FileHandler(path));
        if (!gui.Read()) throw new InvalidDataException("Failed to read GUI file.");

        foreach (var container in gui.Containers)
        {
            if (!inspectAll && !LargeControllerContainers.Contains(container.Info.Name,
                    StringComparer.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"CONTAINER {container.Info.Name}");
            foreach (var clip in container.Clips.Where(item => inspectAll ||
                         LargeMarkers.ContainsKey(item.name) ||
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
            if (inspectAll)
            {
                DumpDisplay(gui.RootView, "");
            }
            else
            {
                var smallRoot = FindDisplay(gui.RootView, "c_XB1") ?? gui.RootView;
                foreach (var name in SmallMarkers.Keys)
                {
                    var display = FindDisplay(smallRoot, name);
                    if (display is null) continue;
                    DumpDisplay(display, "");
                }
            }
        }
        foreach (var attribute in gui.AttributeOverrides.Where(item => inspectAll ||
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
        var containers = gui.Containers.Where(item =>
            LargeControllerContainers.Contains(item.Info.Name,
                StringComparer.OrdinalIgnoreCase)).ToArray();
        if (containers.Length == 0)
            throw new InvalidDataException("Neither s_c_XB1 nor s_c_PS4 was found.");

        var changed = AlignGroupedDpadDefaults(gui);
        foreach (var container in containers)
        foreach (var clip in container.Clips)
        {
            if (clip.clip is null || !LargeMarkers.TryGetValue(clip.name, out var marker)) continue;
            // DMC5 has two independent controller timelines in this screen.
            // Nero's grouped D-pad and the Settings shoulder indicators can be
            // rendered by s_c_PS4, while other layouts use s_c_XB1. Patch those
            // controls in both timelines; retain the PS4-specific face/stick
            // positions which are already aligned with the replacement art.
            // Stick-press markers are also patched in both timelines because
            // the original PS4 anchors sit above the DualSense stick caps.
            if (container.Info.Name.Equals("s_c_PS4", StringComparison.OrdinalIgnoreCase) &&
                !clip.name.StartsWith("Dir", StringComparison.OrdinalIgnoreCase) &&
                clip.name is not ("LT" or "LB" or "RT" or "RB" or "LStP" or "RStP"))
                continue;
            foreach (var track in clip.clip.Tracks.Where(track =>
                         track.Name.StartsWith("t_btn_active", StringComparison.OrdinalIgnoreCase)))
            {
                var property = track.Properties.FirstOrDefault(item =>
                    item.Info.FunctionName.Equals("Position", StringComparison.OrdinalIgnoreCase));
                if (property?.ChildProperties is null) continue;
                changed += SetCoordinate(property, "x", marker.Position.X);
                changed += SetCoordinate(property, "y", marker.Position.Y);

                if (marker.Size is not Vector2 size) continue;
                var sizeProperty = track.Properties.FirstOrDefault(item =>
                    item.Info.FunctionName.Equals("Size", StringComparison.OrdinalIgnoreCase));
                if (sizeProperty?.ChildProperties is null) continue;
                changed += SetCoordinate(sizeProperty, "w", size.X);
                changed += SetCoordinate(sizeProperty, "h", size.Y);
            }
        }
        return changed;
    }

    private static int AlignGroupedDpadDefaults(GuiFile gui)
    {
        if (gui.RootView is null) return 0;

        var groupedLayers = new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase)
        {
            ["t_btn_active0"] = LargeMarkers["DirL"].Position,
            ["t_btn_active1"] = LargeMarkers["DirD"].Position,
            ["t_btn_active2"] = LargeMarkers["DirU"].Position
        };

        var changed = 0;
        foreach (var controllerName in new[] { "c_XB1", "c_PS4" })
        {
            var controller = FindDisplay(gui.RootView, controllerName);
            if (controller is null) continue;

            foreach (var pair in groupedLayers)
            {
                var display = FindDisplay(controller, pair.Key);
                if (display is null) continue;
                changed += SetPosition(display.Element.Attributes, pair.Value);
                changed += SetPosition(display.Element.ExtraAttributes, pair.Value);
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
        var controllers = new[] { "c_XB1", "c_PS4" }
            .Select(name => (Name: name, Display: FindDisplay(gui.RootView, name)))
            .Where(item => item.Display is not null)
            .ToArray();
        if (controllers.Length == 0)
            throw new InvalidDataException("Neither c_XB1 nor c_PS4 display was found.");

        var changed = 0;
        foreach (var controller in controllers)
        {
            var root = controller.Display!;
            changed += SetBoolean(root.Element.Attributes, "ResolutionAdjust", false);
            foreach (var pair in SmallMarkers)
            {
                var display = FindDisplay(root, pair.Key);
                if (display is null) continue;
                changed += SetPosition(display.Element.Attributes, pair.Value.Position);
                changed += SetPosition(display.Element.ExtraAttributes, pair.Value.Position);
                changed += SetBoolean(display.Element.Attributes, "ResolutionAdjust", false);

                // The Void can switch between these two independent controller
                // branches. Patching c_XB1 alone left c_PS4 at Capcom's old
                // offsets, which is why only the right D-pad marker happened to
                // line up in some modes while the other three moved differently.
                var targetPath = "/c_top/c_image/" + controller.Name + "/" +
                                 pair.Key + "/t_btn";
                changed += SetOverride(gui, targetPath, "UVPatternNo", pair.Value.Pattern);
                if (pair.Value.Size is Vector2 size)
                    changed += SetOverride(gui, targetPath, "Size",
                        new ReeLib.via.Size { w = size.X, h = size.Y });
            }
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
