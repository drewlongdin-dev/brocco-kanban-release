using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BroccoKanban;

/// <summary>
/// Loads, saves, and deletes custom palette files. Built-in presets from
/// <see cref="PaletteLibrary"/> are always available; this class adds user-created
/// palettes stored as <c>*.palette.json</c> files in a configurable directory.
/// </summary>
/// <remarks>
/// The split between presets (code) and custom (files on disk) means built-in palettes
/// can never be accidentally overwritten by a bad file on disk.
/// </remarks>
public sealed class PaletteStore
{
    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    public readonly string directory;
    private readonly List<BoardPalette> custom = [];

    /// <param name="directory">Path to the folder where custom palette files are stored.</param>
    public PaletteStore(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        LoadCustom();
    }

    /// <summary>All palettes in display order: presets first, then custom alphabetically.</summary>
    public IReadOnlyList<BoardPalette> All => PaletteLibrary.Presets.Concat(custom).ToList();

    /// <summary>The first built-in preset, used as the fallback when no palette is specified.</summary>
    public BoardPalette Default => PaletteLibrary.Presets[0];

    /// <summary>Returns true if the given key belongs to a user-created (non-preset) palette.</summary>
    public bool IsCustom(string key) => custom.Any(p => p.Key == key);

    /// <summary>
    /// Returns the palette with the given key, or <see cref="Default"/> if not found.
    /// Prefer <see cref="TryGet"/> when you need to know whether the key was valid.
    /// </summary>
    public BoardPalette Get(string key) => All.FirstOrDefault(p => p.Key == key) ?? Default;

    /// <summary>
    /// Attempts to find a palette by key. Returns false (and sets <paramref name="palette"/>
    /// to <see cref="Default"/>) when the key does not exist, which lets callers detect
    /// a missing palette and show a warning.
    /// </summary>
    public bool TryGet(string key, out BoardPalette palette)
    {
        palette = All.FirstOrDefault(p => p.Key == key) ?? Default;
        return All.Any(p => p.Key == key);
    }

    /// <summary>Writes a custom palette to disk as <c>{key}.palette.json</c> and updates the in-memory list.</summary>
    /// <remarks>
    /// The filename is derived from the key with non-word characters replaced by hyphens so it is
    /// safe on all Windows filesystems. If a palette with the same key already exists in memory it is
    /// replaced so callers can use this for both create and update.
    /// </remarks>
    public void SaveCustom(BoardPalette palette)
    {
        Directory.CreateDirectory(directory);
        string fileName = Regex.Replace(palette.Key, @"[^\w\-]+", "-");
        string path = Path.Combine(directory, $"{fileName}.palette.json");
        File.WriteAllText(path, JsonSerializer.Serialize(palette, jsonOptions));
        custom.RemoveAll(p => p.Key == palette.Key);
        custom.Add(palette);
    }

    /// <summary>
    /// Deletes a custom palette file from disk and removes it from memory.
    /// Uses <see cref="FindCustomFile"/> rather than re-deriving the filename because a renamed
    /// palette's key and filename may differ.
    /// </summary>
    public void DeleteCustom(string key)
    {
        string? file = FindCustomFile(key);
        if (file is not null && File.Exists(file))
        {
            File.Delete(file);
        }
        custom.RemoveAll(p => p.Key == key);
    }

    /// <summary>
    /// Flips the <see cref="BoardPalette.Favourite"/> flag on a custom palette and saves it to disk.
    /// The <c>with</c> expression creates a modified copy of the immutable record.
    /// </summary>
    public void ToggleFavourite(string key)
    {
        BoardPalette? palette = custom.FirstOrDefault(p => p.Key == key);
        if (palette is null) return;
        SaveCustom(palette with { Favourite = !palette.Favourite });
    }

