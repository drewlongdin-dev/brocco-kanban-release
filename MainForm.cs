using System.Text.Json;

namespace BroccoKanban;

/// <summary>
/// The single application window. It has two logical views that share the same Form:
/// the <em>board list</em> (shown on startup) and the <em>open board</em> (shown when
/// a board is double-clicked or opened).
/// </summary>
/// <remarks>
/// Switching views clears <see cref="root"/> and rebuilds all child controls from scratch
/// rather than showing/hiding panels.
/// </remarks>
public sealed class MainForm : Form
{
    // Win32 message ID that tells a window to stop (or resume) redrawing itself.
    // Used in SetRedraw() below.
    private const int WM_SETREDRAW = 0x0B;

    // P/Invoke declaration: calls the native Windows SendMessage function.
    // This is how C# talks to the Win32 API when the .NET wrapper doesn't exist.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    // %APPDATA%\BroccoKanban — stores settings.json and the Palettes subfolder.
    private readonly string appDirectory;
    private readonly string settingsPath;

    private AppSettings settings = new();
    private PaletteStore paletteStore = null!;
    private readonly List<BoardFile> boards = [];

    // root is the single full-window panel. Both views (board list and open board) build
    // their controls inside it; ShowBoardList / ShowOpenBoard clear it and rebuild.
    private readonly SmoothPanel root = new() { Dock = DockStyle.Fill };

    // Per-column task areas and drop indicators, populated when a board is open.
    // Keys are column name strings from BoardColumns.All.
    private readonly Dictionary<string, SmoothPanel> taskLists = [];
    private readonly Dictionary<string, DropIndicatorControl> dropIndicators = [];

    private BoardFile? currentBoard;
    private BoardPalette palette = PaletteLibrary.Presets[0];

    // Drag state. All three fields are null/0 when no drag is in progress.
    private DragPreviewForm? dragPreview;
    private string? draggingTaskId;
    private string? dragTargetColumn;
    private int dragTargetIndex;

    // Task scrolling values
    private readonly Dictionary<SmoothPanel, (float Current, float Target)> _scrollStates = new();
    private System.Windows.Forms.Timer? _scrollLerpTimer;

    private const float ScrollLerpFactor = 0.22f;
    private const float ScrollStopThresh = 0.5f;

    public MainForm(string? openFilePath = null)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "Brocco Kanban";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        MinimumSize = new Size(980, 620);
        Size = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;

        appDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BroccoKanban");
        settingsPath = Path.Combine(appDirectory, "settings.json");

        InitScrollLerp();

