using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlideScroll.Config;

public enum AppListMode { Exclusion, Inclusion }

/// <summary>Theme appearance modes (v1.3 theme overhaul). Auto follows the
/// Windows light/dark flag between the Light and Dark presets, nothing else
/// is read from the OS. Every preset is an original GlideScroll composition,
/// every preset is user-editable (edits persist as overrides, per-theme Reset
/// restores factory values), and there are three independent Custom slots.</summary>
[JsonConverter(typeof(ThemeAppearanceJsonConverter))]
public enum ThemeAppearance
{
    Auto,          // follow Windows light/dark between Light and Dark
    Light,         // white-first light (was "Paper white")
    Dark,          // deep blue dark (was "Twilight reef")
    OledElectric,  // pure black, electric azure accent
    CozyPink,      // white and pink, hot pink accent
    CozyBlue,      // white and sky, vivid blue accent
    SakuraPop,     // warm paper, navy border, sakura red-pink accent
    LavenderMilk,  // pale violet light
    GridGraphite,  // neutral graphite dark, crimson accent
    AizomeIndigo,  // Japanese indigo dark
    NeonDusk,      // purple-noir neon
    CyanCircuit,   // near-black cyan neon
    GreenPhosphor, // CRT green terminal
    AmberCrt,      // CRT amber terminal
    AbyssGlow,     // deep ocean teal glow
    Custom1,       // user palette slot 1
    Custom2,       // user palette slot 2
    Custom3        // user palette slot 3
}

/// <summary>Tolerant appearance converter. Maps legacy v1.2 names from
/// existing configs onto their nearest surviving v1.3 theme instead of
/// throwing (a throw would discard the user's entire config on load), and
/// falls back to Auto for anything unrecognized.</summary>
public sealed class ThemeAppearanceJsonConverter : JsonConverter<ThemeAppearance>
{
    public override ThemeAppearance Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ThemeConfig.ParseAppearance(reader.GetString() ?? "auto");

    public override void Write(Utf8JsonWriter writer, ThemeAppearance value, JsonSerializerOptions options)
        => writer.WriteStringValue(ThemeConfig.KeyOf(value));
}

/// <summary>Wheel smoothing engine selection. Every engine is our own code.
/// Silk and Cadence are original designs; the others are our implementations of
/// standard techniques (damped spring, momentum decay, linear lerp).</summary>
public enum SmoothingMode
{
    Flywheel,    // momentum / inertia model, coasts after each notch
    Spring,      // 2nd-order spring with tunable damping ratio
    Hybrid,      // spring while scrolling, momentum coast on release
    LowLatency,  // minimal lerp per notch, fastest, no curve
    Silk,        // online minimum-jerk, finite bounded settle time
    Cadence      // input-rate adaptive: crisp glide reading, momentum on flicks
}

/// <summary>Middle-button pan engine selection. Both are our own code.
/// Feather drives content displacement 1:1 with the hand for the lightest,
/// lowest-latency feel (no velocity window, no filter); Cushion is the original
/// windowed-velocity model with an optional low-pass filter for a softer, more
/// damped glide.</summary>
public enum PanMode
{
    Feather,   // direct displacement drive, featherweight, minimal latency
    Cushion    // windowed-velocity + low-pass filter, softer and damped
}

/// <summary>A 6-color palette describing every CSS variable the WebView2 UI consumes.
/// Property defaults seed the Custom slots and match the Dark preset (our own
/// composition, see ThemeConfig.Presets).</summary>
public sealed class Palette
{
    public string Background { get; set; } = "#0B1626";
    public string Surface { get; set; } = "#142136";
    public string Text { get; set; } = "#DCE8F7";
    public string TextMuted { get; set; } = "#8296B3";
    public string Accent { get; set; } = "#3FA0FF";
    public string Border { get; set; } = "#1E3049";

    public Palette Clone() => new()
    {
        Background = Background, Surface = Surface, Text = Text,
        TextMuted = TextMuted, Accent = Accent, Border = Border
    };
}

