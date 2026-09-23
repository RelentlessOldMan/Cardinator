# Developing Cardinator

Notes for building, testing, and understanding the code.

---

## Prerequisites

- **.NET 8 SDK** (Windows). The app is WPF, so it builds and runs on Windows only.

## Build & run

```powershell
# run the app
dotnet run --project src/Cardinator

# run the test suite
dotnet test

# build the single distributable exe
dotnet publish src/Cardinator/Cardinator.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish
# -> publish/Cardinator.exe  (no install / runtime needed on the target PC)
```

## Verifying without the GUI

Rendering runs headlessly, which makes it easy to check changes on a remote/CI machine without
opening a window:

```powershell
dotnet build src/Cardinator/Cardinator.csproj -c Debug
$exe = "src/Cardinator/bin/Debug/net8.0-windows/win-x64/Cardinator.exe"
& $exe --selftest out                          # render sample cards
& $exe --render examples\card.json card.png     # render a specific card
```

See [CLI.md](CLI.md) for all headless modes.

---

## Project layout

```
src/Cardinator/
  App.xaml(.cs)            App entry point + headless CLI mode dispatch (--help, --render, …)
  Theme.xaml               Shared styles/palette (merged by App.xaml; loadable standalone in tests)
  MainWindow.xaml(.cs)     Main UI: card list, editor, live preview, batch actions
  DetailsWindow.xaml(.cs)  Modal editor for the less-common fields
  HelpWindow.xaml(.cs)     Built-in cheat sheet (Help button / F1)
  InputDialog.xaml(.cs)    Small themed text prompt (used by Scryfall search)
  Models/
    CardModel.cs           One card's editable content (+ JSON load/save, Clone)
    CardProject.cs         A saved project (.cardinator) = a list of cards
    TemplateSpec.cs        Template layout: regions, fonts, colors (+ safe parsing)
  Services/
    ScryfallClient.cs      Fuzzy card lookup (User-Agent, rate limit, timeout handling)
    ScryfallMapper.cs      Maps Scryfall JSON -> CardModel(s), layout-aware (DFC/adventure/split)
    SymbolService.cs       Mana-symbol SVGs from Scryfall, cached; procedural pip fallback
    CardRenderer.cs        The compositor: art -> frame -> title/mana -> type -> rules -> P/T -> footer
    CardExporter.cs        BitmapSource -> PNG/JPEG file (+ DPI stamping)
    PrintExporter.cs       Adds print bleed margins (edge-extended)
    BackRenderer.cs        Renders the decorative card back
    TemplateService.cs     Discover/load templates; self-heal + guarantee built-ins exist
    BuiltInTemplates.cs    The starter template specs
    FrameGenerator.cs      Generates frame.png from a TemplateSpec
    ImportService.cs       Parse name lists / CSV / TSV into cards
    ArtMatcher.cs          Attach a folder of images to cards by filename
    BatchService.cs        Fill-from-Scryfall + render-all over many cards
    SheetExporter.cs       Compose printable 3x3 sheet pages
    ManaText.cs            Tokenize {..} symbols; normalize loose mana costs
    SampleCards.cs         Built-in sample/blank cards (offline start)
    AppPaths.cs            Resolves the CardinatorData folder (portable, %APPDATA% fallback)
    SelfTest.cs            Headless entry points (render/lookup/batch/sheet/search/newtemplate/cardback)
    TemplateImporter.cs    Create a custom template from a frame image (file/URL)
    TextUtil.cs            Shared slug / safe-filename helpers
tests/Cardinator.Tests/    xUnit tests (137), incl. golden-image regression baselines
docs/images, golden/       Rendered gallery + committed golden baselines
docs/                      This documentation + rendered gallery images
examples/                  Sample CSV to import
```

## How a card is rendered

`CardRenderer.RenderToBitmap` draws into a `DrawingVisual`, then rasterizes with
`RenderTargetBitmap`. The compositing order (`Draw`) is the key idea:

1. **Art** — drawn first, cover-fit and clipped to the `artWindow` region (with pan/zoom). For a
   `fullArt` template the region is the whole card, so the art bleeds edge to edge.
2. **Frame** — `template.FrameImage` drawn over the whole card; its transparent window lets the art
   show through. Full-art frames are just a thin border, and legibility scrims + per-font `shadow`
   outlines keep the text readable directly on the art.
3. **Title + mana**, **type line + rarity pip**, then the **body** — which branches by card type:
   planeswalker / saga / class (badged rows), adventure (sub-box), or a normal rules+flavor box.
4. **Power/Toughness** (or **loyalty**), then the **footer** (collector / set / artist).

The live preview renders at `supersample: 1`; export uses `supersample: 2` for print quality.
Rendering must happen on an **STA thread** (that's why the batch/export paths hop threads, and why
the render tests use an STA helper).

## Testing approach

The suite (`dotnet test`, **137 tests**) covers:

- **Pure logic** — mana tokenizing/normalizing, CSV/TSV import, Scryfall JSON mapping, art
  matching, footer/badge/ability parsing, image-intake path helpers.
- **Rendering** (on STA threads) — sample cards, every special layout, supersample scaling,
  determinism (same card → identical bytes), and robustness (empty cards, very long/Unicode text,
  hybrid/Phyrexian mana, **degenerate templates** with bad fonts/sizes).
- **The core goal** (`ArtCompositingTests`) — loads a real image as art and inspects pixels to
  prove the artwork fills the window with the frame/text/symbols composited over the top, stays
  clipped when zoomed, and doesn't crash on a missing art file.
- **Hardening** — malformed projects/templates, empty/duplicate template sets, invalid colors.
- **Windows** — the dialog windows (`HelpWindow`, `DetailsWindow`) are constructed on an STA
  thread with the real `Theme.xaml` loaded, to catch runtime XAML/resource errors the compiler
  misses (these can't be clicked in a headless environment).

## Robustness notes

The app is built to never crash on bad input:

- Templates self-heal (corrupt built-in spec/frame is rewritten/regenerated); the loader always
  returns at least the built-ins.
- Invalid colors → black; blank/unknown font → default font; zero/negative font size → clamped.
- Network calls catch connection errors **and timeouts**, surfacing a friendly message.
- Clipboard writes retry (Windows clipboard can be transiently locked); all `async void` UI
  handlers wrap their work so an error becomes a status message, not a crash.
- Missing/corrupt art renders an empty window instead of failing.

## Adding a template

Templates are data, not code — see [TEMPLATES.md](TEMPLATES.md). Built-in starter specs live in
`BuiltInTemplates.cs`.