        Controls.Add(root);
        LoadSettings();
        paletteStore = new PaletteStore(settings.PaletteDirectory);
        LoadBoards();
        // ShowBoardList moved to Load so DeviceDpi reflects the real monitor DPI.
        // InitDpi must run first so factory methods called by ShowBoardList can use Ui.Scaled().
        Load += (_, _) =>
        {
            Ui.InitDpi(this);
            if (openFilePath != null)
            {
                var board = LoadBoardFromPath(openFilePath);
                if (board != null)
                    OpenBoard(board);
                else
                    ShowBoardList();
            }
            else
            {
                ShowBoardList();
            }
        };
    }

    /// <summary>Suspends or resumes window redraws using the Win32 WM_SETREDRAW message.</summary>
    /// <remarks>
    /// Significantly faster than SuspendLayout alone for large UI rebuilds because
    /// it prevents Windows from processing any paint messages until re-enabled.
    /// Always call <c>SetRedraw(false)</c> before bulk control changes and
    /// <c>SetRedraw(true)</c> (+ <see cref="Control.Refresh"/>) immediately after.
    /// </remarks>
    private void SetRedraw(bool enabled)
    {
        SendMessage(Handle, WM_SETREDRAW, enabled ? 1 : 0, 0);
        if (enabled)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Loads settings from disk, applies defaults for missing directories, creates the
    /// palette directory if needed, then saves back so the file is always up to date.
    /// Migrates legacy single-folder installs by scanning BoardDirectory once and
    /// converting each .knbn file found there into a BoardEntry.
    /// </summary>
    private void LoadSettings()
    {
        Directory.CreateDirectory(appDirectory);
        if (File.Exists(settingsPath))
        {
            settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(settingsPath), jsonOptions) ?? new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(settings.PaletteDirectory))
        {
            settings.PaletteDirectory = Path.Combine(appDirectory, "Palettes");
        }

        Directory.CreateDirectory(settings.PaletteDirectory);

        SaveSettings();
    }

    /// <summary>Serializes current settings to <see cref="settingsPath"/>.</summary>
    private void SaveSettings()
    {
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, jsonOptions));
    }

    private void InitScrollLerp()
    {
        _scrollLerpTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _scrollLerpTimer.Tick += OnScrollLerpTick;
    }

    private BoardFile? LoadBoardFromPath(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path).Trim();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}", "BroccoKanban", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        bool alreadyRegistered = settings.BoardEntries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(json))
        {
            var blank = new BoardFile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Path.GetFileNameWithoutExtension(path),
                Favourite = false,
                Palette = PaletteLibrary.DefaultKey,
                Tasks = []
            };
            File.WriteAllText(path, JsonSerializer.Serialize(blank, jsonOptions));
            blank.FilePath = path;
            if (!alreadyRegistered)
            {
                boards.Add(blank);
                settings.BoardEntries.Add(new BoardEntry { Name = blank.Name, Path = path });
                SaveSettings();
            }
            return blank;
        }

        try
        {
            var board = JsonSerializer.Deserialize<BoardFile>(json, jsonOptions);
            if (board is null) throw new Exception("Deserialised to null.");
            board.FilePath = path;
            board.Tasks ??= [];
            board.Palette = string.IsNullOrWhiteSpace(board.Palette) ? PaletteLibrary.DefaultKey : board.Palette;
            if (!alreadyRegistered)
            {
                boards.Add(board);
                settings.BoardEntries.Add(new BoardEntry { Name = board.Name, Path = path });
                SaveSettings();
            }
            return board;
        }
        catch
        {
            MessageBox.Show(
                $"\"{Path.GetFileName(path)}\" doesn't appear to be a valid BroccoKanban board file.",
                "BroccoKanban",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }
    }

    /// <summary>
    /// Populates <see cref="boards"/> from the per-board path cache in settings.
    /// Files that no longer exist produce a ghost <see cref="BoardFile"/> with
    /// <see cref="BoardFile.IsMissing"/> set so the UI can show an error state.
    /// Cached entry names are refreshed from the file contents on every load.
    /// </summary>
    private void LoadBoards()
    {
        boards.Clear();
        bool anyNamesUpdated = false;

        foreach (BoardEntry entry in settings.BoardEntries)
        {
            if (!File.Exists(entry.Path))
            {
                boards.Add(new BoardFile { Name = entry.Name, FilePath = entry.Path, IsMissing = true });
                continue;
            }

            try
            {
                BoardFile? board = JsonSerializer.Deserialize<BoardFile>(File.ReadAllText(entry.Path), jsonOptions);
                if (board is null) continue;
                board.FilePath = entry.Path;
                board.Tasks ??= [];
                board.Palette = string.IsNullOrWhiteSpace(board.Palette) ? PaletteLibrary.DefaultKey : board.Palette;

                if (entry.Name != board.Name)
                {
                    entry.Name = board.Name;
                    anyNamesUpdated = true;
                }

                boards.Add(board);
            }
            catch
            {
                boards.Add(new BoardFile { Name = entry.Name, FilePath = entry.Path, IsMissing = true });
            }
        }

        if (anyNamesUpdated) SaveSettings();
    }

    /// <summary>
    /// Switches to the board-list view. Clears <see cref="root"/>, rebuilds the entire
    /// page layout, and populates it with a card per board. Favourited boards sort to the top.
    /// Uses SetRedraw + SuspendLayout to batch the rebuild without visual flicker.
    /// </summary>
    private void ShowBoardList()
    {
        LoadBoards();
        SetRedraw(false);
        SuspendLayout();
        currentBoard = null;
        palette = ResolveAppPalette();
        root.Controls.Clear();
        root.BackColor = Ui.ColorFrom(palette.Window);

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(this.DpiScaled(24)),
            BackColor = Ui.ColorFrom(palette.Window)
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Scaled(96)));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(page);

        var paletteSelect = Ui.MakeIconButton(
            Icons.Palette, "Select app palette",
            (_, _) => { ChooseAppPalette(); },
            palette: palette, size: 60, quiet: true
        );
        paletteSelect.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        paletteSelect.Location = new Point(root.Width - this.DpiScaled(72), this.DpiScaled(12));
        root.Controls.Add(paletteSelect);
        paletteSelect.BringToFront();

        page.Controls.Add(new Label
        {
            Text = "Board List",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 36F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Text),
            Margin = new Padding(0, 0, 0, this.DpiScaled(24))
        }, 0, 0);

        var toolbar = Ui.MakeSegmentedButton(vertical: false,
            Ui.MakeIconButton(
                Icons.Plus, "New board", (_, _) => CreateBoard(),
                palette: palette, size: 64, iconSize: 18f),
            Ui.MakeIconButton(
                Icons.FolderOpen, "Load board", (_, _) => LoadBoard(),
                palette: palette, size: 64, iconSize: 18f, quiet: true));
        toolbar.Margin = new Padding(0);

        var toolbarRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, this.DpiScaled(8))
        };
        toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbarRow.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Scaled(64)));
        page.Controls.Add(toolbarRow, 0, 1);

        toolbarRow.Controls.Add(toolbar, 0, 0);

        var searchBar = new SearchBar(palette)
        {
            Height = Ui.Scaled(64),
            Dock = DockStyle.Top,
            Margin = new Padding(this.DpiScaled(8), 0, this.DpiScaled(9), 0),
            Font = new Font("Segoe UI", 16f),
        };
        toolbarRow.Controls.Add(searchBar, 1, 0);

        var scroll = new SmoothFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, this.DpiScaled(2), this.DpiScaled(12), this.DpiScaled(12))
        };
        page.Controls.Add(scroll, 0, 2);
        scroll.Resize += (_, _) =>
        {
            foreach (Control child in scroll.Controls)
            {
                child.Width = Math.Max(this.DpiScaled(460), scroll.ClientSize.Width);
            }
        };

        var sortedBoards = boards.OrderByDescending(b => b.Favourite).ThenBy(b => b.Name).ToList();
        searchBar.Items = sortedBoards.Select(b => b.Name).ToArray();
        searchBar.OnSearch = indices =>
        {
            var visible = indices is null ? null : new HashSet<int>(indices);
            int i = 0;
            foreach (Control card in scroll.Controls)
                card.Visible = visible is null || visible.Contains(i++);
        };
        foreach (BoardFile board in sortedBoards)
        {
            scroll.Controls.Add(BuildBoardCard(board, Math.Max(this.DpiScaled(460), scroll.ClientSize.Width)));
        }

        ResumeLayout();
        SetRedraw(true);
    }

    /// <summary>
    /// Builds the card control for a single board entry in the board list.
    /// Favourite boards get an accent-colored border and a slightly tinted background.
    /// The card's <see cref="Control.Tag"/> stores the <see cref="BoardFile"/> so event
    /// handlers can retrieve the board without capturing a closure over a loop variable.
    /// </summary>
    private Control BuildBoardCard(BoardFile board, int width)
    {
        if (board.IsMissing)
            return BuildMissingBoardCard(board, width);

        Color border = board.Favourite ? Ui.ColorFrom(palette.Accent) : Ui.ColorFrom(palette.Border);
        Color back = board.Favourite
            ? Ui.Blend(Ui.ColorFrom(palette.Surface), Ui.ColorFrom(palette.Accent), 0.08f)
            : Ui.ColorFrom(palette.Surface);

        var card = new RoundedPanel
        {
            Width = width,
            Height = this.DpiScaled(110),
            Radius = this.DpiScaled(16),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            BorderColor = border,
            BackColor = back,
            Margin = new Padding(0, 0, 0, this.DpiScaled(12)),
            Padding = new Padding(this.DpiScaled(16)),
            Tag = board
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.Controls.Add(layout);

        var info = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Color.Transparent };
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(info, 0, 0);

        info.Controls.Add(new Label
        {
            Text = board.Favourite ? $"{board.Name}  *" : board.Name,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font(Font.FontFamily, 12.5F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Text),
            AutoSize = false,
            Height = this.DpiScaled(24)
        }, 0, 0);
        info.Controls.Add(new Label
        {
            Text = Path.GetDirectoryName(board.FilePath) ?? board.FilePath,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Ui.ColorFrom(palette.Muted),
            Font = new Font(Font.FontFamily, 8.5F),
            AutoSize = false,
            Height = this.DpiScaled(18)
        }, 0, 1);
        info.Controls.Add(new Label
        {
            Text = $"{board.Tasks.Count} tasks   {Path.GetFileName(board.FilePath)}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Ui.ColorFrom(palette.Muted)
        }, 0, 2);

        //var actions = new FlowLayoutPanel
        //{
        //    AutoSize = true,
        //    WrapContents = false,
        //    FlowDirection = FlowDirection.LeftToRight,
        //    BackColor = Color.Transparent,
        //    Padding = new Padding(0, 0, 0, 0)
        //};
        //layout.Controls.Add(actions, 1, 0);
        int buttonPadding = 16;
        int buttonSize = 103 - buttonPadding * 2;
        var actions = Ui.MakeSegmentedButton(vertical: false,
            Ui.MakeIconButton(
                Icons.Star,
                board.Favourite ? "Unfavourite" : "Favourite",
                (_, _) => ToggleFavourite(board),
                palette: palette,
                size: buttonSize,
                fontType: board.Favourite ? Icons.FontType.Fill : Icons.FontType.Bold,
                quiet: !board.Favourite),
            Ui.MakeIconButton(
                Icons.PencilLine, "Rename",
                (_, _) => { RenameBoard(board); ShowBoardList(); },
                palette: palette, size: buttonSize, quiet: true),
            Ui.MakeIconButton(
                Icons.Folder, "Move", (_, _) => MoveBoard(board),
                palette: palette, size: buttonSize, quiet: true),
            Ui.MakeIconButton(
                Icons.ArrowSquare, "Open", (_, _) => OpenBoard(board),
                palette: palette, size: buttonSize),
            Ui.MakeIconButton(
                Icons.Trash, "Delete", (_, _) => DeleteBoard(board),
                palette: palette, size: buttonSize, danger: true));
        actions.Margin = new Padding(0, 0, this.DpiScaled(buttonPadding / 2), this.DpiScaled(buttonPadding));
        actions.Radius = this.DpiScaled(16 - buttonPadding / 2);
        layout.Controls.Add(actions, 1, 0);
        return card;
    }

    private Control BuildMissingBoardCard(BoardFile board, int width)
    {
        Color fadedBack = Ui.Blend(Ui.ColorFrom(palette.Surface), Ui.ColorFrom(palette.Window), 0.55f);
        Color fadedBorder = Ui.Blend(Ui.ColorFrom(palette.Border), Ui.ColorFrom(palette.Window), 0.55f);

        var card = new RoundedPanel
        {
            Width = width,
            Height = this.DpiScaled(116),
            Radius = this.DpiScaled(16),
            Shadow = false,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            BorderColor = fadedBorder,
            BackColor = fadedBack,
            Margin = new Padding(0, 0, 0, this.DpiScaled(12)),
            Padding = new Padding(this.DpiScaled(16)),
            Tag = board
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.Controls.Add(layout);

        var info = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, BackColor = Color.Transparent };
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(info, 0, 0);

        info.Controls.Add(new Label
        {
            Text = board.Name,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font(Font.FontFamily, 12.5F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Muted),
            AutoSize = false,
            Height = this.DpiScaled(24)
        }, 0, 0);
        info.Controls.Add(new Label
        {
            Text = board.FilePath,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Ui.ColorFrom(palette.Muted),
            AutoSize = false,
            Height = this.DpiScaled(18)
        }, 0, 1);
        info.Controls.Add(new Label
        {
            Text = "Board cannot be found. Has it been moved, deleted or renamed?",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Ui.ColorFrom(palette.Danger),
            Font = new Font(Font.FontFamily, 9F),
            AutoSize = false
        }, 0, 2);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 12, 0, 0)
        };
        layout.Controls.Add(actions, 1, 0);
        actions.Controls.Add(Ui.MakeDangerButton("Remove", (_, _) => DeleteBoard(board), palette: palette, 90));

        return card;
    }

    /// <summary>
    /// Opens a save-file dialog so the user picks a location and filename for a new board.
    /// The board name is taken from the chosen filename (without extension).
    /// </summary>
    private void CreateBoard()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Create board",
            Filter = "Kanban boards|*.knbn",
            DefaultExt = "knbn",
            FileName = "My board"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string name = Path.GetFileNameWithoutExtension(dialog.FileName).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var board = new BoardFile { Name = name, FilePath = dialog.FileName };
        SaveBoard(board);
        settings.BoardEntries.Add(new BoardEntry { Path = board.FilePath, Name = board.Name });
        SaveSettings();
        boards.Add(board);
        ShowBoardList();
    }

    /// <summary>
    /// Opens a file picker so the user can add one or more existing .knbn files to the list.
    /// Already-tracked paths are skipped. New entries are added to the cache, then boards reload.
    /// </summary>
    private void LoadBoard()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Load board",
            Filter = "Kanban boards|*.knbn",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        bool added = false;
        foreach (string file in dialog.FileNames)
        {
            bool alreadyTracked = settings.BoardEntries.Any(e =>
                string.Equals(e.Path, file, StringComparison.OrdinalIgnoreCase));
            if (alreadyTracked) continue;

            settings.BoardEntries.Add(new BoardEntry
            {
                Path = file,
                Name = Path.GetFileNameWithoutExtension(file)
            });
            added = true;
        }

        if (!added) return;

        SaveSettings();
        LoadBoards();
        ShowBoardList();
    }

    /// <summary>
    /// Resolves the board's palette (falling back to the default if missing), then
    /// switches to the open-board view.
    /// </summary>
    private void OpenBoard(BoardFile board)
    {
        currentBoard = board;
        if (!paletteStore.TryGet(board.Palette, out palette))
        {
            board.Palette = PaletteLibrary.DefaultKey;
            SaveBoard(board);
            StyledDialog.Message(this, "Palette missing",
                "This board used a palette that could not be found, so Garden focus was applied.", palette);
        }
        ShowOpenBoard();
    }

    /// <summary>Prompts for a new name, saves the board file in place, and syncs the cache.</summary>
    private void RenameBoard(BoardFile board)
    {
        string? name = Prompt.Show("Rename board", "Board name", board.Name, palette);
        if (string.IsNullOrWhiteSpace(name)) return;
        board.Name = name.Trim();
        SaveBoard(board);

        BoardEntry? entry = settings.BoardEntries.FirstOrDefault(e =>
            string.Equals(e.Path, board.FilePath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) entry.Name = board.Name;
        SaveSettings();
    }

    /// <summary>
    /// Opens a folder picker, moves the board's .knbn file to the chosen folder,
    /// and updates the cache entry path.
    /// </summary>
    private void MoveBoard(BoardFile board)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Move board to folder",
            SelectedPath = Path.GetDirectoryName(board.FilePath) ?? "",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string newPath = Path.Combine(dialog.SelectedPath, Path.GetFileName(board.FilePath));
        if (string.Equals(newPath, board.FilePath, StringComparison.OrdinalIgnoreCase)) return;

        if (File.Exists(newPath))
        {
            StyledDialog.Message(this, "File already exists",
                $"A file named \"{Path.GetFileName(board.FilePath)}\" already exists in that folder.", palette);
            return;
        }

        File.Move(board.FilePath, newPath);

        BoardEntry? entry = settings.BoardEntries.FirstOrDefault(e =>
            string.Equals(e.Path, board.FilePath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) entry.Path = newPath;
        SaveSettings();

        board.FilePath = newPath;
        ShowBoardList();
    }

    /// <summary>Flips the favourite flag on a board, saves it, and refreshes the list.</summary>
    private void ToggleFavourite(BoardFile board)
    {
        board.Favourite = !board.Favourite;
        SaveBoard(board);
        ShowBoardList();
    }

    /// <summary>
    /// Deletes (or removes) a board. For missing boards only the cache entry is removed.
    /// For present boards the .knbn file is deleted and the entry removed from the cache.
    /// Deletion of present boards is blocked when the board is favourited.
    /// </summary>
    private void DeleteBoard(BoardFile board)
    {
        if (board.IsMissing)
        {
            if (!StyledDialog.Confirm(this, "Remove board",
                $"Remove \"{board.Name}\" from the board list?", palette)) return;
        }
        else
        {
            if (board.Favourite)
            {
                StyledDialog.Message(this, "Favourite board", "This board is favourited, so deletion is blocked.", palette);
                return;
            }
            if (!StyledDialog.Confirm(this, "Delete board", $"Delete \"{board.Name}\"?", palette)) return;
            File.Delete(board.FilePath);
        }

        settings.BoardEntries.RemoveAll(e =>
            string.Equals(e.Path, board.FilePath, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
        boards.Remove(board);
        ShowBoardList();
    }

    /// <summary>
    /// Switches to the open-board view. Clears <see cref="root"/>, resets per-column state
    /// dictionaries, builds the toolbar and the four-column grid, then fills each column with
    /// task cards. Uses SetRedraw + SuspendLayout to prevent visual tearing during the rebuild.
    /// </summary>
    private void ShowOpenBoard()
    {
        if (currentBoard is null) return;

        SetRedraw(false);
        SuspendLayout();
        taskLists.Clear();
        dropIndicators.Clear();
        root.Controls.Clear();
        root.BackColor = Ui.ColorFrom(palette.Window);

        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(this.DpiScaled(18)),
            BackColor = Ui.ColorFrom(palette.Window)
        };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(page);

        var toolbar = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 3,
            Margin = new Padding(0, 0, 0, this.DpiScaled(14)),
            BackColor = Color.Transparent
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        page.Controls.Add(toolbar, 0, 0);

        toolbar.Controls.Add(Ui.MakeIconButton(
            Icons.ArrowLeft, "Back", (_, _) => ShowBoardList(),
            palette: palette, size: 64, quiet: true), 0, 0);
        toolbar.Controls.Add(new Label
        {
            Text = currentBoard.Name,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 24F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Text),
            Padding = new Padding(this.DpiScaled(14), this.DpiScaled(3), 0, 0)
        }, 1, 0);

        var rightTools = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        toolbar.Controls.Add(rightTools, 2, 0);
        rightTools.Controls.Add(Ui.MakeSegmentedButton(vertical: false,
            Ui.MakeIconButton(
                Icons.PencilLine, "Rename",
                (_, _) => { RenameBoard(currentBoard); ShowOpenBoard(); },
                palette: palette, size: 64, quiet: true),
            Ui.MakeIconButton(
            Icons.Palette, "Palettes", (_, _) => ManagePalettes(),
            palette: palette, size: 64, quiet: true)
        ));

        // Each column gets an equal share (25%) of the board grid width.
        var boardGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = BoardColumns.All.Length,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        foreach (string _ in BoardColumns.All) boardGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        page.Controls.Add(boardGrid, 0, 1);

        for (int i = 0; i < BoardColumns.All.Length; i++)
        {
            boardGrid.Controls.Add(BuildColumn(BoardColumns.All[i]), i, 0);
        }

        ResumeLayout();
        SetRedraw(true);
    }

    /// <summary>
    /// Resolves the app-level palette from <see cref="settings"/>. If the stored key no longer
    /// exists (e.g. a custom palette was deleted), resets to the default, saves settings, and
    /// shows a one-time warning.
    /// </summary>
    private BoardPalette ResolveAppPalette()
    {
        if (!paletteStore.TryGet(settings.AppPaletteKey, out BoardPalette resolved))
        {
            settings.AppPaletteKey = PaletteLibrary.DefaultKey;
            SaveSettings();
            StyledDialog.Message(this, "Palette missing",
                "The app palette could not be found, so Garden focus was applied.", resolved);
        }
        return resolved;
    }

    /// <summary>
    /// Opens the palette browser, then applies the result. If the user closed without
    /// selecting a palette, the board keeps whatever palette it had (re-verifying it still
    /// exists). Recreates <see cref="paletteStore"/> before and after in case custom palettes
    /// were added or deleted inside the dialog.
    /// </summary>
    private void ManagePalettes()
    {
        if (currentBoard is null) return;

        paletteStore = new PaletteStore(settings.PaletteDirectory);
        PaletteDialogResult? result = PaletteManager.Show(this, paletteStore, currentBoard.Palette);
        paletteStore = new PaletteStore(settings.PaletteDirectory);

        if (result?.SelectedPaletteKey is null)
        {
            if (!paletteStore.TryGet(currentBoard.Palette, out palette))
            {
                currentBoard.Palette = PaletteLibrary.DefaultKey;
                palette = paletteStore.Default;
                SaveBoard(currentBoard);
            }
            ShowOpenBoard();
            return;
        }

        currentBoard.Palette = result.SelectedPaletteKey;
        palette = paletteStore.Get(currentBoard.Palette);
        SaveBoard(currentBoard);
        PaletteTransition.Run(this, ShowOpenBoard);
    }

    /// <summary>
    /// Opens the palette browser so the user can choose the app-level palette used for the
    /// board list and all non-board dialogs. Saves the selection to settings and refreshes
    /// the board list. Recreates <see cref="paletteStore"/> before and after in case custom
    /// palettes were created or deleted inside the dialog.
    /// </summary>
    private void ChooseAppPalette()
    {
        paletteStore = new PaletteStore(settings.PaletteDirectory);
        PaletteDialogResult? result = PaletteManager.Show(this, paletteStore, settings.AppPaletteKey);
        paletteStore = new PaletteStore(settings.PaletteDirectory);

        if (result?.SelectedPaletteKey is not null)
        {
            settings.AppPaletteKey = result.SelectedPaletteKey;
            SaveSettings();
        }
        else if (!paletteStore.TryGet(settings.AppPaletteKey, out _))
        {
            settings.AppPaletteKey = PaletteLibrary.DefaultKey;
            SaveSettings();
        }

        PaletteTransition.Run(this, ShowBoardList);
    }

    /// <summary>
    /// Builds the full UI for one Kanban column: a <see cref="RoundedPanel"/> shell with a
    /// colored top band, a header row with the column name and an Add button, and a scrollable
    /// task area (<see cref="SmoothPanel"/>).
    /// </summary>
    /// <remarks>
    /// Both the shell and the task panel set <c>AllowDrop = true</c> and subscribe to
    /// <c>DragEnter</c>/<c>DragOver</c>/<c>DragDrop</c>. WinForms requires all three events
    /// to be wired for drag-and-drop to function: DragEnter sets the effect, DragOver keeps
    /// updating the drop position, and DragDrop commits the move.
    /// The task panel also registers a <c>MouseWheel</c> handler for custom scrolling — we use
    /// manual layout via <see cref="LayoutTaskCards"/> instead of AutoScroll to avoid the
    /// default system-styled scrollbar appearing in the column.
    /// </remarks>
    private Control BuildColumn(string columnName)
    {
        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(this.DpiScaled(6)),
            Padding = new Padding(this.DpiScaled(12)),
            BackColor = Ui.ColorFrom(palette.SurfaceAlt),
            BorderColor = Ui.ColorFrom(palette.Border),
            Radius = this.DpiScaled(18),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            TopBandColor = Ui.ColorFrom(ColumnBandColor(columnName)),
            TopBandHeight = this.DpiScaled(64),
            AllowDrop = true
        };
        shell.DragEnter += (_, e) => AcceptTaskDrop(e);
        shell.DragOver += (_, e) => UpdateDropIndicatorForDrag(columnName, e);
        shell.DragDrop += (_, e) => MoveDraggedTask(e, columnName);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Ui.ColorFrom(palette.SurfaceAlt)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, this.DpiScaled(52)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(layout);

        var header = new SmoothPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Ui.ColorFrom(ColumnBandColor(columnName)),
            Margin = new Padding(0, 0, 0, 0)
        };
        layout.Controls.Add(header, 0, 0);

        header.Controls.Add(new Label
        {
            Text = columnName,
            AutoSize = false,
            Left = 0,
            Top = this.DpiScaled(7),
            Width = this.DpiScaled(170),
            Height = this.DpiScaled(34),
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.AccentText),
            BackColor = Color.Transparent,
            Padding = new Padding(this.DpiScaled(8), this.DpiScaled(5), 0, 0)
        });
        var tasks = new SmoothPanel
        {

            Dock = DockStyle.Fill,
            BackColor = Ui.ColorFrom(palette.SurfaceAlt),
            AllowDrop = true,
            TabStop = true,
            Padding = new Padding(0, this.DpiScaled(32), 0, 0)

        };
        tasks.DragEnter += (_, e) => AcceptTaskDrop(e);
        tasks.DragOver += (_, e) => UpdateDropIndicatorForDrag(columnName, e);
        tasks.DragDrop += (_, e) => MoveDraggedTask(e, columnName);
        // Focus the panel on mouse-enter so its MouseWheel event fires without needing a click.
        tasks.MouseEnter += (_, _) => tasks.Focus();
        tasks.MouseWheel += (_, e) => ScrollTaskList(tasks, e.Delta);
        tasks.Resize += (_, _) =>
        {
            LayoutTaskCards(tasks, CurrentTaskOffset(tasks));
        };
        layout.Controls.Add(tasks, 0, 1);
        taskLists[columnName] = tasks;

        // One invisible drop indicator per column. Tag "__drop_indicator" distinguishes it
        // from real task cards in TaskCards() so it is excluded from layout and task counts.
        var indicator = new DropIndicatorControl
        {
            ForeColor = Ui.ColorFrom(ColumnBandColor(columnName)),
            Visible = false,
            Tag = "__drop_indicator"
        };
        tasks.Controls.Add(indicator);
        dropIndicators[columnName] = indicator;

        if (currentBoard is not null)
        {
            FillTaskList(tasks, columnName);
        }

        return shell;
    }

    /// <summary>
    /// Builds the card control for a single task. When <paramref name="task"/> is the one
    /// currently being dragged (<see cref="draggingTaskId"/>), the card is rendered as a
    /// "ghost" — faded out, no drag handle, no action buttons — to show where it came from.
    /// </summary>
    private Control BuildTaskCard(KanbanTask task)
    {
        bool isGhost = draggingTaskId == task.Id;
        var card = new RoundedPanel
        {
            Width = this.DpiScaled(248),
            Height = this.DpiScaled(148),
            Margin = new Padding(0, 0, 0, this.DpiScaled(12)),
            Padding = new Padding(this.DpiScaled(8)),
            BackColor = isGhost
                ? Ui.Blend(Ui.ColorFrom(palette.Card), Ui.ColorFrom(palette.SurfaceAlt), 0.65f)
                : Ui.ColorFrom(palette.Card),
            BorderColor = isGhost ? Ui.ColorFrom(palette.Accent) : Ui.ColorFrom(palette.Border),
            Radius = Ui.Scaled(15),
            Shadow = !isGhost,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            Tag = task.Id
        };

        // Outer: [drag handle] | [body]
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = isGhost ? 1 : 2,
            RowCount = 1,
            BackColor = Color.Transparent
        };
        if (!isGhost) layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(layout);

        if (!isGhost)
        {
            var handle = new DragHandle { Dock = DockStyle.Fill, DotColor = Ui.ColorFrom(palette.Muted) };
            handle.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) BeginTaskDrag(handle, task);
            };
            layout.Controls.Add(handle, 0, 0);
        }

        int bodyCol = isGhost ? 0 : 1;
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, this.DpiScaled(32)));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(body, bodyCol, 0);

        // Build actions before the title so we know the width for the right margin
        SegmentedButton? actions = null;
        if (!isGhost)
        {
            int buttonSize = 32; // raw logical pixels — MakeIconButton applies Ui.Scaled() internally
            actions = Ui.MakeSegmentedButton(vertical: false,
                Ui.MakeIconButton(
                    Icons.NotePencil, "Edit task", (_, _) => EditTask(task),
                    palette: palette, size: buttonSize, iconSize: 13f, quiet: true),
                Ui.MakeIconButton(
                    Icons.Trash, "Delete task", (_, _) => DeleteTask(task),
                    palette: palette, size: buttonSize, iconSize: 13f, danger: true));
            actions.Radius = Math.Max(1, card.Radius - card.Padding.Top);
            int actionOffsetX = this.DpiScaled(7);
            actions.Location = new Point(card.Width - actionOffsetX - card.Padding.Right - actions.Width, card.Padding.Top);
            card.Resize += (_, _) =>
            {
                actions.Location = new Point(card.Width - actionOffsetX - card.Padding.Right - actions.Width, card.Padding.Top);
            };
            card.Controls.Add(actions);
            card.Controls.SetChildIndex(actions, 0);
        }

        body.Controls.Add(new Label
        {
            Text = task.Title,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            ForeColor = isGhost ? Ui.ColorFrom(palette.Muted) : Ui.ColorFrom(palette.Text),
            Margin = new Padding(0, this.DpiScaled(8), actions is not null ? actions.Width + this.DpiScaled(4) : 0, 0)
        }, 0, 0);

        var notesLabel = new Label
        {
            Text = task.Notes,
            Dock = DockStyle.Fill,
            AutoSize = false,
            MinimumSize = new Size(0, 0),
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Italic),
            ForeColor = Ui.ColorFrom(palette.Muted),
            Margin = new Padding(isGhost ? 0 : this.DpiScaled(18), this.DpiScaled(6), this.DpiScaled(4), this.DpiScaled(4))
        };
        body.Controls.Add(notesLabel, 0, 1);
        if (!isGhost) body.SetColumnSpan(notesLabel, 2);

        return card;
    }

    /// <summary>
    /// Recursively walks the control tree rooted at <paramref name="root"/> and hides every
    /// control of type <typeparamref name="T"/>. Used during drag initiation to hide the drag
    /// handle and action buttons on the source card, converting it to ghost appearance.
    /// </summary>
    private static void HideDescendantsOfType<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T) c.Visible = false;
            HideDescendantsOfType<T>(c);
        }
    }

    /// <summary>
    /// Starts a drag-and-drop operation for the given task. Sets drag state, converts the
    /// source card to ghost appearance, shows the <see cref="DragPreviewForm"/>, then calls
    /// <see cref="Control.DoDragDrop"/> which <em>blocks</em> until the drag ends.
    /// </summary>
    /// <remarks>
    /// DoDragDrop is synchronous and blocks the calling thread. Mouse events stop firing
    /// during the native drag, which is why the preview form uses its own timer to follow
    /// the cursor (via GiveFeedback and QueryContinueDrag events which do still fire).
    /// Cleanup (hiding the preview, clearing drag state, refreshing columns) happens in the
    /// lines after DoDragDrop returns.
    /// </remarks>
    private void BeginTaskDrag(Control handle, KanbanTask task)
    {
        string sourceColumn = task.Column; // capture before DoDragDrop — ReorderTask will mutate this
        draggingTaskId = task.Id;

        RoundedPanel? sourceCard = FindAncestor<RoundedPanel>(handle);
        if (sourceCard is not null)
        {
            sourceCard.BackColor = Ui.Blend(Ui.ColorFrom(palette.Card), Ui.ColorFrom(palette.SurfaceAlt), 0.65f);
            sourceCard.BorderColor = Ui.ColorFrom(palette.Accent);
            sourceCard.Shadow = false;
            HideDescendantsOfType<DragHandle>(sourceCard);
            HideDescendantsOfType<SegmentedButton>(sourceCard);
            sourceCard.Invalidate(true);
        }

        dragPreview = new DragPreviewForm(task.Title, task.Notes, palette);
        dragPreview.Begin(Cursor.Position);
        handle.GiveFeedback += DragGiveFeedback;
        handle.QueryContinueDrag += DragQueryContinue;
        handle.DoDragDrop(task.Id, DragDropEffects.Move);
        handle.GiveFeedback -= DragGiveFeedback;
        handle.QueryContinueDrag -= DragQueryContinue;
        dragPreview?.End();
        dragPreview = null;
        draggingTaskId = null;
        HideDropIndicators();

        // task.Column is now the destination (updated by ReorderTask), or unchanged if cancelled
        string destColumn = task.Column;
        RefreshColumn(sourceColumn);
        if (destColumn != sourceColumn) RefreshColumn(destColumn);
    }

    /// <summary>
    /// Walks up the parent chain from <paramref name="control"/> and returns the first
    /// ancestor of type <typeparamref name="T"/>, or null if none is found.
    /// </summary>
    private static T? FindAncestor<T>(Control control) where T : Control
    {
        Control? current = control;
        while (current is not null)
        {
            if (current is T match) return match;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// GiveFeedback fires continuously during a native drag. We suppress the default cursor
    /// change and use our own (<see cref="Cursors.SizeAll"/>), then update the floating preview.
    /// </summary>
    private void DragGiveFeedback(object? sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = false;
        Cursor.Current = Cursors.SizeAll;
        dragPreview?.MoveTo(Cursor.Position);
    }

    /// <summary>
    /// QueryContinueDrag fires when the mouse moves or a key state changes during a drag.
    /// We only use it to update the preview position; cancellation is handled by the default
    /// behavior (Escape key sets Action = Cancel automatically).
    /// </summary>
    private void DragQueryContinue(object? sender, QueryContinueDragEventArgs e)
    {
        dragPreview?.MoveTo(Cursor.Position);
    }

    private void MovePreview(DragEventArgs e) => dragPreview?.MoveTo(new Point(e.X, e.Y));

    /// <summary>
    /// Sets the drag effect to Move if the data is text (which is how we pass the task ID).
    /// This must be set in DragEnter for WinForms to allow the drop to proceed.
    /// </summary>
    private static void AcceptTaskDrop(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.Text) == true) e.Effect = DragDropEffects.Move;
    }

    /// <summary>Shows the task editor, creates a new task in the given column, and saves.</summary>
    private void AddTask(string columnName)
    {
        if (currentBoard is null) return;
        TaskDialogData? data = TaskEditor.Show("Add task", "New task", "", palette);
        if (data is null || string.IsNullOrWhiteSpace(data.Title)) return;

        currentBoard.Tasks.Add(new KanbanTask
        {
            Title = data.Title.Trim(),
            Notes = data.Notes.Trim(),
            Column = columnName
        });
        SaveBoard(currentBoard);
        RefreshColumn(columnName);
    }

    /// <summary>Shows the task editor pre-populated with existing values, then saves on OK.</summary>
    private void EditTask(KanbanTask task)
    {
        if (currentBoard is null) return;
        TaskDialogData? data = TaskEditor.Show("Edit task", task.Title, task.Notes, palette);
        if (data is null || string.IsNullOrWhiteSpace(data.Title)) return;

        task.Title = data.Title.Trim();
        task.Notes = data.Notes.Trim();
        SaveBoard(currentBoard);
        RefreshColumn(task.Column);
    }

    /// <summary>Confirms and removes a task from the board, then saves.</summary>
    private void DeleteTask(KanbanTask task)
    {
        if (currentBoard is null) return;
        if (!StyledDialog.Confirm(this, "Delete task", $"Delete \"{task.Title}\"?", palette)) return;

        string columnName = task.Column;

        currentBoard.Tasks.Remove(task);
        SaveBoard(currentBoard);
        RefreshColumn(columnName);
    }

    /// <summary>
    /// Handles the DragDrop event on a column. Calls <see cref="UpdateDropIndicatorForDrag"/>
    /// one last time to ensure <see cref="dragTargetIndex"/> is current, then delegates to
    /// <see cref="ReorderTask"/> and saves.
    /// </summary>
    private void MoveDraggedTask(DragEventArgs e, string columnName)
    {
        UpdateDropIndicatorForDrag(columnName, e);
        if (currentBoard is null || e.Data?.GetData(DataFormats.Text) is not string taskId) return;

        KanbanTask? task = currentBoard.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null) return;

        string sourceColumn = task.Column; // before ReorderTask changes it
        ReorderTask(task, columnName, dragTargetColumn == columnName ? dragTargetIndex : int.MaxValue);
        var boardSnapshot = currentBoard; // capture reference
        _ = Task.Run(() => SaveBoard(boardSnapshot));

        // Clear draggingTaskId first so RefreshColumn doesn't render a ghost card
        draggingTaskId = null;

        RefreshColumn(columnName);
        if (sourceColumn != columnName) RefreshColumn(sourceColumn);

        taskLists.TryGetValue(columnName, out SmoothPanel? list);
        if (list is null) return;

        BeginInvoke(() =>
        {
            int currentScroll = CurrentTaskOffset(list);
            ScrollToTaskIfHidden(list, task.Id, currentScroll);
        });
    }

    private void ScrollToTaskIfHidden(SmoothPanel list, string taskId, int currentScroll)
    {
        Control? card = list.Controls
            .OfType<Control>()
            .FirstOrDefault(c => c.Tag is string id && id == taskId);

        if (card is null) return;

        // card.Bottom is visual position; add currentScroll to get content-space bottom
        int contentBottom = card.Bottom + currentScroll;
        int visibleHeight = list.ClientSize.Height;

        if (contentBottom > visibleHeight + currentScroll)
        {
            int targetScroll = Math.Clamp(
                contentBottom - visibleHeight,
                0,
                GetMaxTaskOffset(list)
            );
            ScrollTaskList(list, 0, targetScroll);
        }
    }

    /// <summary>Moves a task to a new column and position within that column.</summary>
    /// <remarks>
    /// Tasks have no explicit order field — their order within a column is their position
    /// in <see cref="BoardFile.Tasks"/>. This method rebuilds the entire list in canonical
    /// column order (<see cref="BoardColumns.All"/>) after inserting the moved task at the
    /// requested index, which keeps the JSON file clean and predictable.
    /// </remarks>
    private void ReorderTask(KanbanTask task, string columnName, int index)
    {
        if (currentBoard is null) return;

        currentBoard.Tasks.Remove(task);
        task.Column = columnName;

        var ordered = new List<KanbanTask>();
        foreach (string column in BoardColumns.All)
        {
            List<KanbanTask> columnTasks = currentBoard.Tasks.Where(t => t.Column == column).ToList();
            if (column == columnName)
            {
                columnTasks.Insert(Math.Clamp(index, 0, columnTasks.Count), task);
            }
            ordered.AddRange(columnTasks);
        }

        currentBoard.Tasks = ordered;
    }

    /// <summary>
    /// Rebuilds all task columns without tearing down the column shells. Preserves each
    /// column's current scroll offset so the viewport doesn't jump after an edit.
    /// </summary>
    private void RefreshTaskColumns()
    {
        if (currentBoard is null) return;
        SetRedraw(false);
        SuspendLayout();
        foreach ((string columnName, SmoothPanel list) in taskLists)
        {
            int scrollY = CurrentTaskOffset(list);
            list.SuspendLayout();
            list.Controls.Clear();
            FillTaskList(list, columnName);
            list.ResumeLayout();
            int maxOffset = GetMaxTaskOffset(list);
            int clampedScrollY = Math.Clamp(scrollY, 0, maxOffset);
            LayoutTaskCards(list, clampedScrollY, clampedScrollY == 0 ? list.DpiScaled(8) : 0);
            _scrollStates.Remove(list);
        }
        ResumeLayout();
        SetRedraw(true);
    }

    private void RefreshColumn(string columnName)
    {
        if (currentBoard is null || !taskLists.TryGetValue(columnName, out SmoothPanel? list)) return;

        int scrollY = CurrentTaskOffset(list);
        SetRedraw(false);
        list.SuspendLayout();
        list.Controls.Clear();
        FillTaskList(list, columnName);
        list.ResumeLayout();
        int clampedScrollY = Math.Clamp(scrollY, 0, GetMaxTaskOffset(list));
        LayoutTaskCards(list, clampedScrollY, clampedScrollY == 0 ? list.DpiScaled(8) : 0);
        _scrollStates.Remove(list);
        SetRedraw(true);
    }

    /// <summary>
    /// Clears a task panel and repopulates it with cards for the given column, then
    /// re-adds the column's drop indicator and runs initial layout at offset 0.
    /// </summary>
    private void FillTaskList(SmoothPanel list, string columnName)
    {
        list.Controls.Clear();
        foreach (KanbanTask task in currentBoard!.Tasks.Where(t => t.Column == columnName))
        {
            Control card = BuildTaskCard(task);
            card.Width = Math.Max(this.DpiScaled(140), list.ClientSize.Width - this.DpiScaled(8));
            list.Controls.Add(card);
        }
        if (dropIndicators.TryGetValue(columnName, out DropIndicatorControl? indicator))
        {
            indicator.Width = Math.Max(this.DpiScaled(120), list.ClientSize.Width - this.DpiScaled(10));
            indicator.Visible = false;
            list.Controls.Add(indicator);
        }
        Control addButton = BuildAddTaskButton(columnName);
        addButton.Width = Math.Max(this.DpiScaled(140), list.ClientSize.Width - this.DpiScaled(8));
        list.Controls.Add(addButton);
        LayoutTaskCards(list, 0);
    }

    private Control BuildAddTaskButton(string columnName)
    {
        var button = new AddTaskButton(palette)
        {
            Tag = "__add_button"
        };
        button.Click += (_, _) => AddTask(columnName);
        return button;
    }

    /// <summary>
    /// Scrolls the task list by <paramref name="delta"/> pixels (positive = scroll up /
    /// reveal content below) or jumps to an absolute offset when <paramref name="absolute"/>
    /// is provided. Clamps to valid range.
    /// </summary>
    private void ScrollTaskList(SmoothPanel list, int delta, int? absolute = null)
    {
        // Use the in-flight target as the base so fast scrolling accumulates
        float existingTarget = _scrollStates.TryGetValue(list, out var s) ? s.Target : CurrentTaskOffset(list);
        float currentPos = _scrollStates.TryGetValue(list, out var s2) ? s2.Current : CurrentTaskOffset(list);

        float newTarget = absolute.HasValue
            ? absolute.Value
            : Math.Clamp(existingTarget - delta, 0, GetMaxTaskOffset(list));

        _scrollStates[list] = (currentPos, newTarget);
        if (!_scrollLerpTimer!.Enabled) _scrollLerpTimer.Start();
    }

    private void OnScrollLerpTick(object? sender, EventArgs e)
    {
        bool anyActive = false;

        foreach (var key in _scrollStates.Keys.ToList())
        {
            var (current, target) = _scrollStates[key];
            current += (target - current) * ScrollLerpFactor;

            if (Math.Abs(current - target) < ScrollStopThresh)
            {
                current = target;
                _scrollStates.Remove(key);
            }
            else
            {
                _scrollStates[key] = (current, target);
                anyActive = true;
            }

            LayoutTaskCards(key, (int)Math.Round(current));
        }

        if (!anyActive) _scrollLerpTimer!.Stop();
    }

    /// <summary>
    /// Returns the maximum valid scroll offset — the total content height minus the visible
    /// area, so the last card's bottom edge is flush with the panel bottom.
    /// </summary>
    private static int GetMaxTaskOffset(SmoothPanel list) =>
    GetMaxTaskOffset(TaskCards(list).ToList(), list.ClientSize.Height);

    private static int GetMaxTaskOffset(IReadOnlyList<Control> cards, int clientHeight)
    {
        if (cards.Count == 0) return 0;
        int contentHeight = cards.Sum(c => c.Height + c.Margin.Vertical);
        int bottomPad = cards[0].DpiScaled(8);
        return Math.Max(0, contentHeight - clientHeight + bottomPad);
    }

    /// <summary>
    /// Reads the current scroll offset from the first card's Top position.
    /// A negative Top means the list is scrolled down by that many pixels.
    /// </summary>
    private static int CurrentTaskOffset(SmoothPanel list)
    {
        Control? first = TaskCards(list).OrderBy(c => c.Top).FirstOrDefault();
        return first is null ? 0 : Math.Max(0, -first.Top);
    }

    /// <summary>
    /// Manually positions all task cards top-to-bottom inside the panel at the given scroll
    /// offset. We use manual layout instead of AutoScroll so no system scrollbar appears.
    /// Cards are sized to fill the panel width. SuspendLayout / ResumeLayout suppresses
    /// intermediate layout passes that would cause flicker.
    /// </summary>
    private static void LayoutTaskCards(SmoothPanel list, int offset, int top_padding = -1)
    {
        if (top_padding < 0) top_padding = list.DpiScaled(8);
        List<Control> cards = TaskCards(list).ToList();
        int next = Math.Clamp(offset, 0, GetMaxTaskOffset(cards, list.ClientSize.Height));

        list.SuspendLayout();
        int y = -next + top_padding;
        foreach (Control control in cards)
        {
            control.Width = Math.Max(list.DpiScaled(140), list.ClientSize.Width - list.DpiScaled(8));
            control.Left = control.Margin.Left;
            y += control.Margin.Top;
            control.Top = y;
            y += control.Height + control.Margin.Bottom;
        }
        list.ResumeLayout();
    }

    /// <summary>
    /// Updates the drop indicator position as the user drags over a column. Also calls
    /// <see cref="AcceptTaskDrop"/> to keep the effect set to Move, and <see cref="MovePreview"/>
    /// to keep the floating card preview in sync.
    /// </summary>
    /// <remarks>
    /// The insertion index is determined by which cards the cursor's Y coordinate is past
    /// their midpoint. The drop indicator is a thin colored strip painted at the insertion point.
    /// </remarks>
    private void UpdateDropIndicatorForDrag(string columnName, DragEventArgs e)
    {
        MovePreview(e);
        AcceptTaskDrop(e);
        if (!taskLists.TryGetValue(columnName, out SmoothPanel? list)
            || !dropIndicators.TryGetValue(columnName, out DropIndicatorControl? indicator))
        {
            return;
        }

        Point local = list.PointToClient(new Point(e.X, e.Y));
        // Exclude the ghost card (the source card being dragged) from position calculations.
        List<Control> cards = TaskCards(list)
            .Where(c => c.Tag as string != draggingTaskId)
            .OrderBy(c => c.Top)
            .ToList();

        int index = 0;
        foreach (Control card in cards)
        {
            if (local.Y > card.Top + (card.Height / 2))
            {
                index++;
            }
        }

        int top = cards.Count == 0
            ? list.DpiScaled(8)
            : index >= cards.Count
                ? cards[^1].Bottom + list.DpiScaled(4)
                : Math.Max(list.DpiScaled(4), cards[index].Top - list.DpiScaled(8));

        HideDropIndicators(columnName);
        indicator.Left = list.DpiScaled(4);
        indicator.Top = top - indicator.Height / 2; // centre it on the gap, not sit below it
        indicator.Width = Math.Max(list.DpiScaled(120), list.ClientSize.Width - list.DpiScaled(10));
        indicator.Visible = true;
        indicator.BringToFront();

        dragTargetColumn = columnName;
        dragTargetIndex = index;
    }

    /// <summary>
    /// Hides all drop indicators except the one for <paramref name="exceptColumn"/>.
    /// When called with no argument (or null), also resets the drag target state.
    /// </summary>
    private void HideDropIndicators(string? exceptColumn = null)
    {
        foreach ((string column, DropIndicatorControl indicator) in dropIndicators)
        {
            if (column != exceptColumn)
            {
                indicator.Visible = false;
            }
        }
        if (exceptColumn is null)
        {
            dragTargetColumn = null;
            dragTargetIndex = 0;
        }
    }

    /// <summary>
    /// Returns the real task card controls inside a task panel, excluding the drop indicator
    /// (which is tagged "__drop_indicator"). Used wherever we need to iterate or measure only
    /// actual task cards.
    /// </summary>
    private static IEnumerable<Control> TaskCards(SmoothPanel list)
    {
        return list.Controls.Cast<Control>().Where(c => c.Tag as string != "__drop_indicator"); //&& c.Tag as string != "__add_button");
    }

    /// <summary>Returns the accent band hex color for a given column name.</summary>
    private string ColumnBandColor(string columnName) => columnName switch
    {
        BoardColumns.Todo => palette.Todo,
        BoardColumns.InProgress => palette.InProgress,
        BoardColumns.Testing => palette.Testing,
        BoardColumns.Complete => palette.Complete,
        _ => palette.Accent
    };

    /// <summary>Serializes the board to its .knbn file at <see cref="BoardFile.FilePath"/>.</summary>
    private void SaveBoard(BoardFile board)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(board.FilePath)!);
        File.WriteAllText(board.FilePath, JsonSerializer.Serialize(board, jsonOptions));
    }

}