public sealed class WheelConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Which smoothing engine the pipeline uses. Silk is the default:
    /// as smooth as Liquid Glide but settles fast with no lag tail. Existing
    /// configs keep whatever engine the user picked.</summary>
    public SmoothingMode SmoothingMode { get; set; } = SmoothingMode.Silk;
    public int StepSize { get; set; } = 120;
    public int AnimationTimeMs { get; set; } = 360;
    /// <summary>Engine-specific tuning slider, range -10 to 10, default 0
    /// (ideal for both engines that use it). Per-engine interpretation:
    /// Liquid Glide (Spring): "Bounce / Glide", -10 bouncy to +10 sluggish, 0 = critical damping.
    /// Smoothed Notch (Hybrid): "Spring / Coast", -10 mostly coast to +10 mostly spring, 0 = balanced.
    /// Hidden in UI for Notch with Glide (Flywheel) and Low Latency (no useful interpretation).</summary>
    public double FeelBias { get; set; } = 0.0;
    public int AccelWindowMs { get; set; } = 70;  // "Acceleration window"
    public double AccelBoost { get; set; } = 7.0;  // "Acceleration boost"
    public bool ShiftWheelHorizontal { get; set; } = true;
    public bool SmoothHorizontal { get; set; } = true;
    public bool ReverseDirection { get; set; } = false;
}

public sealed class PanConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Which pan engine drives middle-button dragging. Feather (default)
    /// maps hand motion straight to scroll for an immediate, paper-light feel;
    /// Cushion is the softer windowed-velocity model with the Smoothness filter.</summary>
    public PanMode Mode { get; set; } = PanMode.Feather;

    public double Friction { get; set; } = 30.0;             // "Coast Friction", exponential decay rate during post-release coast
    public double SensitivityX { get; set; } = 100.0;
    public double SensitivityY { get; set; } = 100.0;
    public int ActivationThresholdPixels { get; set; } = 5;
    public int MinStepPixels { get; set; } = 1;              // Minimum scroll-pixels per emit. Default 1 = max visual fidelity on modern apps.
    /// <summary>"Smoothness" slider (0-20). First-order low-pass filter strength
    /// on scroll velocity, rejects high-frequency cursor jitter (shaky hands)
    /// without affecting deliberate motion. 0 = filter off (raw velocity);
    /// higher = more smoothing + more lag. Default 5 = mild filter, comfortable
    /// for steady-handed users too.</summary>
    public double Smoothness { get; set; } = 5.0;
    public bool FlickMode { get; set; } = true;              // coast on release
    public bool LockCursorDuringPan { get; set; } = true;
    public bool SnapToAxis { get; set; } = false;
    public bool ReverseDirection { get; set; } = false;
}

public sealed class AppListConfig
{
    public AppListMode Mode { get; set; } = AppListMode.Exclusion;
    public List<string> Blacklist { get; set; } = new();
    public List<string> Whitelist { get; set; } = new();

    /// <summary>Set once the shipped default class blocks have been seeded into
    /// the Blacklist. Lets a returning user delete a default without it silently
    /// reappearing, while still seeding fresh installs and the one-time upgrade
    /// from builds that hardcoded these surfaces.</summary>
    public bool DefaultsSeeded { get; set; } = false;

    /// <summary>The window-class blocks GlideScroll ships with. These surfaces
    /// only honor whole-notch wheel input (modern File Explorer, the legacy
    /// Photo Viewer), so injected smoothing can't help and is passed through.
    /// Stored as "class:" entries so they sit in the exclusion list as normal,
    /// user-removable rows, matched by window class rather than exe name.</summary>
    public static readonly string[] DefaultBlocks =
    {
        "class:CabinetWClass",             // Windows File Explorer
        "class:Photo_Lightweight_Viewer",  // legacy Windows Photo Viewer
    };

    /// <summary>Seed the default class blocks into the Blacklist exactly once
    /// (first run, or first launch after upgrading from a build that hardcoded
    /// these surfaces). Returns true if anything changed so the caller can
    /// persist. After this runs, deletions stick, no silent re-seed.</summary>
    public bool EnsureDefaultBlocks()
    {
        if (DefaultsSeeded) return false;
        foreach (var b in DefaultBlocks)
        {
            if (!Blacklist.Any(e => string.Equals(e, b, StringComparison.OrdinalIgnoreCase)))
                Blacklist.Add(b);
        }
        DefaultsSeeded = true;
        return true;
    }

    public IEnumerable<string> GetActiveList() =>
        Mode == AppListMode.Exclusion ? Blacklist : Whitelist;
}

