using System.Text.Json.Serialization;

namespace BroccoKanban;

/// <summary>
/// A single entry in the board path cache, persisted in settings so the app can
/// find each board on next launch without scanning a folder.
/// </summary>
public sealed class BoardEntry
{
    /// <summary>Absolute path to the .knbn file.</summary>
    public string Path { get; set; } = "";

    /// <summary>Last-known display name of the board (kept in sync with BoardFile.Name).</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Persisted user preferences. Serialized to %APPDATA%\BroccoKanban\settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Directory where custom .palette.json files are stored.</summary>
    public string PaletteDirectory { get; set; } = "";

    /// <summary>Per-board path cache. Each entry points to one .knbn file.</summary>
    public List<BoardEntry> BoardEntries { get; set; } = [];

    /// <summary>
    /// Key of the palette used for app-level windows (board list, dialogs).
    /// Falls back to <see cref="PaletteLibrary.DefaultKey"/> if the stored key can't be found.
    /// </summary>
    public string AppPaletteKey { get; set; } = PaletteLibrary.DefaultKey;
}

/// <summary>
/// The full in-memory and on-disk representation of a single Kanban board.
/// Serialized as a .knbn file (JSON). All tasks live in a flat <see cref="Tasks"/> list
/// ordered by column; there is no separate order field.
/// </summary>
public sealed class BoardFile
{
    /// <summary>
    /// Stable unique ID for the board. "N" format = 32 hex chars with no hyphens,
    /// which keeps filenames and JSON compact.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name shown in the board list and title bar.</summary>
    public string Name { get; set; } = "Untitled board";

    /// <summary>When true, the board is pinned to the top of the list and protected from deletion.</summary>
    public bool Favourite { get; set; }

    /// <summary>Key of the active <see cref="BoardPalette"/> (matches <see cref="BoardPalette.Key"/>).</summary>
    public string Palette { get; set; } = PaletteLibrary.DefaultKey;

    /// <summary>All tasks on this board, stored in display order grouped by column.</summary>
    public List<KanbanTask> Tasks { get; set; } = [];

    /// <summary>
    /// Absolute path to the .knbn file on disk. Not persisted to JSON — populated at load time
    /// from the filename so we know where to save back to.
    /// </summary>
    [JsonIgnore]
    public string FilePath { get; set; } = "";

    /// <summary>True when the file at FilePath no longer exists. Set by LoadBoards.</summary>
    [JsonIgnore]
    public bool IsMissing { get; set; }
}

/// <summary>
/// A single task card on the board. Column assignment and display order are both
/// stored here; ordering within a column is determined by position in <see cref="BoardFile.Tasks"/>.
/// </summary>
public sealed class KanbanTask
{
    /// <summary>Stable unique ID used to identify this task during drag-and-drop.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Short title shown in bold on the card.</summary>
    public string Title { get; set; } = "New task";

    /// <summary>Optional longer description shown in italics below the title.</summary>
    public string Notes { get; set; } = "";

    /// <summary>
    /// Which column this task belongs to. Must match one of the values in <see cref="BoardColumns.All"/>.
    /// Stored as a plain string rather than an enum so the JSON is human-readable.
    /// </summary>
    public string Column { get; set; } = BoardColumns.Todo;
}

/// <summary>
/// The four fixed Kanban column names. There is no dynamic column support —
/// all boards always have exactly these four columns in this order.
/// </summary>
/// <remarks>
/// <see cref="All"/> order controls left-to-right column layout and is also the
/// canonical grouping order used when serializing task lists.
/// </remarks>
public static class BoardColumns
{
    public const string Todo = "Todo";
    public const string InProgress = "In Progress";
    public const string Testing = "Testing";
    public const string Complete = "Complete";

    /// <summary>All column names in display order. Iterate this to build column UI.</summary>
    public static readonly string[] All = [Todo, InProgress, Testing, Complete];
}