/// <summary>Return value from <see cref="TaskEditor.Show"/> carrying the two editable task fields.</summary>
public sealed record TaskDialogData(string Title, string Notes);

/// <summary>Single-input text dialog. Used for board names and rename operations.</summary>
/// <remarks>
/// All modal dialogs in this app use the same borderless pattern:
/// <c>FormBorderStyle.None</c> with <c>BackColor = Color.Magenta</c> and
/// <c>TransparencyKey = Color.Magenta</c>. Magenta pixels are rendered fully transparent
/// by Windows, so only the painted <see cref="RoundedPanel"/> shell is visible.
/// The × button carries <c>DialogResult.Cancel</c> so pressing it (or Escape via
/// <see cref="Form.CancelButton"/>) closes the dialog cleanly.
/// </remarks>
public static class Prompt
{
    /// <param name="title">Dialog heading and window title.</param>
    /// <param name="label">Label shown above the text input.</param>
    /// <param name="initialValue">Pre-filled value for the input (selected on open).</param>
    /// <param name="palette">Color palette for theming. Defaults to the first preset.</param>
    /// <returns>The entered text, or null if the dialog was cancelled.</returns>
    public static string? Show(string title, string label, string initialValue, BoardPalette? palette = null)
    {
        palette ??= PaletteLibrary.Presets[0];
        using var form = new Form
        {
            Text = title,
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Width = Ui.Scaled(450),
            Height = Ui.Scaled(210),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Ui.ColorFrom(palette.Border),
            Font = new Font("Segoe UI", 10F)
        };
        form.AutoScaleMode = AutoScaleMode.None;

        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(Ui.Scaled(14)),
            Padding = new Padding(Ui.Scaled(18)),
            Radius = Ui.Scaled(18),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            BorderColor = Ui.ColorFrom(palette.Border),
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        form.Controls.Add(shell);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Ui.Scaled(4)),
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.Controls.Add(layout);