/// <summary>Game Mode: automatically pause GlideScroll while the input target
/// is a game (anti-cheat detected, fullscreen, or a known game exe). The list
/// ships seeded with popular anti-cheat titles, shown with a Default badge and
/// fully user-editable, exactly like the App rules default blocks.</summary>
public sealed class GameModeConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Game exe filenames (no path) that always pause GlideScroll
    /// when the input target belongs to them. Seeds + user additions.</summary>
    public List<string> Games { get; set; } = new();

    /// <summary>Set once the shipped seed list has been written into Games.
    /// Lets a user delete a seeded game without it silently reappearing.</summary>
    public bool DefaultsSeeded { get; set; } = false;

    /// <summary>The shipped seed list. Popular titles across every major
    /// anti-cheat engine. Exists mainly for VAC games (CS2, Dota), the one
    /// engine class with no runtime evidence to detect; everything else is
    /// also caught by engine detection and fullscreen anchoring.</summary>
    public static readonly string[] DefaultGames =
    {
        "VALORANT-Win64-Shipping.exe",           // Valorant (Vanguard)
        "League of Legends.exe",                 // League of Legends (Vanguard)
        "cs2.exe",                               // Counter-Strike 2 (VAC)
        "dota2.exe",                             // Dota 2 (VAC)
        "FortniteClient-Win64-Shipping.exe",     // Fortnite (EAC)
        "r5apex.exe",                            // Apex Legends (EAC)
        "r5apex_dx12.exe",                       // Apex Legends DX12 (EAC)
        "RustClient.exe",                        // Rust (EAC)
        "DeadByDaylight-Win64-Shipping.exe",     // Dead by Daylight (EAC)
        "RainbowSix.exe",                        // Rainbow Six Siege (BattlEye)
        "TslGame.exe",                           // PUBG (BattlEye)
        "destiny2.exe",                          // Destiny 2 (BattlEye)
        "EscapeFromTarkov.exe",                  // Escape from Tarkov (BattlEye)
        "cod.exe",                               // Call of Duty (Ricochet)
        "Overwatch.exe",                         // Overwatch 2 (Defense Matrix)
    };

    /// <summary>Seed the default games exactly once (first run or upgrade from
    /// a pre-Game-Mode build). Returns true if anything changed so the caller
    /// can persist. After this runs, deletions stick.</summary>
    public bool EnsureDefaults()
    {
        if (DefaultsSeeded) return false;
        foreach (var g in DefaultGames)
        {
            if (!Games.Any(e => string.Equals(e, g, StringComparison.OrdinalIgnoreCase)))
                Games.Add(g);
        }
        DefaultsSeeded = true;
        return true;
    }
}

public sealed class ThemeConfig
{
    public ThemeAppearance Appearance { get; set; } = ThemeAppearance.Auto;

    /// <summary>Three independent from-scratch palettes. Each defaults to the
    /// Dark preset values (the Palette property defaults).</summary>
    public Palette Custom1 { get; set; } = new();
    public Palette Custom2 { get; set; } = new();
    public Palette Custom3 { get; set; } = new();

    /// <summary>Sparse user edits of the baked-in presets, keyed by the
    /// appearance's camelCase name ("gridGraphite"). A key exists only once
    /// the user edits that theme; per-theme Reset removes it, restoring
    /// factory values. Unknown keys (from newer versions) are kept inert.</summary>
    public Dictionary<string, Palette> Overrides { get; set; } = new();