    /// <summary>
    /// Locates the physical file for a custom palette by key. First tries the expected
    /// filename (derived from the key); falls back to scanning all .palette.json files
    /// and matching on the double-stripped extension (i.e. <c>my-palette</c> from
    /// <c>my-palette.palette.json</c>) to handle cases where the file was renamed externally.
    /// </summary>
    private string? FindCustomFile(string key)
    {
        string fileName = Regex.Replace(key, @"[^\w\-]+", "-");
        string expected = Path.Combine(directory, $"{fileName}.palette.json");
        return File.Exists(expected)
            ? expected
            : Directory.GetFiles(directory, "*.palette.json")
                .FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(f)) == key);
    }

    /// <summary>
    /// Scans the palette directory and deserializes all valid .palette.json files into
    /// <see cref="custom"/>. Files whose key matches a preset key are silently skipped so
    /// a file cannot shadow a built-in palette. Corrupt files are silently ignored so a
    /// single bad file does not block the app from opening.
    /// </summary>
    private void LoadCustom()
    {
        custom.Clear();
        foreach (string file in Directory.GetFiles(directory, "*.palette.json").OrderBy(f => f))
        {
            try
            {
                BoardPalette? palette = JsonSerializer.Deserialize<BoardPalette>(File.ReadAllText(file), jsonOptions);
                if (palette is not null && !PaletteLibrary.Presets.Any(p => p.Key == palette.Key))
                {
                    custom.Add(palette);
                }
            }
            catch
            {
                // Ignore broken custom palettes and keep the app usable.
            }
        }
    }
}

/// <summary>
/// Return value from <see cref="PaletteManager.Show"/>. A non-null value means the user chose a
/// palette; null means they closed the dialog without making a selection.
/// </summary>
public sealed record PaletteDialogResult(string? SelectedPaletteKey);

/// <summary>
/// Shows the palette selection dialog. Handles the re-entry loop needed to refresh
/// the list after a create, edit, delete, or favourite toggle without returning a
/// selection result.
/// </summary>
public static class PaletteManager
{
    /// <summary>Opens the palette browser and returns the user's choice, or null if cancelled.</summary>
    /// <remarks>
    /// Internally loops because edit/delete/favourite actions on a palette need to reopen
    /// the same dialog with a fresh list. The dialog signals "reopen me" by setting its
    /// <see cref="DialogResult"/> to <see cref="DialogResult.Retry"/> before closing.
    /// The loop continues until either a palette is selected (OK) or the dialog is
    /// cancelled without selecting (the Cancel button or the × close button).
    /// </remarks>
    public static PaletteDialogResult? Show(IWin32Window owner, PaletteStore store, string currentKey)
    {
        while (true)
        {
            PaletteDialogResult? result = ShowOnce(owner, store, currentKey);
            if (result is not null || lastDialogWasCancelled) return result;
        }
    }

    // Tracks whether the most recent dialog was dismissed via Cancel (as opposed to Retry).
    // Retry = "reopen me"; Cancel = "user wants to exit". Used by the Show loop above.
    private static bool lastDialogWasCancelled;

    /// <summary>
    /// Creates and displays the palette browser form exactly once, then returns.
    /// Returns a <see cref="PaletteDialogResult"/> if the user selected a palette,
    /// or null if they cancelled or triggered a Retry (edit/delete/favourite action).
    /// </summary>
    private static PaletteDialogResult? ShowOnce(IWin32Window owner, PaletteStore store, string currentKey)
    {
        lastDialogWasCancelled = false;
        // FormBorderStyle.None + BackColor=Magenta + TransparencyKey=Magenta is the
        // standard pattern for borderless rounded dialogs throughout this app.
        // The magenta background becomes fully transparent, exposing only the painted
        // RoundedPanel shell. See also: Prompt, StyledDialog, TaskEditor, PaletteEditor.
        using var form = new Form
        {
            Text = "Palettes",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Width = Ui.Scaled(720),
            Height = Ui.Scaled(680),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Ui.ColorFrom(store.Default.Border),
            Font = new Font("Segoe UI", 10F)
        };
        form.AutoScaleMode = AutoScaleMode.None;

        string? selected = null;
        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(Ui.Scaled(14)),
            Padding = new Padding(Ui.Scaled(18)),
            Radius = Ui.Scaled(20),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(store.Default.Shadow),
            BorderColor = Ui.ColorFrom(store.Default.Border),
            BackColor = Ui.ColorFrom(store.Default.Surface)
        };
        form.Controls.Add(shell);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Ui.ColorFrom(store.Default.Surface) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(layout);