        var close = Ui.MakeCloseButton(palette, new Point(form.Width - Ui.Scaled(68), Ui.Scaled(12)));
        shell.Controls.Add(close);
        close.BringToFront();

        layout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Text),
            BackColor = Ui.ColorFrom(palette.Surface),
            Margin = new Padding(0, 0, 0, Ui.Scaled(12))
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Ui.ColorFrom(palette.Text),
            BackColor = Ui.ColorFrom(palette.Surface)
        }, 0, 1);
        var input = new TextBox
        {
            Text = initialValue,
            Dock = DockStyle.Top,
            Margin = new Padding(0, Ui.Scaled(6), 0, Ui.Scaled(14)),
            BackColor = Ui.ColorFrom(palette.Card),
            ForeColor = Ui.ColorFrom(palette.Text),
            BorderStyle = BorderStyle.FixedSingle
        };
        layout.Controls.Add(input, 0, 2);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        var ok = new ModernButton
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = Ui.Scaled(90),
            BackColor = Ui.ColorFrom(palette.Accent),
            ForeColor = Ui.ColorFrom(palette.AccentText),
            Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
        };
        var cancel = new ModernButton
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = Ui.Scaled(90),
            BackColor = Ui.ColorFrom(palette.SurfaceAlt),
            ForeColor = Ui.ColorFrom(palette.Text),
            BorderColor = Ui.ColorFrom(palette.Border),
            Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
        };
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 3);

        form.AcceptButton = ok;
        form.CancelButton = cancel;
        input.SelectAll();

        Ui.SetRoundedRegion(form, shell.Radius, true);

        return form.ShowDialog() == DialogResult.OK ? input.Text : null;
    }
}