    /// <summary>v1.2 configs stored a single "custom" palette under this JSON
    /// name. Imported into Custom1 once by <see cref="MigrateLegacy"/>, then
    /// dropped from disk. Never surfaced in the UI.</summary>
    [JsonPropertyName("custom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Palette? LegacyCustom { get; set; }

    /// <summary>camelCase key for an appearance ("gridGraphite"). Used for
    /// JSON serialization, the Overrides dictionary, and the UI protocol.</summary>
    public static string KeyOf(ThemeAppearance a)
    {
        string n = a.ToString();
        return char.ToLowerInvariant(n[0]) + n.Substring(1);
    }

    /// <summary>Parse an appearance key, mapping legacy v1.2 names to their
    /// nearest surviving theme. Unknown input falls back to Auto.</summary>
    public static ThemeAppearance ParseAppearance(string s) => s switch
    {
        "auto" => ThemeAppearance.Auto,
        "light" => ThemeAppearance.Light,
        "dark" => ThemeAppearance.Dark,
        "oledElectric" => ThemeAppearance.OledElectric,
        "cozyPink" => ThemeAppearance.CozyPink,
        "cozyBlue" => ThemeAppearance.CozyBlue,
        "sakuraPop" => ThemeAppearance.SakuraPop,
        "lavenderMilk" => ThemeAppearance.LavenderMilk,
        "gridGraphite" => ThemeAppearance.GridGraphite,
        "aizomeIndigo" => ThemeAppearance.AizomeIndigo,
        "neonDusk" => ThemeAppearance.NeonDusk,
        "cyanCircuit" => ThemeAppearance.CyanCircuit,
        "greenPhosphor" => ThemeAppearance.GreenPhosphor,
        "amberCrt" => ThemeAppearance.AmberCrt,
        "abyssGlow" => ThemeAppearance.AbyssGlow,
        "custom1" => ThemeAppearance.Custom1,
        "custom2" => ThemeAppearance.Custom2,
        "custom3" => ThemeAppearance.Custom3,
        // Legacy v1.2 names, mapped to the nearest surviving theme.
        "rose" => ThemeAppearance.CozyPink,
        "graphite" => ThemeAppearance.GridGraphite,
        "parchment" => ThemeAppearance.SakuraPop,
        "blackIce" => ThemeAppearance.AbyssGlow,
        "oledBlack" => ThemeAppearance.OledElectric,
        "custom" => ThemeAppearance.Custom1,
        _ => ThemeAppearance.Auto
    };

    /// <summary>The concrete theme Auto resolves to right now. Auto follows
    /// only the Windows light/dark flag between Light and Dark; no other OS
    /// theme data is read. Non-Auto appearances return themselves.</summary>
    public ThemeAppearance ResolveBase(bool systemDark) =>
        Appearance == ThemeAppearance.Auto
            ? (systemDark ? ThemeAppearance.Dark : ThemeAppearance.Light)
            : Appearance;

    private Palette? CustomSlot(ThemeAppearance a) => a switch
    {
        ThemeAppearance.Custom1 => Custom1,
        ThemeAppearance.Custom2 => Custom2,
        ThemeAppearance.Custom3 => Custom3,
        _ => null
    };

    /// <summary>The actually-applied palette: Custom slot contents, else the
    /// user's saved edit of the preset, else factory values.</summary>
    public Palette ResolveActivePalette(bool systemDark)
    {
        var b = ResolveBase(systemDark);
        var custom = CustomSlot(b);
        if (custom is not null) return custom.Clone();
        if (Overrides.TryGetValue(KeyOf(b), out var edited)) return edited.Clone();
        return Presets.Factory.TryGetValue(b, out var factory) ? factory.Clone() : new Palette();
    }

    /// <summary>Apply one color edit to the theme the user is looking at
    /// (with Auto that is whichever of Light/Dark is currently resolved).
    /// Preset edits persist as overrides; Custom slots mutate directly.</summary>
    public void EditActive(bool systemDark, string slot, string value)
    {
        var b = ResolveBase(systemDark);
        var target = CustomSlot(b);
        if (target is null)
        {
            string key = KeyOf(b);
            if (!Overrides.TryGetValue(key, out target))
            {
                target = Presets.Factory.TryGetValue(b, out var factory) ? factory.Clone() : new Palette();
                Overrides[key] = target;
            }
        }
        switch (slot)
        {
            case "background": target.Background = value; break;
            case "surface": target.Surface = value; break;
            case "text": target.Text = value; break;
            case "textMuted": target.TextMuted = value; break;
            case "accent": target.Accent = value; break;
            case "border": target.Border = value; break;
        }
    }

    /// <summary>Reset ONLY the currently selected theme: drop a preset's
    /// override (restoring factory values) or return a Custom slot to its
    /// defaults. Other themes' edits are untouched.</summary>
    public void ResetCurrent(bool systemDark)
    {
        var b = ResolveBase(systemDark);
        switch (b)
        {
            case ThemeAppearance.Custom1: Custom1 = new Palette(); break;
            case ThemeAppearance.Custom2: Custom2 = new Palette(); break;
            case ThemeAppearance.Custom3: Custom3 = new Palette(); break;
            default: Overrides.Remove(KeyOf(b)); break;
        }
    }

    /// <summary>One-time v1.2 migration: import the old single Custom palette
    /// into Custom 1, unless it was one of the untouched historical default
    /// sets (those just become the new defaults). Returns true if anything
    /// changed so Load can persist the migration.</summary>
    public bool MigrateLegacy()
    {
        if (LegacyCustom is null) return false;
        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        bool untouchedVsCodeEra =
            Eq(LegacyCustom.Background, "#1E1E1E") && Eq(LegacyCustom.Surface, "#252526")
            && Eq(LegacyCustom.Text, "#D4D4D4") && Eq(LegacyCustom.TextMuted, "#858585")
            && Eq(LegacyCustom.Accent, "#0E639C") && Eq(LegacyCustom.Border, "#3C3C3C");
        bool untouchedV12Era =
            Eq(LegacyCustom.Background, "#1E1F20") && Eq(LegacyCustom.Surface, "#26272A")
            && Eq(LegacyCustom.Text, "#D3D5D5") && Eq(LegacyCustom.TextMuted, "#868684")
            && Eq(LegacyCustom.Accent, "#10659E") && Eq(LegacyCustom.Border, "#3B3D40");
        if (!untouchedVsCodeEra && !untouchedV12Era)
            Custom1 = LegacyCustom;
        LegacyCustom = null;
        return true;
    }

    /// <summary>Factory preset palettes, all original GlideScroll
    /// compositions, WCAG-contrast checked. User edits never touch these;
    /// they persist separately in <see cref="Overrides"/>.</summary>
    public static class Presets
    {
        public static readonly Dictionary<ThemeAppearance, Palette> Factory = new()
        {
            // Light: white-first body with grey surfaces doing the highlighting.
            [ThemeAppearance.Light] = new()
            {
                Background = "#FDFDFC", Surface = "#F1F2F3",
                Text = "#17191C", TextMuted = "#66696E",
                Accent = "#2E7FD6", Border = "#E2E4E6"
            },
            // Dark: deep blue-navy, the dark half of Auto.
            [ThemeAppearance.Dark] = new()
            {
                Background = "#0B1626", Surface = "#142136",
                Text = "#DCE8F7", TextMuted = "#8296B3",
                Accent = "#3FA0FF", Border = "#1E3049"
            },
            // OLED Electric: true #000000 so OLED pixels are physically off.
            [ThemeAppearance.OledElectric] = new()
            {
                Background = "#000000", Surface = "#0D0D0D",
                Text = "#DEE3EA", TextMuted = "#99A4B2",
                Accent = "#1D7EF7", Border = "#171C26"
            },
            [ThemeAppearance.CozyPink] = new()
            {
                Background = "#FEF0FC", Surface = "#FFFDFE",
                Text = "#491C2F", TextMuted = "#8C6672",
                Accent = "#FF3DA5", Border = "#FBD0E4"
            },
            [ThemeAppearance.CozyBlue] = new()
            {
                Background = "#EAF6FB", Surface = "#FDFEFF",
                Text = "#121316", TextMuted = "#566A78",
                Accent = "#4775FF", Border = "#ACCCFB"
            },
            // Sakura Pop: warm paper with a deliberate dark-navy border frame.
            [ThemeAppearance.SakuraPop] = new()
            {
                Background = "#F7F3EE", Surface = "#FEFCF9",
                Text = "#2B2724", TextMuted = "#736A63",
                Accent = "#FE3460", Border = "#172433"
            },
            [ThemeAppearance.LavenderMilk] = new()
            {
                Background = "#F4F0FE", Surface = "#FDFCFF",
                Text = "#372A55", TextMuted = "#6F6589",
                Accent = "#7D5BE6", Border = "#E2D9F6"
            },
            [ThemeAppearance.GridGraphite] = new()
            {
                Background = "#17181A", Surface = "#1F2124",
                Text = "#E9E9E6", TextMuted = "#8E9094",
                Accent = "#E3364A", Border = "#2C2E31"
            },
            [ThemeAppearance.AizomeIndigo] = new()
            {
                Background = "#1B2432", Surface = "#232E40",
                Text = "#E8E4DA", TextMuted = "#97A0AC",
                Accent = "#6FA3C7", Border = "#303F57"
            },
            [ThemeAppearance.NeonDusk] = new()
            {
                Background = "#12081F", Surface = "#1C1030",
                Text = "#F2E9FF", TextMuted = "#9E8FC0",
                Accent = "#FF2E97", Border = "#2E1D4A"
            },
            [ThemeAppearance.CyanCircuit] = new()
            {
                Background = "#05090F", Surface = "#0D1520",
                Text = "#D7F4FF", TextMuted = "#7FA3B8",
                Accent = "#0AE8CE", Border = "#172433"
            },
            [ThemeAppearance.GreenPhosphor] = new()
            {
                Background = "#0A0F0A", Surface = "#111A12",
                Text = "#BFE8BC", TextMuted = "#6E9A70",
                Accent = "#35E06B", Border = "#1C2B1E"
            },
            [ThemeAppearance.AmberCrt] = new()
            {
                Background = "#100C06", Surface = "#191309",
                Text = "#EFD9B4", TextMuted = "#A98F63",
                Accent = "#E9A028", Border = "#2A2110"
            },
            [ThemeAppearance.AbyssGlow] = new()
            {
                Background = "#04121B", Surface = "#0A1D2A",
                Text = "#D6EEF2", TextMuted = "#7C9FAC",
                Accent = "#19E3C2", Border = "#12303F"
            },
        };
    }
}

public sealed class GeneralConfig
{
    public bool AutostartWithWindows { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowNotificationOnModeToggle { get; set; } = false;
    /// <summary>Physical screen diagonal in inches. Used by "Optimize for monitor"
    /// to compute true PPI and scale step size so the per-notch scroll distance
    /// feels consistent across resolutions. Default 27" (modern gaming sweet spot).</summary>
    public double ScreenDiagonalInches { get; set; } = 27.0;
}

public sealed class AppConfig
{
    public WheelConfig Wheel { get; set; } = new();
    public PanConfig Pan { get; set; } = new();
    public AppListConfig Apps { get; set; } = new();
    public GameModeConfig GameMode { get; set; } = new();
    public ThemeConfig Theme { get; set; } = new();
    public GeneralConfig General { get; set; } = new();

    public static AppConfig Load()
    {
        AppConfig cfg;
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                cfg = new AppConfig();
            }
            else
            {
                string json = File.ReadAllText(path);
                cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            // Corrupted config, start fresh rather than crash.
            cfg = new AppConfig();
        }

        // Clamp any out-of-range tunables (a hand-edited config.json bypasses
        // the UI's clamps).
        cfg.Sanitize();

        // Seed the shipped default class blocks on first run, and once when
        // upgrading from a build that hardcoded these surfaces (older configs
        // have an empty blacklist + no seeded flag). Same one-time pattern for
        // the Game Mode seed list. Persist only if a seed actually changed
        // something so each happens exactly once.
        bool seeded = cfg.Apps.EnsureDefaultBlocks();
        seeded |= cfg.GameMode.EnsureDefaults();
        seeded |= cfg.Theme.MigrateLegacy();
        if (seeded)
        {
            try { cfg.Save(); } catch { /* Save already swallows failures */ }
        }
        return cfg;
    }

    public void Save()
    {
        try
        {
            string path = ConfigPath;
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(this, JsonOptions);
            // Atomic write: serialize to a temp file, then swap it into place so
            // a crash or power loss mid-write can never leave a truncated
            // config.json (which Load() would silently discard, wiping settings).
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch
        {
            // Don't let a config write failure crash the pipeline.
        }
    }

    /// <summary>Clamp all tunables into their valid ranges. Guards against a
    /// hand-edited config.json (which bypasses the UI's clamps) feeding extreme
    /// values into the pipeline, e.g. friction 0 = coast never decays.</summary>
    public void Sanitize()
    {
        Wheel.StepSize = Math.Clamp(Wheel.StepSize, 10, 1000);
        Wheel.AnimationTimeMs = Math.Clamp(Wheel.AnimationTimeMs, 50, 2000);
        Wheel.FeelBias = Math.Clamp(Wheel.FeelBias, -10.0, 10.0);
        Wheel.AccelWindowMs = Math.Clamp(Wheel.AccelWindowMs, 0, 1000);
        Wheel.AccelBoost = Math.Clamp(Wheel.AccelBoost, 1.0, 20.0);
        Pan.Friction = Math.Clamp(Pan.Friction, 1.0, 200.0);
        Pan.SensitivityX = Math.Clamp(Pan.SensitivityX, 1.0, 500.0);
        Pan.SensitivityY = Math.Clamp(Pan.SensitivityY, 1.0, 500.0);
        Pan.ActivationThresholdPixels = Math.Clamp(Pan.ActivationThresholdPixels, 0, 50);
        Pan.MinStepPixels = Math.Clamp(Pan.MinStepPixels, 1, 50);
        Pan.Smoothness = Math.Clamp(Pan.Smoothness, 0.0, 20.0);
        General.ScreenDiagonalInches = Math.Clamp(General.ScreenDiagonalInches, 10.0, 65.0);
    }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlideScroll", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}
