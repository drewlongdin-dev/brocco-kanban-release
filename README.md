# BroccoKanban

A lightweight Kanban board app for Windows, built with .NET 8 and WinForms. Designed with a focus on reducing cognitive load — clean visuals, minimal noise, and a calm interface that gets out of your way.

---

## Features

- **Four-column workflow** — Todo, In Progress, Testing, Complete
- **Multiple colour palettes** — Garden, Dawn, Ink, Night, Orchard, and Studio themes
- **Portable board files** — Boards are saved as `.knbn` files (plain JSON) that you can move, back up, or share freely
- **Drag and drop** — Reorder and move tasks between columns fluidly
- **Task notes** — Each task supports an optional notes field for extra context
- **Neurodivergent-friendly design** — Low visual clutter, consistent layout, and a reduced-stimulation aesthetic

---

## Requirements

- Windows 10 or later
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

---

## Installation

> Releases are coming soon. Check back here or watch the repo for updates.

<!-- Once available:
1. Go to the [Releases](../../releases) page
2. Download the latest `.zip`
3. Extract and run `BroccoKanban.exe` — no installer needed
-->

---

## Board files

Boards are stored as `.knbn` files — a thin JSON format specific to BroccoKanban. You can keep them anywhere on your machine. There is no cloud sync or account required.

If you want to generate a board programmatically or pre-populate one with tasks, the format is straightforward:

```json
{
  "Id": "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4",
  "Name": "My Board",
  "Favourite": false,
  "Palette": "garden",
  "Tasks": [
    {
      "Id": "11111111111111111111111111111111",
      "Title": "Example task",
      "Notes": "Optional detail here",
      "Column": "Todo"
    }
  ]
}
```

Valid column values: `"Todo"`, `"In Progress"`, `"Testing"`, `"Complete"`

Valid palette values: `"garden"`, `"dawn"`, `"ink"`, `"latte"`, `"mocha"`, `"americano"` `"night"`, `"orchard"`, `"studio"`, `"trappist1"`, `"argon"`, `"titanium"`, `"banana"`, `"redblack"`, `"neon80s"`, `"cherry"`, `"rainforest"`

In this repo you can find a [.skill file](brocco-kanban.skill) compatible with Claude to generate Kanban boards with prompts.

---

## Licence

MIT — see [LICENSE](LICENSE) for details.