        // The × button closes the form with DialogResult.Cancel, which the Show loop
        // interprets as "user explicitly dismissed — stop looping".
        var close = Ui.MakeCloseButton(store.Default, new Point(form.Width - Ui.Scaled(68), Ui.Scaled(12)));
        shell.Controls.Add(close);
        close.BringToFront();

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            BackColor = Ui.ColorFrom(store.Default.Surface),
            Margin = new Padding(0, 0, 0, Ui.Scaled(14))
        };
        layout.Controls.Add(top, 0, 0);
        var createBtn = Ui.MakeButton("Create Palette", (_, _) =>
        {
            BoardPalette? created = PaletteEditor.Show(form, store.Default, null);
            if (created is null) return;
            store.SaveCustom(created);
            selected = created.Key;
            form.DialogResult = DialogResult.OK;
            form.Close();
        }, store.Default, 122);
        createBtn.Height = Ui.Scaled(32);
        createBtn.Margin = new Padding(0, 0, Ui.Scaled(8), Ui.Scaled(6));
        var openFolder = Ui.MakeIconButton(
            Icons.Folder, "Open palette folder",
            (_, _) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(store.directory);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = store.directory,
                        UseShellExecute = true
                    });
                }
                catch { }
            },
            store.Default, size: 32, iconSize: 11F, quiet: true);
        top.Controls.Add(Ui.MakeSegmentedButton(false, createBtn, openFolder));

        var searchBar = new SearchBar(store.Default)
        {
            Height = Ui.Scaled(48),
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, Ui.Scaled(8)),
            Font = new Font("Segoe UI", 11f),
        };
        layout.Controls.Add(searchBar, 0, 1);

        var scroll = new SmoothFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Ui.ColorFrom(store.Default.Surface),
            Padding = new Padding(0, 0, Ui.Scaled(16), Ui.Scaled(12))
        };
        layout.Controls.Add(scroll, 0, 2);
        scroll.Resize += (_, _) =>
        {
            foreach (Control child in scroll.Controls)
            {
                child.Width = Math.Max(Ui.Scaled(560), scroll.ClientSize.Width - Ui.Scaled(24));
            }
        };

        // Sort: favourited first, then custom before presets, then alphabetically.
        var sortedPalettes = store.All.OrderByDescending(p => p.Favourite).ThenBy(p => store.IsCustom(p.Key) ? 1 : 0).ThenBy(p => p.Name).ToList();
        searchBar.Items = sortedPalettes.Select(p => p.Name).ToArray();
        searchBar.OnSearch = indices =>
        {
            var visible = indices is null ? null : new HashSet<int>(indices);
            int idx = 0;
            foreach (Control card in scroll.Controls)
                card.Visible = visible is null || visible.Contains(idx++);
        };
        foreach (BoardPalette palette in sortedPalettes)
        {
            scroll.Controls.Add(BuildPaletteCard(form, store, palette, currentKey, Math.Max(Ui.Scaled(560), scroll.ClientSize.Width - Ui.Scaled(24)), key =>
            {
                selected = key;
                form.DialogResult = DialogResult.OK;
                form.Close();
            }));
        }

        Ui.SetRoundedRegion(form, shell.Radius, true);

        DialogResult dialogResult = form.ShowDialog(owner);
        if (dialogResult == DialogResult.OK) return new PaletteDialogResult(selected);
        // Retry = action taken (edit/delete/fave), but no palette selected yet. Return null
        // so Show() knows to loop and reopen.
        if (dialogResult == DialogResult.Retry) return null;
        lastDialogWasCancelled = true;
        return null;
    }

    /// <summary>
    /// Builds the card UI for a single palette entry in the browser list.
    /// Cards show the palette name, a <see cref="PaletteBandPreview"/>, and action buttons.
    /// The currently active palette is highlighted with an accent-colored border.
    /// Edit/Delete/Favourite buttons set <see cref="DialogResult.Retry"/> before closing so
    /// the <see cref="Show"/> loop reopens the dialog with a refreshed list.
    /// </summary>
    private static Control BuildPaletteCard(Form form, PaletteStore store, BoardPalette palette, string currentKey, int width, Action<string> select)
    {
        bool custom = store.IsCustom(palette.Key);
        var card = new RoundedPanel
        {
            Width = width,
            Height = Ui.Scaled(132),
            Radius = Ui.Scaled(18),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            BackColor = Ui.ColorFrom(palette.Surface),
            BorderColor = palette.Key == currentKey ? Ui.ColorFrom(palette.Accent) : Ui.ColorFrom(palette.Border),
            Padding = new Padding(Ui.Scaled(14)),
            Margin = new Padding(0, 0, 0, Ui.Scaled(14)),
            Cursor = Cursors.Hand
        };
        card.DoubleClick += (_, _) => select(palette.Key);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Ui.ColorFrom(palette.Surface) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Scaled(210)));

        var preview = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Ui.ColorFrom(palette.Surface) };
        preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(preview, 0, 0);

        preview.Controls.Add(new Label
        {
            Text = palette.Favourite ? $"{palette.Name}  *" : palette.Name,
            AutoSize = true,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Text),
            BackColor = Ui.ColorFrom(palette.Surface)
        }, 0, 0);

        preview.Controls.Add(new Label
        {
            Text = custom ? "Custom palette" : "System palette",
            AutoSize = true,
            ForeColor = Ui.ColorFrom(palette.Muted),
            BackColor = Ui.ColorFrom(palette.Surface)
        }, 0, 1);

        var bands = new PaletteBandPreview
        {
            Dock = DockStyle.Fill,
            Height = Ui.Scaled(34),
            Margin = new Padding(0, Ui.Scaled(12), 0, Ui.Scaled(10)),
            Colors = [palette.Todo, palette.InProgress, palette.Testing, palette.Complete],
            Radius = Ui.Scaled(9)
        };
        preview.Controls.Add(bands, 0, 2);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Ui.ColorFrom(palette.Surface),
            Padding = new Padding(0, 4, 0, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        layout.Controls.Add(actions, 1, 0);
        card.Controls.Add(layout);

        if (custom)
        {
            var segmented = Ui.MakeSegmentedButton(vertical: false,
                Ui.MakeIconButton(Icons.Star, palette.Favourite ? "Unfavourite" : "Favourite",
                    (_, _) => { store.ToggleFavourite(palette.Key); form.DialogResult = DialogResult.Retry; form.Close(); },
                    palette: palette, size: 36, iconSize: 13f,
                    fontType: palette.Favourite ? Icons.FontType.Fill : Icons.FontType.Bold,
                    quiet: !palette.Favourite),
                Ui.MakeIconButton(Icons.PencilLine, "Edit",
                    (_, _) => { var edited = PaletteEditor.Show(form, palette, palette.Key); if (edited is null) return; store.SaveCustom(edited); form.DialogResult = DialogResult.Retry; form.Close(); },
                    palette: palette, size: 36, iconSize: 13f, quiet: true),
                Ui.MakeIconButton(Icons.Trash, "Delete",
                    (_, _) => { if (!StyledDialog.Confirm(form, "Delete palette", $"Delete \"{palette.Name}\"?", palette)) return; store.DeleteCustom(palette.Key); form.DialogResult = DialogResult.Retry; form.Close(); },
                    palette: palette, size: 36, iconSize: 13f, danger: true)
            );
            segmented.Margin = new Padding(0, 0, Ui.Scaled(4), 0);
            actions.Controls.Add(segmented);
        }

        var useBtn = Ui.MakeButton("Use", (_, _) => select(palette.Key), palette, 72);
        useBtn.Height = Ui.Scaled(36);
        useBtn.Margin = new Padding(Ui.Scaled(4), 0, Ui.Scaled(8), 0);
        actions.Controls.Add(useBtn);

        return card;
    }

}