/// <summary>
/// Themed message and confirmation dialogs. Uses the same borderless form pattern as
/// <see cref="Prompt"/> — see that class for a description of the TransparencyKey trick.
/// Confirm dialogs show "Yes" (danger-styled) + "Cancel"; message dialogs show just "OK".
/// </summary>
public static class StyledDialog
{
    /// <summary>Shows a dismissible message with a single OK button.</summary>
    public static void Message(IWin32Window owner, string title, string message, BoardPalette palette)
    {
        Show(owner, title, message, palette, confirm: false);
    }

    /// <summary>Shows a confirmation dialog. Returns true if the user clicked Yes.</summary>
    public static bool Confirm(IWin32Window owner, string title, string message, BoardPalette palette)
    {
        return Show(owner, title, message, palette, confirm: true) == DialogResult.OK;
    }

    private static DialogResult Show(
        IWin32Window owner, string title, string message, BoardPalette palette, bool confirm)
    {
        using var form = new Form
        {
            Text = title,
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Width = Ui.Scaled(460),
            Height = Ui.Scaled(230),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None,
            BackColor = Ui.ColorFrom(palette.Surface),
            Font = new Font("Segoe UI", 10F)
        };
        form.AutoScaleMode = AutoScaleMode.None;

        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(Ui.Scaled(14)),
            Padding = new Padding(Ui.Scaled(18)),
            Radius = Ui.Scaled(18),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            BorderColor = Ui.ColorFrom(palette.Border),
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        form.Controls.Add(shell);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(layout);

        var close = Ui.MakeCloseButton(palette, new Point(form.Width - Ui.Scaled(68), Ui.Scaled(12)));
        shell.Controls.Add(close);
        close.BringToFront();

        layout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Text),
            BackColor = Ui.ColorFrom(palette.Surface),
            Margin = new Padding(0, 0, 0, Ui.Scaled(10))
        }, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            ForeColor = Ui.ColorFrom(palette.Muted),
            BackColor = Ui.ColorFrom(palette.Surface),
            Padding = new Padding(0, Ui.Scaled(4), 0, 0)
        }, 0, 1);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        var ok = new ModernButton
        {
            Text = confirm ? "Yes" : "OK",
            DialogResult = DialogResult.OK,
            Width = Ui.Scaled(90),
            BackColor = confirm ? Ui.ColorFrom(palette.Danger) : Ui.ColorFrom(palette.Accent),
            ForeColor = Ui.ColorFrom(palette.AccentText),
            Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
        };
        actions.Controls.Add(ok);
        if (confirm)
        {
            actions.Controls.Add(new ModernButton
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Width = Ui.Scaled(96),
                BackColor = Ui.ColorFrom(palette.SurfaceAlt),
                ForeColor = Ui.ColorFrom(palette.Text),
                BorderColor = Ui.ColorFrom(palette.Border),
                Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
            });
        }
        layout.Controls.Add(actions, 0, 2);

        form.AcceptButton = ok;
        form.CancelButton = confirm
            ? actions.Controls.OfType<Button>().FirstOrDefault(b => b.DialogResult == DialogResult.Cancel)
            : ok;

        Ui.SetRoundedRegion(form, shell.Radius, true);

        return form.ShowDialog(owner);
    }
}

