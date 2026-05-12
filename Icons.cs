using System.Drawing.Text;
using System.Reflection;

namespace BroccoKanban;

/// <summary>
/// Loads the Phosphor icon font from the embedded resource and exposes
/// named codepoint constants. Use <see cref="Get"/> to get a sized Font
/// for painting, and the constants as button/label Text values.
/// </summary>
public static class Icons
{
    private static readonly PrivateFontCollection _collection = new();
    private static readonly Dictionary<FontType, FontFamily> _families = new();

    // Call once from Program.Main before Application.Run.
    public static void Load()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        RegisterFont(assembly, "BroccoKanban.Fonts.Phosphor-Bold.ttf", FontType.Bold);
        RegisterFont(assembly, "BroccoKanban.Fonts.Phosphor-Fill.ttf", FontType.Fill);
    }

    private static void RegisterFont(Assembly assembly, string resourceName, FontType fontType)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded font not found: {resourceName}");

        byte[] data = new byte[stream.Length];
        _ = stream.Read(data, 0, data.Length);

        var before = _collection.Families.Select(f => f.Name).ToHashSet();

        unsafe
        {
            fixed (byte* ptr = data)
            {
                _collection.AddMemoryFont((nint)ptr, data.Length);
            }
        }

        FontFamily newFamily = _collection.Families.First(f => !before.Contains(f.Name));
        _families.Add(fontType, newFamily);
    }

    /// <summary>Returns a Phosphor icon font at the requested size.</summary>
    public static Font Get(float size, FontType fontType = FontType.Bold) =>
        new(_families.GetValueOrDefault(fontType) ?? throw new InvalidOperationException("Icons.Load() not called"), size);

    public enum FontType
    {
        Bold,
        Fill
    };

    // ── Icon codepoints ────────────────────────────────────────────────────

    public const string Plus = "\uE3D4";  // plus
    public const string FolderOpen = "\uE256";  // folder-open
    public const string ArrowLeft = "\uE058";  // arrow-left
    public const string Star = "\uE46A";  // star
    public const string PencilLine = "\uE3B2";  // pencil-line
    public const string Folder = "\uE24A";  // folder
    public const string ArrowSquare = "\uE5DE";  // arrow-square-out (Open)
    public const string Trash = "\uE4A6";  // trash
    public const string Palette = "\uE6C8";  // palette
    public const string X = "\uE4F6";  // x
    public const string NotePencil = "\uE34C";  // note-pencil
    public const string MagnifyingGlass = "\uE30C"; // magnifying-glass
}