/// <summary>
/// Modal dialog for creating or editing a custom palette. The caller passes a seed palette
/// to pre-populate the color values; for a new palette <paramref name="existingKey"/> is null.
/// Returns the completed <see cref="BoardPalette"/> on OK, or null if cancelled.
/// </summary>
public static class PaletteEditor
{
    /// <param name="owner">Parent window for modal positioning.</param>
    /// <param name="seed">Initial color values shown in the editor.</param>
    /// <param name="existingKey">
    /// Key of the palette being edited, or null when creating a new one.
    /// Passing a key preserves it on save so the board reference stays valid.
    /// </param>
    public static BoardPalette? Show(IWin32Window owner, BoardPalette seed, string? existingKey)
    {
        // See PaletteManager.ShowOnce for an explanation of the borderless dialog pattern.
        using var form = new Form
        {
            Text = existingKey is null ? "Create Palette" : "Edit Palette",
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Width = Ui.Scaled(760),
            Height = Ui.Scaled(620),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Ui.ColorFrom(seed.Border),
            Font = new Font("Segoe UI", 10F)
        };
        form.AutoScaleMode = AutoScaleMode.None;

        // Live editable color values keyed by BoardPalette property name.
        // Mutated in-place when the user picks a color from ColorDialog.
        var values = new Dictionary<string, string>
        {
            ["Window"] = seed.Window,
            ["Surface"] = seed.Surface,
            ["SurfaceAlt"] = seed.SurfaceAlt,
            ["Card"] = seed.Card,
            ["Accent"] = seed.Accent,
            ["AccentText"] = seed.AccentText,
            ["Text"] = seed.Text,
            ["Muted"] = seed.Muted,
            ["Border"] = seed.Border,
            ["Danger"] = seed.Danger,
            ["Shadow"] = seed.Shadow,
            ["Todo"] = seed.Todo,
            ["InProgress"] = seed.InProgress,
            ["Testing"] = seed.Testing,
            ["Complete"] = seed.Complete
        };

        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(Ui.Scaled(14)),
            Padding = new Padding(Ui.Scaled(18)),
            Radius = Ui.Scaled(20),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(seed.Shadow),
            BorderColor = Ui.ColorFrom(seed.Border),
            BackColor = Ui.ColorFrom(seed.Surface)
        };
        form.Controls.Add(shell);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Ui.ColorFrom(seed.Surface)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Scaled(150)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(layout);

