using System.Numerics;
using System.Text.RegularExpressions;
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
            // The controller is drawn in perspective: the visible movable cap
            // sits a little below and inward from the mechanical stick base.
            // Cover that cap rather than the base centre used by the stock art.
            ["LStP"] = new(new(243.0f, 321.0f), new(106.0f, 110.0f)),
            ["RStP"] = new(new(476.0f, 321.0f), new(106.0f, 110.0f)),
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

    // ui3109 is the pause-menu "Display Controls" diagram. Platform selection,
    // character clips, keyboard/controller switching and show/hide state all use
    // the original c_PS4/c_XB1 timelines. Never force one branch visible: doing
    // so freezes the labels and leaves the overlay on screen. Instead, keep both
    // timelines intact and calibrate the semantically equivalent endpoint in
    // each branch against the accepted ui8013 controller artwork.
    private static readonly IReadOnlyDictionary<string, Vector2> PauseConnectors =
        new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase)
        {
            ["c_PS4_C"] = new(-1.0f, -20.0f),
            ["c_PS4_L_top"] = new(-90.5f, -59.25f),
            ["c_PS4_L_center"] = new(-90.8f, 2.75f),
            ["c_PS4_L_center_2nd"] = new(-90.8f, 2.75f),
            ["c_PS4_L_bottom"] = new(-46.0f, 39.5f),
            ["c_PS4_R_top"] = new(88.5f, -59.25f),
            ["c_PS4_R_center"] = new(89.5f, 2.125f),
            ["c_PS4_R_bottom"] = new(45.5f, 39.5f),
            ["c_XB1_C"] = new(-1.0f, -20.0f),
            ["c_XB1_L_top"] = new(-90.5f, -59.25f),
            ["c_XB1_L_center"] = new(-46.0f, 39.5f),
            ["c_XB1_L_bottom"] = new(-90.8f, 2.75f),
            ["c_XB1_L_bottom_2nd"] = new(-90.8f, 2.75f),
            ["c_XB1_R_top"] = new(88.5f, -59.25f),
            ["c_XB1_R_center"] = new(89.5f, 2.125f),
            ["c_XB1_R_bottom"] = new(45.5f, 39.5f)
        };

    private static readonly Vector2 PauseOptionsTarget = new(68.0f, -26.0f);

    // RE Engine rasterizes these rotated GUI rectangles without texture
    // filtering. A sub-two-pixel strip exposes obvious stair steps on the
    // pause diagram, whereas 2.25 px keeps the route crisp without making it
    // visually heavier than the surrounding cyan panel rules.
    private const float PauseConnectorThickness = 2.25f;

    private static readonly IReadOnlyDictionary<string, (Guid Circle, Guid Line)>
        PauseOptionsConnectorIds =
            new Dictionary<string, (Guid Circle, Guid Line)>(StringComparer.OrdinalIgnoreCase)
            {
                ["c_PS4_C"] = (
                    Guid.Parse("78560ed2-5284-4ebd-ae91-bcd006d70910"),
                    Guid.Parse("e24c039d-bf43-4f8a-b5ef-8dca2f3e7bbc")),
                ["c_XB1_C"] = (
                    Guid.Parse("60618106-3f64-47dd-b6b7-5962199e97bd"),
                    Guid.Parse("fb4df65e-88d3-4453-bc7b-55aa32098f09"))
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
            if (args.Length == 2 && args[0].Equals("hash-path", StringComparison.OrdinalIgnoreCase))
                return HashPath(args[1]);
            if (args.Length == 5 && args[0].Equals("extract-pak", StringComparison.OrdinalIgnoreCase))
                return ExtractPak(args[1], args[2], args[3], args[4]);
            if (args.Length == 3 && args[0].Equals("scan-directory", StringComparison.OrdinalIgnoreCase))
                return ScanDirectory(args[1], args[2]);

            if (args.Length != 3 ||
                (!args[0].Equals("large", StringComparison.OrdinalIgnoreCase) &&
                 !args[0].Equals("small", StringComparison.OrdinalIgnoreCase) &&
                 !args[0].Equals("pause", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(
                    "Usage:\n" +
                    "  <large|small|pause> <input.gui.*> <output.gui.*>\n" +
                    "  inspect <input.gui.*>\n" +
                    "  inspect-all <input.gui.*>\n" +
                    "  inspect-uvs <input.uvs.*>\n" +
                    "  hash-path <natives-relative-path>\n" +
                    "  scan-directory <input-directory> <name-regex>\n" +
                    "  extract-pak <input.pak> <file-list> <path-regex> <output-directory>");
                return 2;
            }

            using var gui = new GuiFile(new FileHandler(args[1]));
            if (!gui.Read()) throw new InvalidDataException("Failed to read GUI file.");

            var changed = args[0].ToLowerInvariant() switch
            {
                "large" => AlignLarge(gui),
                "small" => AlignSmall(gui),
                "pause" => AlignPause(gui),
                _ => 0
            };
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

    private static int HashPath(string path)
    {
        var hash = PakUtils.GetFilepathHash(path.Replace('\\', '/'));
        Console.WriteLine($"0x{hash:X16} lower={(uint)hash} upper={(uint)(hash >> 32)}");
        return 0;
    }

    private static int ScanDirectory(string inputDirectory, string namePattern)
    {
        var regex = new Regex(namePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var matchedFiles = 0;
        foreach (var path in Directory.EnumerateFiles(
                     Path.GetFullPath(inputDirectory), "*.gui.*", SearchOption.AllDirectories))
        {
            using var gui = new GuiFile(new FileHandler(path));
            if (!gui.Read()) continue;

            var matches = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in gui.Containers)
            {
                if (regex.IsMatch(container.Info.Name)) matches.Add("container:" + container.Info.Name);
                foreach (var clip in container.Clips)
                    if (regex.IsMatch(clip.name)) matches.Add("clip:" + clip.name);
            }
            if (gui.RootView is not null) CollectDisplayNames(gui.RootView, regex, matches);
            foreach (var attribute in gui.AttributeOverrides)
                if (regex.IsMatch(attribute.TargetPath)) matches.Add("override:" + attribute.TargetPath);

            if (matches.Count == 0) continue;
            matchedFiles++;
            Console.WriteLine(Path.GetRelativePath(inputDirectory, path));
            foreach (var match in matches) Console.WriteLine("  " + match);
        }
        Console.WriteLine($"Matched {matchedFiles} GUI files.");
        return matchedFiles == 0 ? 4 : 0;
    }

    private static void CollectDisplayNames(DisplayElement display, Regex regex, ISet<string> matches)
    {
        if (regex.IsMatch(display.Element.Name)) matches.Add("display:" + display.Element.Name);
        foreach (var child in display.Children) CollectDisplayNames(child, regex, matches);
    }

    private static int ExtractPak(string pakPath, string listPath, string pathPattern, string outputDirectory)
    {
        var reader = new PakReader
        {
            EnableConsoleLogging = true,
            Filter = new Regex(pathPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };
        reader.PakFilePriority.Add(Path.GetFullPath(pakPath));
        reader.AddFilesFromListFile(Path.GetFullPath(listPath));

        var missing = new List<string>();
        var count = reader.UnpackFilesTo(Path.GetFullPath(outputDirectory), missing);
        Console.WriteLine($"Extracted {count} matching PAK files; missing {missing.Count}.");
        foreach (var path in missing) Console.WriteLine($"MISSING {path}");
        return missing.Count == 0 ? 0 : 3;
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
            // controls in both timelines.
            // Face-button and stick-press markers are also patched in both
            // timelines. The original PS4 face anchors belong to the narrower
            // DualShock artwork and sit progressively left of Square, Cross
            // and especially Circle on the replacement DualSense image.
            if (container.Info.Name.Equals("s_c_PS4", StringComparison.OrdinalIgnoreCase) &&
                !clip.name.StartsWith("Dir", StringComparison.OrdinalIgnoreCase) &&
                clip.name is not ("BtnU" or "BtnD" or "BtnL" or "BtnR" or
                                  "LT" or "LB" or "RT" or "RB" or "LStP" or "RStP"))
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

    private static int AlignPause(GuiFile gui)
    {
        if (gui.RootView is null) throw new InvalidDataException("GUI root view was not found.");

        var ps4 = FindDisplay(gui.RootView, "c_PS4") ??
                  throw new InvalidDataException("c_PS4 display was not found in the pause diagram.");
        var xbox = FindDisplay(gui.RootView, "c_XB1") ??
                   throw new InvalidDataException("c_XB1 display was not found in the pause diagram.");

        var changed = 0;
        // Both controller cells contain the same DualSense artwork in the
        // shipped atlas, but pattern 1 intentionally adds a cyan outer outline.
        // Keep only the clean pattern 0 artwork and remove the old separate
        // stick-cap textures: the DualSense image already contains both sticks.
        foreach (var root in new[] { ps4, xbox })
        {
            var gamepad = FindDisplay(root, "t_gamepad");
            if (gamepad is not null)
            {
                changed += SetPosition(gamepad.Element.Attributes, Vector2.Zero);
                changed += SetSize(gamepad.Element.Attributes, new ReeLib.via.Size { w = 288.0f, h = 184.0f });
                changed += SetUInt32(gamepad.Element.Attributes, "UVPatternNo", 0);
            }
            foreach (var stickName in new[] { "t_LS", "t_RS" })
            {
                var stick = FindDisplay(root, stickName);
                if (stick is not null) changed += SetBoolean(stick.Element.Attributes, "Visible", false);
            }
        }

        foreach (var pair in PauseConnectors)
        {
            var branch = pair.Key.StartsWith("c_PS4_", StringComparison.OrdinalIgnoreCase)
                ? ps4
                : xbox;
            var panel = FindDisplay(branch, pair.Key);
            if (panel is null) throw new InvalidDataException($"Pause connector panel {pair.Key} was not found.");
            changed += AlignConnector(panel, pair.Value);
        }

        // The stock central panel combines Back/Provoke and Start/Pause behind
        // one line. Keep the original touchpad line and add an independent line
        // to the physical Options button for Pause. Both lines remain children
        // of the branch panel, so the game's own visibility timeline owns them.
        foreach (var pair in new[]
                 {
                     (Root: ps4, Name: "c_PS4_C"),
                     (Root: xbox, Name: "c_XB1_C")
                 })
        {
            var panel = FindDisplay(pair.Root, pair.Name) ??
                        throw new InvalidDataException($"Pause central panel {pair.Name} was not found.");
            changed += AddOptionsConnector(panel, PauseOptionsTarget);
        }
        return changed;
    }

    private static int AddOptionsConnector(DisplayElement panel, Vector2 target)
    {
        if (panel.Container is null)
            throw new InvalidDataException($"Panel {panel.Element.Name} has no child container.");

        var sourceCircle = FindDisplay(panel, "circle") ??
                           throw new InvalidDataException($"Panel {panel.Element.Name} has no endpoint circle.");
        var sourceLine = FindDisplay(panel, "r_link_line") ??
                         throw new InvalidDataException($"Panel {panel.Element.Name} has no link line.");
        if (!PauseOptionsConnectorIds.TryGetValue(panel.Element.Name, out var ids))
            throw new InvalidDataException($"No deterministic IDs for {panel.Element.Name}.");

        var existingCircle = FindDisplay(panel, "circle_options");
        var existingLine = FindDisplay(panel, "r_link_line_options");
        var circleElement = existingCircle?.Element ?? sourceCircle.Element.DeepClone<Element>()!;
        var lineElement = existingLine?.Element ?? sourceLine.Element.DeepClone<Element>()!;
        var added = 0;
        if (existingCircle is null)
        {
            circleElement.ID = new GuiObjectID(ids.Circle);
            circleElement.Name = "circle_options";
            SetString(circleElement.Attributes, "Name", "circle_options");
            panel.Container.Elements.Add(circleElement);
            added++;
        }
        if (existingLine is null)
        {
            lineElement.ID = new GuiObjectID(ids.Line);
            lineElement.Name = "r_link_line_options";
            SetString(lineElement.Attributes, "Name", "r_link_line_options");
            panel.Container.Elements.Add(lineElement);
            added++;
        }

        var circle = new DisplayElement(circleElement, null);
        var line = new DisplayElement(lineElement, null);
        // Start below the Pause/Options half of the central text panel instead
        // of stacking both routes on its centre.
        var changed = SetPosition(line.Element.Attributes, new Vector2(50.0f, 2.0f));
        changed += AlignConnector(panel, target, circle, line);
        return changed + added;
    }

    private static int AlignConnector(DisplayElement panel, Vector2 target)
    {
        var circle = FindDisplay(panel, "circle") ??
                     throw new InvalidDataException($"Panel {panel.Element.Name} has no endpoint circle.");
        var line = FindDisplay(panel, "r_link_line") ??
                   throw new InvalidDataException($"Panel {panel.Element.Name} has no link line.");
        return AlignConnector(panel, target, circle, line);
    }

    private static int AlignConnector(
        DisplayElement panel,
        Vector2 target,
        DisplayElement circle,
        DisplayElement line)
    {
        var panelPosition = GetVector3(panel.Element.Attributes, "Position") ??
                            throw new InvalidDataException($"Panel {panel.Element.Name} has no Position.");
        var localTarget = target - new Vector2(panelPosition.X, panelPosition.Y);
        var linePosition = GetVector3(line.Element.Attributes, "Position") ??
                           throw new InvalidDataException($"Panel {panel.Element.Name} link has no Position.");
        var lineSize = GetSize(line.Element.Attributes, "Size") ??
                       throw new InvalidDataException($"Panel {panel.Element.Name} link has no Size.");

        var start = new Vector2(linePosition.X, linePosition.Y);
        var delta = localTarget - start;
        var length = MathF.Max(1.5f, delta.Length());
        var changed = SetPosition(circle.Element.Attributes, localTarget);
        if (lineSize.w <= lineSize.h)
        {
            var rotation = -MathF.Atan2(delta.X, delta.Y) * 180.0f / MathF.PI;
            changed += SetSize(line.Element.Attributes, new ReeLib.via.Size
            {
                w = PauseConnectorThickness,
                h = length
            });
            changed += SetRotationZ(line.Element.Attributes, rotation);
        }
        else
        {
            var rotation = delta.X >= 0.0f
                ? MathF.Atan2(delta.Y, delta.X) * 180.0f / MathF.PI
                : MathF.Atan2(-delta.Y, -delta.X) * 180.0f / MathF.PI;
            changed += SetSize(line.Element.Attributes, new ReeLib.via.Size
            {
                w = length,
                h = PauseConnectorThickness
            });
            changed += SetRotationZ(line.Element.Attributes, rotation);
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

    private static int SetUInt32(List<ReeLib.Gui.Attribute> attributes, string name, uint value)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null) return 0;
        attribute.Value = value;
        return 1;
    }

    private static int SetString(
        List<ReeLib.Gui.Attribute> attributes,
        string name,
        string value)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null) return 0;
        attribute.Value = value;
        return 1;
    }

    private static int SetSize(List<ReeLib.Gui.Attribute> attributes, ReeLib.via.Size value)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals("Size", StringComparison.OrdinalIgnoreCase));
        if (attribute is null) return 0;
        attribute.Value = value;
        return 1;
    }

    private static int SetRotationZ(List<ReeLib.Gui.Attribute> attributes, float value)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals("Rotation", StringComparison.OrdinalIgnoreCase));
        if (attribute?.Value is not Vector3 current) return 0;
        attribute.Value = new Vector3(current.X, current.Y, value);
        return 1;
    }

    private static Vector3? GetVector3(List<ReeLib.Gui.Attribute> attributes, string name)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return attribute?.Value is Vector3 value ? value : null;
    }

    private static ReeLib.via.Size? GetSize(List<ReeLib.Gui.Attribute> attributes, string name)
    {
        var attribute = attributes.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return attribute?.Value is ReeLib.via.Size value ? value : null;
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