/// <summary>Two-field dialog for editing a task's title and notes.</summary>
/// <remarks>
/// Uses the same borderless form pattern as <see cref="Prompt"/> — see that class
/// for a description of the TransparencyKey trick.
/// Returns null if the dialog is cancelled or if the title is blank.
/// </remarks>
public static class TaskEditor
{
    /// <param name="title">Dialog heading text.</param>
    /// <param name="initialTitle">Pre-filled task title (selected on open).</param>
    /// <param name="initialNotes">Pre-filled notes text.</param>
    /// <param name="palette">Color palette for theming.</param>
    /// <returns>The entered title and notes, or null if cancelled.</returns>
    public static TaskDialogData? Show(string title, string initialTitle, string initialNotes, BoardPalette palette)
    {
        using var form = new Form
        {
            Text = title,
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Width = Ui.Scaled(540),
            Height = Ui.Scaled(390),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Ui.ColorFrom(palette.Border),
            Font = new Font("Segoe UI", 10F)
        };
        form.AutoScaleMode = AutoScaleMode.None;

        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(Ui.Scaled(16)),
            Padding = new Padding(Ui.Scaled(18)),
            Radius = Ui.Scaled(18),
            Shadow = true,
            ShadowColor = Ui.ColorFrom(palette.Shadow),
            BorderColor = Ui.ColorFrom(palette.Border),
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        form.Controls.Add(shell);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(Ui.Scaled(4)),
            RowCount = 7,
            ColumnCount = 1,
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.Scaled(8)));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(layout);

        var close = Ui.MakeCloseButton(palette, new Point(form.Width - Ui.Scaled(72), Ui.Scaled(14)));
        shell.Controls.Add(close);
        close.BringToFront();

        layout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Ui.ColorFrom(palette.Text),
            BackColor = Ui.ColorFrom(palette.Surface),
            Margin = new Padding(0, 0, 0, Ui.Scaled(14))
        }, 0, 0);

        layout.Controls.Add(new Label
        {
            Text = "Task title",
            AutoSize = true,
            ForeColor = Ui.ColorFrom(palette.Text),
            BackColor = Ui.ColorFrom(palette.Surface)
        }, 0, 1);
        var titleBox = new TextBox
        {
            Text = initialTitle,
            Dock = DockStyle.Top,
            Margin = new Padding(0, Ui.Scaled(6), 0, Ui.Scaled(14)),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Ui.ColorFrom(palette.Card),
            ForeColor = Ui.ColorFrom(palette.Text)
        };
        layout.Controls.Add(titleBox, 0, 2);

        layout.Controls.Add(new Label
        {
            Text = "Notes",
            AutoSize = true,
            ForeColor = Ui.ColorFrom(palette.Text),
            BackColor = Ui.ColorFrom(palette.Surface)
        }, 0, 3);
        var notesBox = new TextBox
        {
            Text = initialNotes,
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Margin = new Padding(0, Ui.Scaled(6), 0, Ui.Scaled(12)),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Ui.ColorFrom(palette.Card),
            ForeColor = Ui.ColorFrom(palette.Text),
            Font = new Font("Segoe UI", 10F, FontStyle.Italic)
        };
        layout.Controls.Add(notesBox, 0, 4);

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = Ui.ColorFrom(palette.Surface)
        };
        var ok = new ModernButton
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = Ui.Scaled(96),
            BackColor = Ui.ColorFrom(palette.Accent),
            ForeColor = Ui.ColorFrom(palette.AccentText),
            Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
        };
        var cancel = new ModernButton
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Width = Ui.Scaled(96),
            BackColor = Ui.ColorFrom(palette.SurfaceAlt),
            ForeColor = Ui.ColorFrom(palette.Text),
            BorderColor = Ui.ColorFrom(palette.Border),
            Margin = new Padding(Ui.Scaled(8), 0, 0, 0)
        };
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 6);

        form.AcceptButton = ok;
        form.CancelButton = cancel;
        titleBox.SelectAll();

        Ui.SetRoundedRegion(form, shell.Radius, true);

        return form.ShowDialog() == DialogResult.OK ? new TaskDialogData(titleBox.Text, notesBox.Text) : null;
    }
}