/// <summary>
/// An immutable color palette that controls every color in the app UI.
/// Declared as a <c>record</c> so "with expressions" can create modified copies without mutating the original.
/// </summary>
/// <remarks>
/// All color values are CSS hex strings (e.g. "#2D6A4F") parsed at render time via <see cref="Ui.ColorFrom"/>.
/// </remarks>
public sealed record BoardPalette(
    /// <summary>URL-safe identifier, e.g. "garden". Used as the lookup key in <see cref="PaletteLibrary"/>.</summary>
    string Key,
    /// <summary>Human-readable name shown in the palette browser.</summary>
    string Name,
    /// <summary>Outermost window/page background color.</summary>
    string Window,
    /// <summary>Card and panel surface color (primary background for most UI elements).</summary>
    string Surface,
    /// <summary>Alternate surface used for column backgrounds and secondary areas.</summary>
    string SurfaceAlt,
    /// <summary>Task card background color.</summary>
    string Card,
    /// <summary>Primary accent used for buttons, highlights, and active borders.</summary>
    string Accent,
    /// <summary>Text color to use on top of <see cref="Accent"/> backgrounds (usually white).</summary>
    string AccentText,
    /// <summary>Default body text color.</summary>
    string Text,
    /// <summary>De-emphasized text color for secondary labels and notes.</summary>
    string Muted,
    /// <summary>Subtle border color for panels and cards.</summary>
    string Border,
    /// <summary>Destructive-action color used for delete buttons and error states.</summary>
    string Danger,
    /// <summary>Drop-shadow color. Semi-transparent dark tones work best.</summary>
    string Shadow = "#2D3748",
    /// <summary>Header band color for the Todo column.</summary>
    string Todo = "#3B82F6",
    /// <summary>Header band color for the In Progress column.</summary>
    string InProgress = "#EF4444",
    /// <summary>Header band color for the Testing column.</summary>
    string Testing = "#EAB308",
    /// <summary>Header band color for the Complete column.</summary>
    string Complete = "#22C55E",
    /// <summary>Whether this palette is starred by the user (custom palettes only).</summary>
    bool Favourite = false);

/// <summary>
/// Registry of the six built-in color presets that ship with the app.
/// Custom palettes live on disk in .palette.json files managed by <see cref="PaletteStore"/>.
/// </summary>
public static class PaletteLibrary
{
    /// <summary>Key of the palette used when no preference has been saved.</summary>
    public const string DefaultKey = "garden";