        var close = Ui.MakeCloseButton(seed, new Point(form.Width - Ui.Scaled(68), Ui.Scaled(12)));
        shell.Controls.Add(close);
        close.BringToFront();

        var nameBox = new TextBox { Text = existingKey is null ? "My palette" : seed.Name, Dock = DockStyle.Top, Margin = new Padding(0, 0, Ui.Scaled(48), Ui.Scaled(14)), BackColor = Ui.ColorFrom(seed.Card), ForeColor = Ui.ColorFrom(seed.Text), BorderStyle = BorderStyle.FixedSingle };
        layout.Controls.Add(nameBox, 0, 0);

        var preview = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, Ui.Scaled(14)), Padding = new Padding(Ui.Scaled(14)), Radius = Ui.Scaled(16), Shadow = true, ShadowColor = Ui.ColorFrom(seed.Shadow), BorderColor = Ui.ColorFrom(seed.Border), BackColor = Ui.ColorFrom(seed.SurfaceAlt) };
        layout.Controls.Add(preview, 0, 1);
        var previewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = preview.BackColor };
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Scaled(38)));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        preview.Controls.Add(previewLayout);
        var previewTitle = new Label { Text = "Context preview", AutoSize = true, Font = new Font("Segoe UI", 12F, FontStyle.Bold), BackColor = preview.BackColor };
        previewLayout.Controls.Add(previewTitle, 0, 0);
        var previewBands = new PaletteBandPreview { Dock = DockStyle.Fill, Margin = new Padding(0, Ui.Scaled(8), 0, Ui.Scaled(6)), Radius = Ui.Scaled(8) };
        previewLayout.Controls.Add(previewBands, 0, 1);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoScroll = true,
            BackColor = Ui.ColorFrom(seed.Surface)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(grid, 0, 2);

        // Local function that syncs the preview panel with the current values dictionary.
        // Called after every color change so the user sees their edits in real time.
        void RefreshPreview()
        {
            preview.BackColor = Ui.ColorFrom(values["SurfaceAlt"]);
            preview.BorderColor = Ui.ColorFrom(values["Border"]);
            preview.ShadowColor = Ui.ColorFrom(values["Shadow"]);
            previewLayout.BackColor = preview.BackColor;
            previewTitle.ForeColor = Ui.ColorFrom(values["Text"]);
            previewTitle.BackColor = preview.BackColor;
            previewBands.Colors = [values["Todo"], values["InProgress"], values["Testing"], values["Complete"]];
            previewBands.Invalidate();
            preview.Invalidate();
        }

        foreach (string key in values.Keys.ToList())
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = LabelFor(key), AutoSize = true, Padding = new Padding(0, Ui.Scaled(8), 0, 0), ForeColor = Ui.ColorFrom(seed.Text), BackColor = Ui.ColorFrom(seed.Surface) }, 0, row);

            var swatch = new Button
            {
                Width = Ui.Scaled(96),
                Height = Ui.Scaled(30),
                Margin = new Padding(Ui.Scaled(8), Ui.Scaled(2), 0, Ui.Scaled(6)),
                BackColor = Ui.ColorFrom(values[key]),
                Text = values[key],
                ForeColor = BestText(Ui.ColorFrom(values[key])),
                Tag = key
            };
            swatch.Click += (_, _) =>
            {
                using var dialog = new ColorDialog
                {
                    Color = Ui.ColorFrom(values[key]),
                    FullOpen = true
                };
                if (dialog.ShowDialog(form) == DialogResult.OK)
                {
                    values[key] = ColorTranslator.ToHtml(dialog.Color);
                    swatch.BackColor = dialog.Color;
                    swatch.Text = values[key];
                    swatch.ForeColor = BestText(dialog.Color);
                    RefreshPreview();
                }
            };
            grid.Controls.Add(swatch, 1, row);
        }
        RefreshPreview();

        var hint = new Label
        {
            Text = "Custom palettes are saved locally as JSON files.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, Ui.Scaled(10), 0, Ui.Scaled(8))
        };
        hint.BackColor = Ui.ColorFrom(seed.Surface);
        layout.Controls.Add(hint, 0, 3);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = Ui.ColorFrom(seed.Surface)
        };
        var ok = new ModernButton
        {
            Text = existingKey is null ? "Create" : "Save",
            DialogResult = DialogResult.OK,
            Width = Ui.Scaled(96),
            BackColor = Ui.ColorFrom(seed.Accent),
            ForeColor = Ui.ColorFrom(seed.AccentText),
            Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
        };
        var cancel = new ModernButton
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = Ui.Scaled(96),
            BackColor = Ui.ColorFrom(seed.SurfaceAlt),
            ForeColor = Ui.ColorFrom(seed.Text),
            BorderColor = Ui.ColorFrom(seed.Border),
            Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
        };
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 4);

        form.AcceptButton = ok;
        form.CancelButton = cancel;

        Ui.SetRoundedRegion(form, shell.Radius, true);

        if (form.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(nameBox.Text)) return null;

        // For new palettes, derive the key from the name. For edits, keep the original key so
        // boards that reference it by key don't lose their palette association.
        string keyName = existingKey ?? "custom-" + Regex.Replace(nameBox.Text.Trim().ToLowerInvariant(), @"[^\w\-]+", "-").Trim('-');
        if (keyName == "custom-") keyName = $"custom-{Guid.NewGuid():N}";

        return new BoardPalette(
            keyName,
            nameBox.Text.Trim(),
            values["Window"],
            values["Surface"],
            values["SurfaceAlt"],
            values["Card"],
            values["Accent"],
            values["AccentText"],
            values["Text"],
            values["Muted"],
            values["Border"],
            values["Danger"],
            values["Shadow"],
            values["Todo"],
            values["InProgress"],
            values["Testing"],
            values["Complete"],
            seed.Favourite);
    }

    /// <summary>Returns a human-readable label for a color field name (handles the two special cases).</summary>
    private static string LabelFor(string key) => key switch
    {
        "SurfaceAlt" => "Column surface",
        "AccentText" => "Accent text",
        _ => Regex.Replace(key, "([a-z])([A-Z])", "$1 $2")
    };

    /// <summary>
    /// Returns black or white, whichever is more legible on top of <paramref name="color"/>.
    /// Uses the standard ITU-R BT.601 perceptual luminance weights (R×0.299, G×0.587, B×0.114)
    /// normalized to [0, 1]; values above 0.55 are treated as light backgrounds.
    /// </summary>
    private static Color BestText(Color color)
    {
        double luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255;
        return luminance > 0.55 ? Color.Black : Color.White;
    }
}