    /// <summary>
    /// All built-in palette definitions in display order.
    /// These are never written to disk; they are always reconstructed from this list.
    /// </summary>
    public static readonly List<BoardPalette> Presets =
    [
        new(Key: "garden", Name: "Garden focus",
            Window: "#F5F7F2", Surface: "#FFFFFF", SurfaceAlt: "#E7EFE3", Card: "#FFFFFF",
            Accent: "#2D6A4F", AccentText: "#FFFFFF",
            Text: "#1F2A24", Muted: "#607267", Border: "#CBD8CE",
            Danger: "#A23B3B", Shadow: "#2D6A4F",
            Todo: "#4F86A0", InProgress: "#BF6B3A", Testing: "#8CA640", Complete: "#2D7A50"),

        new(Key: "ink", Name: "Ink contrast",
            Window: "#0C0F1C", Surface: "#141824", SurfaceAlt: "#1E2638", Card: "#141824",
            Accent: "#ECF0FF", AccentText: "#0C0F1C",
            Text: "#ECF0FF", Muted: "#6878A8", Border: "#283050",
            Danger: "#E03838", Shadow: "#000008",
            Todo: "#3868C0", InProgress: "#A83828", Testing: "#987018", Complete: "#1C7040"),

        new(Key: "night", Name: "Night calm",
            Window: "#111827", Surface: "#1F2937", SurfaceAlt: "#273244", Card: "#1F2937",
            Accent: "#64B6AC", AccentText: "#10211F",
            Text: "#F9FAFB", Muted: "#CBD5E1", Border: "#334155",
            Danger: "#F87171", Shadow: "#64B6AC",
            Todo: "#5B8ADB", InProgress: "#DB6B6B", Testing: "#D4AA40", Complete: "#4DB88A"),

        new(Key: "latte", Name: "Thought Latte",
            Window: "#EDE0CC", Surface: "#F5ECD8", SurfaceAlt: "#DCCAA8", Card: "#F8F0DF",
            Accent: "#6B3A18", AccentText: "#F5ECD8",
            Text: "#1E0E04", Muted: "#7A5A3A", Border: "#BEAA88",
            Danger: "#A83020", Shadow: "#2A1208",
            Todo: "#5A88A8", InProgress: "#B86820", Testing: "#A88020", Complete: "#508050"),

        new(Key: "mocha", Name: "Thought Mocha",
            Window: "#2A1A10", Surface: "#382418", SurfaceAlt: "#462E20", Card: "#382418",
            Accent: "#B87848", AccentText: "#F0DCC8",
            Text: "#F0DCC8", Muted: "#906848", Border: "#5A3A28",
            Danger: "#C83820", Shadow: "#160C06",
            Todo: "#5A90B0", InProgress: "#C07030", Testing: "#B09030", Complete: "#509060"),

        new(Key: "americano", Name: "Thought Americano",
            Window: "#18100A", Surface: "#221810", SurfaceAlt: "#2E2018", Card: "#221810",
            Accent: "#C8935A", AccentText: "#18100A",
            Text: "#F0E0CC", Muted: "#9A7A60", Border: "#3A2818",
            Danger: "#CC3A20", Shadow: "#100A04",
            Todo: "#6B9EC4", InProgress: "#C47B4A", Testing: "#C4A84A", Complete: "#6B9A6B"),

        new(Key: "rainforest", Name: "Bustling Rainforest",
            Window: "#0A1A0C", Surface: "#142016", SurfaceAlt: "#1C2E1E", Card: "#142016",
            Accent: "#5AD67A", AccentText: "#071008",
            Text: "#C8E8CA", Muted: "#6A9A6C", Border: "#2A4A2C",
            Danger: "#FF5555", Shadow: "#040A04",
            Todo: "#1AA8C8", InProgress: "#E07030", Testing: "#D4B800", Complete: "#38C070"),

        new(Key: "cherry", Name: "Blossoming Cherry",
            Window: "#FDF5F7", Surface: "#FFFFFF", SurfaceAlt: "#FAE8EE", Card: "#FFFFFF",
            Accent: "#D45873", AccentText: "#FFFFFF",
            Text: "#3A1824", Muted: "#AC8A95", Border: "#F0C4CE",
            Danger: "#BC2A44", Shadow: "#6B2838",
            Todo: "#9B7AC8", InProgress: "#D45873", Testing: "#E8A84A", Complete: "#7AAF8A"),

        new(Key: "neon80s", Name: "80's Neon",
            Window: "#08081A", Surface: "#10102A", SurfaceAlt: "#18183A", Card: "#10102A",
            Accent: "#FF00FF", AccentText: "#08081A",
            Text: "#E0E0FF", Muted: "#7070B0", Border: "#28285A",
            Danger: "#FF1744", Shadow: "#000015",
            Todo: "#00E5FF", InProgress: "#FF00FF", Testing: "#FFE600", Complete: "#00FF88"),

        new(Key: "redblack", Name: "Red-Black",
            Window: "#0A0A0A", Surface: "#161616", SurfaceAlt: "#202020", Card: "#161616",
            Accent: "#CC0000", AccentText: "#FFFFFF",
            Text: "#F2F2F2", Muted: "#8A8A8A", Border: "#2E2E2E",
            Danger: "#FF3333", Shadow: "#000000",
            Todo: "#CC5500", InProgress: "#CC0000", Testing: "#881A44", Complete: "#006633"),

        new(Key: "banana", Name: "Banana",
            Window: "#FFFCE6", Surface: "#FFFFFF", SurfaceAlt: "#FFF8C4", Card: "#FFFFFF",
            Accent: "#F5A800", AccentText: "#f3eecf",
            Text: "#1A1400", Muted: "#7A7030", Border: "#E8D870",
            Danger: "#CC3300", Shadow: "#3A2800",
            Todo: "#5B9FE8", InProgress: "#E85530", Testing: "#F5A800", Complete: "#50A850"),

        new(Key: "titanium", Name: "Titanium Sleek",
            Window: "#EFEFEF", Surface: "#F7F7F7", SurfaceAlt: "#E2E4E6", Card: "#F7F7F7",
            Accent: "#5A6068", AccentText: "#FFFFFF",
            Text: "#1C2026", Muted: "#7A848E", Border: "#C4C8CC",
            Danger: "#B03030", Shadow: "#2A2E32",
            Todo: "#6A8098", InProgress: "#887060", Testing: "#7A7A6A", Complete: "#5A7860"),

        new(Key: "argon", Name: "Argon Cloudy",
            Window: "#F5F4FA", Surface: "#FDFCFF", SurfaceAlt: "#EDEAF5", Card: "#FDFCFF",
            Accent: "#7B68C0", AccentText: "#FFFFFF",
            Text: "#1E1A30", Muted: "#8A85A8", Border: "#D0CAEA",
            Danger: "#C04060", Shadow: "#1E1A30",
            Todo: "#6888D0", InProgress: "#A865A0", Testing: "#8898C8", Complete: "#5A98A0"),

        new(Key: "trappist1", Name: "TRAPPIST-1",
            Window: "#080B18", Surface: "#0E1228", SurfaceAlt: "#161E38", Card: "#0E1228",
            Accent: "#C04818", AccentText: "#F0E0D0",
            Text: "#EAD8C8", Muted: "#706070", Border: "#1E2A48",
            Danger: "#E83418", Shadow: "#04060E",
            Todo: "#C06828", InProgress: "#903050", Testing: "#3A4A98", Complete: "#287858"),

        //new(Key: "tranquilvoid", Name: "Tranquil Void",
        //    Window: "#080A12", Surface: "#0E1020", SurfaceAlt: "#0A0C18", Card: "#E8ECFC",
        //    Accent: "#6878C0", AccentText: "#E8ECFC",
        //    Text: "#0C1022", Muted: "#38488A", Border: "#B0BAE0",
        //    Danger: "#B82848", Shadow: "#000008",
        //    Todo: "#3868C8", InProgress: "#8848A8", Testing: "#5878C8", Complete: "#2888A0"),
    ];
}
