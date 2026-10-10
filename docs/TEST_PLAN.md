# Cardinator — User Workflow Test Plan & UX Review

*A living document: part manual-test checklist, part usability/logic review. It walks the realistic ways
people use the editor — from a single ad-hoc card up to building and managing a whole custom set — and
flags friction, logic gaps, and open design questions. Reflects the app as of the editor-UX pass
(name/search split, per-set folders, Save-as-new, Delete frame, paste-from-browser, live art controls).*

Legend: ✅ smooth / sound · ⚠️ friction or open question · 🐞 likely bug to verify.

---

## Part 1 — High-level workflows: building & managing a custom set

The scenario matrix (Part 2) tests individual actions. This part steps back to the **journeys** a set
creator actually lives in, and reviews whether the pieces hang together.

### W1 — Conceive a set (identity)
A custom set has an identity that spans every card: a **set code** (e.g. `FMA`), a **set symbol**, a
**collector-number scheme** (`001/N …`), a **rarity mix**, an **artist/copyright line**, and a **frame
language** — one shared frame, or a few frames by faction/color.

- Today these live as **per-card fields** plus **project defaults**; there is no single "set manifest"
  object. **Set fields on all…** is how you stamp the shared identity across the set.
- ✅ *Resolved in 1.1.1: **Set defaults…** (`SetProfile`, saved in the project) — the note below is the original finding.*
- ⚠️ **Gap:** there's no set-level home for "this set's code/symbol/numbering/frame." It's reconstructed
  by applying fields to all cards. Re-opening a set and remembering "what was the house style?" relies on
  inspecting a card. A lightweight **set-profile** (saved in the project file) would make identity
  first-class and let new cards inherit it automatically.

### W2 — Build the set (get cards in)
Entry paths, often mixed within one set:
- **Blank card → fill by hand** (original designs).
- **Recreate real cards** (name + Ctrl+L, or details → Fill from Scryfall).
- **Import file…** (names, or name+art+frame columns).
- **Scryfall search import** (e.g. `t:dragon c:r`) to seed a themed batch with real art.

Review: ✅ The multiple on-ramps are a strength. ⚠️ Mixing them means **inconsistent state** mid-build
(some cards framed, some not; some numbered, some not). The consistency pass (W4) is what reconciles it —
so the intended rhythm is **build loosely → normalize in a pass**, which should be made obvious in docs.

### W3 — Art direction across a set
- Per-card art (Change art / paste / drag), or **Add art from folder…** to bind a folder of images by
  filename in one shot.
- On **Save**, all art is copied into the set's `art/` folder and stored as **relative paths**, so the
  set is self-contained and portable.

Review: ✅ The `art/` folder + relative paths is the right backbone for a set. ⚠️ No set-level view of
"which cards still lack art" beyond clicking through / the CHECKS panel per card. A **set dashboard**
(cards missing art / missing stats / unnumbered) would make a big set manageable.

### W4 — Consistency / normalization pass
The step that turns a pile of cards into a *set*:
- **Set fields on all…** — stamp set code, artist, rarity, copyright, **frame**, set-symbol image
  (blank = leave each card's own).
- **Number cards** — assign `001/N …` in list order.
- **CHECKS** — per-card validation (missing art, bad symbol, overlap, **duplicate collector numbers**).

Review: ✅ These three are exactly the right tools. (✅ *Whole-set report since added: **Check all cards** / `SetValidator`.*) ⚠️ CHECKS is **per selected card**; there's no
**"validate the whole set"** report (e.g. list every card with an issue). For a 100-card set that's the
difference between confidence and clicking through 100 cards. 🐞 Verify **Number cards** recomputes `/N`
correctly after adds/deletes (the denominator must track the current count).

### W5 — Frame language & sharing the look
- One shared frame: design it (Design… → Save as new… to keep a pristine original), then **Set fields on
  all…** to apply it to every card.
- Per-faction frames: give each group its own frame; a set can freely mix frames per card.
- Collaboration: **Export…** each custom frame as a **`.cardframe`** bundle so a collaborator imports the
  identical frame (with tuned regions/fonts/colors).

Review: ⚠️ **The biggest set-management seam:** frames live **globally** in `CardinatorData/templates`,
but art/output live **inside the set folder**. So a set's *visual identity is split across two places*. If
you zip and send a set folder, **the custom frames do not travel with it** — the recipient opens cards
that reference a frame they don't have. Today the workaround is "also send the `.cardframe` bundles."
Options worth considering: (a) **bundle the set's custom frames into the set folder** on save (e.g.
`frames/`), and load set-local frames when a project is open; or (b) a one-click **"Export set + frames"**
that zips the folder plus every referenced custom frame. This is the most important high-level gap.

### W6 — Iterate as the set evolves
Add/edit/reorder/duplicate cards; re-frame; re-number; tweak rules for balance — all undoable across the
whole project (Ctrl+Z/Y).
Review: ✅ Project-wide undo is the right granularity. ⚠️ Re-numbering is manual (**Number cards** again
after reordering/adding); easy to forget. Consider auto-renumber-on-reorder as an option, or a CHECK that
flags "numbering is stale" (gaps/dupes/denominator mismatch).

### W7 — Output & delivery
- **Export all…** → one PNG per card. **Print sheet…** → 3×3 real-size pages (with optional **backs** for
  double-sided playtest printing).
- Share the **self-contained set folder** (art travels; frames don't — see W5).

Review: ✅ Playtest-print path is solid. 🐞 Verify **Export all** defaults to the set's `out/` folder like
single Export/Print sheet now do (it may still use the legacy output dir).

### W8 — Multiple sets & long-term management
One machine, several sets over time.
Review: ⚠️ A **set = the folder you chose at Save**; nothing enforces **one project per folder**, so two
projects saved into the same parent share `art/` and `out/`. ⚠️ No **recent-sets** list / set browser —
re-opening means navigating to the `.cardinator` file. ⚠️ No **"new set from existing"** (clone a set's
identity/frames as a starting point). A light **set-manager** (recent sets, "new set" that makes a clean
folder, "duplicate set") would round out the lifecycle.

### High-level set gaps, ranked
1. **Frames don't travel with a set** (W5) — biggest portability/collaboration hole.
2. **No whole-set validation report** (W4) — scaling pain on big sets.
3. **No set profile / identity object** (W1) — new cards don't inherit the house style.
4. **One-project-per-folder not enforced** (W8) — shared `art/`/`out/` mixing.
5. **Numbering can go stale** after edits (W6) — no guard.
6. **No set dashboard / recent-sets / new-from-existing** (W3/W8) — navigation & overview.

---

## Part 2 — Scenario matrix (action-level)

### A. Single-card journeys
- **A1 — Ad-hoc original card, zero setup.** Name → frame → art → details → Export. ✅ No project needed;
  export → global output dir. Set folder only appears on Save.
- **A2 — Recreate a real card (name = real card).** Name "Lightning Bolt" → Ctrl+L (or details → Fill
  from Scryfall) → art auto-fills → export. ⚠️ **Two lookup paths** now exist (see F-list #1).
- **A3 — Custom name, borrowed stats ("Boogie Woogie").** Name → art → Edit details… → Fill from Scryfall
  "Lightning Bolt". ✅ Fills details; status confirms name/art/frame unchanged.
- **A4 — Fully offline original card.** ✅ Works; symbols fall back to drawn pips.

### B. Art handling
- **B1 Load / B2 Paste image·file·URL / B3 Paste from browser / B4 Drag-drop.** ✅ B3 (browser→black) now
  fixed; all converge on copy-into-cache/set.
- **B5 Position: drag-pan, scroll-zoom, Ctrl=fine, click→arrows nudge, +/- zoom.** ⚠️ arrows/± need a
  preview **click (focus)** first — ✅ (1.6.7) a hint under the preview says so and changes while the keys are on.
  ✅ (1.6.7) **Reset** button (and **0** on the preview) puts the art back centred at its original size, undoable.
- **B6 Replace existing art.** ✅ SetArt resets pan/zoom — confirm that's desired vs. keeping framing.

### C. Frames
- **C1 Pick (per-card) / C2 Design+Apply / C3 Design→Save as new / C4 Edit built-in (guarded) / C5 Import
  PNG·.cardframe·.zip·URL / C6 Export bundle / C7 Delete (user only).**
- ✅ C3/C4/C7 close this pass's gaps (save-as-new doesn't mutate original; delete protects built-ins).
- ✅ *Fixed in 1.6.8 (multi-frame zip import).* ⚠️ C5: a **user-made zip with multiple template folders imports only the first** (one template per
  file). Fine for shipped bundles; note it.

### D. Sets / batch
- **D1 Import file / D2 Scryfall search import / D3 Add art from folder / D4 Fill blanks from Scryfall / D5 Number
  cards / D6 Set fields on all / D7 reorder·duplicate / D8 Export all·Print sheet (+backs).**
- ✅ Mixed frames + bulk re-frame both work; D6 "blank = unchanged" is right.
- ✅ *Fixed in 1.6.9.* 🐞 **D8 Export all** — confirm it defaults to the set's `out/` (Export PNG & Print sheet do now).

### E. Project / set-folder lifecycle
- **E1 First Save → set folder** (project + `art/` + `out/`, art copied, relative paths). **E2 Move/zip,
  reopen elsewhere** → art resolves. **E3 Re-save** → no duplicate art copies. **E4 Open old single-file
  project** → absolute paths still load.
- ✅ Round-trip covered by unit tests. ⚠️ Two projects in one parent folder **share `art/` and `out/`**.

### F. Export / share
- **F1 Export PNG/JPEG / F2 Copy image / F3 Open output folder / F4 bleed·DPI (CLI) / F5 card back.**
- ✅ F3 now follows last export / set `out/`.

### G. Error / edge / resilience
- **G1 Missing art file** → dark backing + CHECKS "art-blank" (detection restored this pass).
- **G2 Offline Scryfall** → graceful. **G3 Bad/HTML image URL** → rejected. **G4 Empty project / delete
  last card.** **G5 Unsaved-changes guard** on New/Open/close.
- ✅ *Fixed in 1.6.8.* ⚠️ **G6 Double-faced card via details search fills only the FRONT** (the main-page Ctrl+L path adds the
  back as a second card; details search does not). Decide if that asymmetry is intended.

---

## Part 3 — Top friction / open questions (action-level, ranked)
1. ✅ **Two lookup paths (A2/G6) (done, 1.6.8):** both bring the back face / other half / melded card. Ctrl+L looks up the card by its own name and takes its name and art; the details search borrows another card's text and keeps your name and art, with an **Also use its art** tick box (remembered while the app runs; Cancel undoes it) for when you want its art too (`DetailsWindow.UseScryfallArtAsync`; window test).
2. ✅ **Export-all output dir (D8) (done, 1.6.9):** it defaults to the open set's `out/` (or a folder you exported to while working on this set). Fixed: after opening ANOTHER set it used to offer the previous set's folder; switching set folders now forgets it (`MainWindow.SetProjectFolder`; window test).
3. ✅ **Keyboard art control discoverability (B5) (done, 1.6.7):** a hint line under the preview lists the keys, and switches to "Keys on…" while the preview has focus.
4. ✅ **Multi-template zip import (C5) (done, 1.6.8):** `TemplateImporter.ImportBundles` imports every folder with a template.json + frame.png (each with its own flip/landscape layouts) and .cardframe files zipped inside; a "Share set + frames" zip imports all its frames. 2 tests.
5. ✅ **Reset-art-position control (B5/B6) (done, 1.6.7):** **Reset** next to Clear, and **0** on the focused preview (`MainWindow.ResetArt`); undoable; window test.
6. ✅ **Replace-art resets framing (B6) (decided, 1.6.9):** intended. A new picture usually has different proportions, so it starts centred at its original size; **Ctrl+Z** brings back the old picture where it was, and **Reset** does the same for pan/zoom (window test).

*(The higher-impact, set-level gaps are ranked separately at the end of Part 1.)*

---

## Part 4 — Deep-review findings (multi-angle audit)

A three-angle audit (single-card, set-building, portability/transfer) against the code surfaced the
following, de-duplicated and ranked. 🐞 = confirmed bug · ⚠️ = gap/ease · each has a code-grounded cause.
These drive the fix backlog.

### High
- ✅ *(fixed — see the fix order below)* **H1 🐞 Set-symbol image doesn't travel with a set.** `SaveProject`/`LocalizeArtInto`/`CloneWithRelativeArt`
  only handle `ArtPath`; `CardModel.SetSymbolPath` is saved as a raw absolute path and never
  copied/relativized/resolved. Move/zip a set → every card's custom set symbol breaks (silently falls back
  to the drawn pip). *(Both set & transfer reviewers.)*
- ✅ *(fixed — see the fix order below)* **H2 🐞 Missing frame silently renders the WRONG frame.** `BatchService.ExportAll` and the live preview
  resolve an unknown `TemplateName` to `templates[0]` with no warning. Open a received project referencing a
  frame you lack → cards export with an arbitrary installed frame. (Compounds W5 "frames don't travel.")
- ✅ *(fixed — see the fix order below)* **H3 ⚠️ No editing UI for whole card types.** Adventure, Subtitle, Land-symbol override, and Layout are in
  `CardModel`/`CardRenderer` but have no `DetailsWindow` fields — only reachable via import or hand-edited
  JSON. A from-scratch author can't make an adventure/subtitle card in the GUI.
- ✅ *(fixed — see the fix order below)* **H4 ⚠️ Planeswalker/Saga/Class have no authoring guidance or validation.** Layout is chosen from
  type-line substrings and parsed from rules-text syntax (`+1:`, `I, II —`, `{cost}: Level N`); wrong
  formatting silently yields empty/garbled badges, no CHECK.

### Medium
- ✅ *(fixed — see the fix order below)* **M1 🐞 Export all ignores the set `out/` and doesn't record where it went.** `OnExportAll` has no
  `InitialDirectory` and never sets `_lastExportDir` (unlike Export PNG / Print sheet). *(Confirms Part-3 #2.)*
- ✅ *(fixed — see the fix order below)* **M2 🐞 "Number cards" destroys real/imported collector numbers.** `OnNumberCards` overwrites every card's
  `CollectorNumber` unconditionally — no skip-if-set, no confirm — clobbering printing hints captured on import.
- ✅ *(fixed — see the fix order below)* **M3 🐞 Duplicate-collector check defeated by mixed formats.** `CardValidator` compares the raw string, so
  `5` vs `005` vs `005/20` never collide — mixed hand/auto numbering hides real duplicates (the one cross-card
  check W4 leans on).
- ✅ *(fixed — see the fix order below)* **M4 🐞 `DefaultTemplate` is saved but never applied on load.** `LoadProjectFile` ignores
  `project.DefaultTemplate`; new blank cards inherit the alphabetically-first installed frame, not the set's.
- ✅ *(fixed — see the fix order below)* **M5 🐞 Live CHECKS never runs `RenderInspector`.** `UpdateValidation` calls only `CardValidator`; the
  `art-blank`/border pixel checks run only in the QA harness/tests. A corrupt-but-existing art file renders a
  blank window yet shows "✓ No issues". *(Corrects this plan's earlier G1 claim.)*
- ✅ *(fixed — see the fix order below)* **M6 🐞 Ctrl+L overwrites/blanks hand-typed fields.** Main-window lookup assigns every text field from the
  face unconditionally; a vanilla result blanks a user-typed Loyalty/P-T. (Art & frame are preserved; text isn't.)
- ✅ *(fixed — see the fix order below)* **M7 ⚠️ Edit details… has no Cancel/discard.** Binds the live card directly; "Done"/Esc both keep edits —
  no way to back out a details session.
- ✅ *(fixed — see the fix order below)* **M8 🐞 Stray Loyalty hides P/T.** `IsPlaneswalker` is true if TypeLine contains "Planeswalker" OR Loyalty
  is non-blank, so a leftover Loyalty makes a creature render as a planeswalker (P/T box vanishes), no CHECK.
- ✅ *(fixed — see the fix order below)* **M9 🐞 DFC back faces dropped on more paths.** Beyond details-search (G6): Scryfall-search import adds
  front-only, and duplicate/print-sheet treat a real back face as just another front (generic back on sheets).
- ✅ *(fixed 1.6.17)* **M10 ⚠️ `.cardframe` dropped on the window is rejected.** `OnWindowDrop` handles projects/lists/images but
  not frame bundles — the core collaboration artifact can't be installed by drag-drop.
- ✅ *(fixed — see the fix order below)* **M11 🐞 Cross-drive / failed art copy stays absolute silently.** If `LocalizeArtInto`'s copy throws it
  keeps the original absolute path and Save still "succeeds" — that card won't travel, with no warning.
- ✅ *(fixed — see the fix order below)* **M12 ⚠️ Two sets in one folder: Export-all PNGs collide.** `BatchService.ExportAll` writes deterministic
  `NNN_slug.png` into the shared `out/`, so a second set overwrites the first's exports (extends W8).
- ✅ *(fixed — see the fix order below)* **M13 ⚠️ No set-symbol missing CHECK / validator symbol regex false positives.** `RenderInspector` doesn't
  check `SetSymbolPath`; `CardValidator.KnownSymbol` is narrower than what renders (verify token list).
- ✅ *(selection fixed: with several cards selected it asks Selected / All; collector number, land symbol and
  layout are still per card)* **M14 ⚠️ Bulk "Set fields on all" can't target a selection** (always all cards) and omits collector /
  land-symbol / layout — limits per-faction set work.

### Low
- ✅ L1 Clear-art doesn't reset pan/zoom (asymmetric with Change-art). · ✅ L2 Fuzzy art-match mis-binds
  prefix/substring names silently, no report *(now `MatchIntoWithReport` splits exact vs fuzzy and the UI
  lists the guesses for review)*. · ✅ L3 Re-importing the same list appends duplicates *(import now detects names already in the project
  and offers Skip-duplicates / Add-anyway / Cancel via `CountNamesAlreadyIn` / `RemoveNamesAlreadyIn`)*. ·
  ✅ L4 No duplicate-card-NAME check. · ✅ L5 Import silently drops empty/unparseable lines
  *(now `ParseWithReport` returns a skipped count surfaced in the import status)*. · ✅ L6 Collector width is `1/9`
  not `001/9` below 10 cards (doc mismatch) *(intended: padded to the set's size; the User Guide and Help now
  give examples for 9, 12 and 120 cards, 1.6.9)*. · ✅ L7 Single Export has no in-progress guard during a background
  Export-all *(OnExport now bails while Busy)*. · ✅ L8 Missing whole `art/` on load gives no
  aggregate warning (only per-card). · ✅ L9 Corrupt project shows raw parser message, no backup/restore *(load failure now copies the file to a
  `.corrupt-backup` sibling via `IoUtil.BackupCorrupt` and shows a friendly dialog instead of a raw error)*. ·
  ✅ L10 Deleting a frame still referenced by other cards leaves them pointing at a missing template
  *(delete confirm now reports how many cards use it via `TemplateService.CountReferencing`)*. · ✅ L11
  Import of a bare frame can't opt into full-art regions *(import now asks Full-art / Standard and passes the
  flag to `TemplateImporter.CreateFromFile` / `CreateFromUrlAsync`)*.

**The Low tier is closed** (L6, the last one, was a wording fix: pad width tracks the set total, as intended).

### Fix order (each change ships with unit tests + coverlet)
1. ✅ **Portability batch (done, `f919490`):** H1 set-symbol travels, M11 surface non-localized art, M4 apply DefaultTemplate + inherit. *(+ M1 Export-all → `out/` + last-dir, M3 dup-collector normalize)*
2. **Batch-correctness:** ✅ M1, ✅ M3, ✅ M2 Number-cards guard, ✅ M12 save-time warning when a folder already holds another project.
3. ✅ **Validation surfacing (done):** ✅ H2 missing-frame CHECK (+ wired live), ✅ M13 set-symbol CHECK, ✅ M8 stray-Loyalty CHECK · ✅ M5 live RenderInspector (pixel checks) (1.3.2).
4. **Authoring depth:** ✅ M7 details Cancel, ✅ M6 non-destructive Ctrl+L, ✅ H3 card-type editing UI, ✅ H4 layout authoring guidance (CHECK flags unparseable PW/Saga/Class syntax, reusing the renderer's own parsers) · ✅ M9 DFC handling (1.4.0).
5. **Big-ticket:** ✅ frames travel with a set — "Share set + frames" zip (`SetPackager`), ✅ whole-set validation report (W4 — "Check all cards" via `SetValidator`) · ✅ set profile (W1, 1.1.1) · W8 multi-set manager: will NOT build (decision).
6. **Low batch:** ✅ L1 clear-art resets framing, ✅ L4 duplicate-name CHECK, ✅ L8 load-time missing-art summary, ✅ L2 fuzzy art-match review report, ✅ L5 import skipped-line count, ✅ L7 single-Export in-progress guard, ✅ L10 delete-frame-in-use warning, ✅ L3 re-import duplicate prompt, ✅ L9 corrupt-project backup + friendly dialog, ✅ L11 full-art option on bare-frame import.

### Agreed roadmap (post-1.1.0)
- ✅ **Backward compatibility (done, 1.1.2):** one shared `JsonCompat` options for all persisted types — case-insensitive keys, ignore unknown properties, tolerate comments/trailing-commas/quoted-numbers, null-guarded loads; `FormatVersion` stamp on projects. Golden "old format" load tests guard it (`BackwardCompatTests`). **Rule:** only ADD optional properties with defaults; never rename/retype an existing one.
- ✅ **Automated backups (done, 1.1.3):** `ProjectBackup` keeps rolling timestamped copies (last 15) of the previous good project in a `backups/` folder on every save; a **Restore…** button rolls back. Complements atomic-write + corrupt-project `.corrupt-backup`. Guarded by `ProjectBackupTests`.
- ✅ **W1 set profile (done, 1.1.1):** `SetProfile` on the project (set code / rarity / copyright / artist / symbol) + default frame; a **Set defaults…** dialog; new/imported cards inherit blanks; optional apply-to-existing; persisted + back-compat (old files without a profile load fine).
- ☐ **Multi-sets (W8): will NOT build** — a set is already a self-contained folder; user switches via Save/Open one at a time. Decision: keep the save/load switch safe (unsaved-changes prompt already fires); no manager/recent-list UI.
- ✅ **M9 double-faced cards (done, 1.4.0):** a card holds an optional recursive **back face** (`BackFace` + `DfcStyle`, additive & back-compat). UI: **Make double-faced / Edit back face… / Show back** flip / style dropdown. Corner indicators drawn by us: **flip arrow** or **sun/moon**. Export writes both sides (`-back.png`); single-sided sheet expands both faces (`ExpandFaces`); double-sided sheet pairs the **real** back. Import fills one card/two faces. `ActiveFace` routes preview/art/validation to the shown face. Tested (model, render, export, expand) + reviewed (fixed `CopyFrom` clobbering `IsBackFace`, removed dead `ExtraBackFaces`).
- ✅ **Horizontal / landscape cards (done, 1.5.0 — agreed minor bump):** Battles, Planes and Phenomena render **sideways automatically** (`CardModel.WantsLandscape`, whole-word type match so *Planeswalker* ≠ *Plane*; `Orientation` = auto/portrait/landscape override in Edit details). No landscape copies of frames: `TemplateSpec.ToLandscape()` re-lays-out ANY drawn frame (width-spanning regions stretch, right-anchored slide, fixed plates keep size + edge distance; the lost height comes from the art first — down to a 3.2:1 strip — then the text box, ≤60%), and `TemplateService.ResolveFor` swaps it in at render time with the frame generated once and cached by content hash in `cache/landscape` (never in the user's template folder). Resolved in `CardRenderer.RenderToBitmap` (every path) and `MainWindow.TemplateFor` (so CHECKS + the pixel inspector judge the drawn geometry). **Battles** get their own `Defense` field + a curved heater-shield (distinct from the loyalty pentagon) — using Loyalty made a Siege behave like a planeswalker. Scryfall `defense` and CSV `defense`/`orientation` columns mapped. Print sheets turn a sideways card upright into a normal slot (`SheetExporter.FitToSlot`, 90° CW) and a Battle's upright transformed back lands behind it. Imported wide frame images become 1050×750 templates; the frame designer's canvas follows the template aspect; a sideways card on an imported (fixed) portrait frame gets a CHECKS note. DFC badge sized from the short side. Verified visually on all 15 drawn frames; `LandscapeTests` (42, incl. layout invariants over every built-in and a pixel-exact sheet-slot check verified to fail without the rotation) + a window preview test; `--qa` gains Battle + Plane cases (30 renders, 0 errors).
- ✅ **Two-part cards (1.6.0 by agreement — base, flip, split and aftermath all DONE):** ONE shared feature for flip / split / aftermath — the data is identical (a second half with its own name, cost, type, rules; split and aftermath halves also have their own art), only the arrangement differs. A second half + a layout setting (`flip` | `split` | `aftermath`), additive & back-compat; "Show other half" edits it like the DFC back face; prints as a normal ONE-sided card (never a back); Scryfall `layout: flip` / `split` (the Aftermath keyword tells aftermath apart) map to ONE card, not two unlinked ones. Examples: *Erayo, Soratami Ascendant* (flip), *Wear // Tear* (split + Fuse), *Destined // Lead* (aftermath). Order:
  - ✅ **1.6.0 — the two-part base + Flip (done).** `CardModel.OtherHalf` (+ transient `IsOtherHalf`, one level, deep-cloned by `CopyFrom`) and `HalfLayout` = "flip"; `Parts()` = faces + half for stored data (art paths travel), `Faces()` stays per printed side, so exports/sheets give a flip card ONE slot. `TemplateSpec.CardLayout`/`ToFlip()` (title → rules → type with P/T at its end, art = middle third, symmetric about the centre; credits on the border; `EffectiveTextBox` doesn't stretch) and `TemplateService.FlipOf` (generate, then the bottom half = the top half mirrored — the look the user approved — cached in `cache/flip`). The renderer draws the front half, then the other half through a 180° rotation about the centre (set identity inherited from the front). **Frame versions:** a template folder may carry `flip/` and `landscape/` subfolders (complete templates; wrong-shaped ones ignored), preferred by `ResolveFor`, extracted from embedded resources for the shipped PCC frames, carried by `.cardframe` export/import. Built by `jacob/build/build_variants.py` (PCC + Jacob's 9, from the same layers) and `own_image_variants.py` (Jacob's 6 own-image frames: his pixels cut + rearranged, cut lines auto-detected). Scryfall `layout: flip` → one card; CSV `flip_name/type/rules/pt`; CHECKS: `no-flip-frame` + "Flipped half:" content checks (live panel + Check all). UI: FLIP CARD section (Make / Edit flipped half… / Show flipped — the preview turns over and pan follows the mouse). Showcase's borderless panel and the bottom-border inspector (now measured at several columns when credits ride the border) fixed along the way. `FlipCardTests` + window tests; `--qa` gains three flip cases (33 renders, 0 errors).
  - ✅ **1.6.2 — Split (done)** (+ the Fuse bar; also Duskmourn Rooms, which Scryfall lists as split). `HalfLayout` = "split" (`CardModel.IsSplit`); the half has its own cost and art. `CardRenderer.DrawSplit`: laid out in READING coordinates (the card turned a quarter clockwise; `SplitGeometry`), each half is the frame's NORMAL card drawn at ~0.66 scale (so every frame — drawn, PCC, Jacob's picture frames — works with no new frame art), rules start larger by the scale and shrink to fit; mapped onto the upright card by `SplitLayout.ToCard` (reading left = card bottom, matching real *Wear // Tear*); the card's credits run upright on its bottom edge. A last line both halves share that is a Fuse line or fully parenthesized (a Room's door rules) is drawn ONCE in a bar across both (`SplitSharedLine`). Scryfall `layout: split` → one card (`AttachFaces`), and the downloaded side-by-side art crop is cut in two (`SplitSharedArt` / `ImageIntake.SplitSideBySide`) on Ctrl+L and batch import. CSV `split_name/cost/type/rules/flavor/art`. Checks: other-half checks include its art ("Other half: …"); the pixel inspector inspects each half as a normal card (`RenderInspector.InspectCard`). UI: **Make split card / Edit other half… / Read sideways**; clicking (or dropping onto) a half picks where art/pan/zoom go (`PreviewSide` vs `ActiveFace`, `PanDelta` turns drags into the sideways half). `SplitCardTests` (17) + 2 window tests; `--qa` gains three split cases. Follow-ups done in 1.6.9 (below): per-half frames and the Fuse bar tinted per half.
  - ✅ **1.6.3 — Aftermath (done).** Scryfall lists *Destined // Lead* as `layout: split` too, so it arrives as a split card; `CardModel.IsAftermath` = a split card whose other half's rules START with the Aftermath keyword (no new data, no new UI — parsed like the other special layouts). The split geometry was generalised: each half is a `SplitPart` (its card, its template, and ONE `ToCard` matrix — scale + quarter turn + offset — used by drawing, hit-testing and drag-to-pan alike). Aftermath: the first half is drawn upright across the top with the frame's wide layout (`ResolveFor` with Orientation=landscape → derived for drawn frames, the shipped landscape version for PCC/Jacob's frames, the plain picture otherwise); the other half is the normal card turned a quarter COUNTER-clockwise below it (title along the right edge, like the real card); credits along the bottom. Read sideways turns the preview counter-clockwise for it. Scryfall's aftermath art crop is cut at 61.3% (wide picture | narrow picture, measured on Destined // Lead). `SplitCardTests` gains geometry/orientation/hit-test/render/art-cut tests (+1 window test); `--qa` gains two aftermath cases.
- ✅ **Split follow-ups (done, 1.6.9):** a split or aftermath card's other half can have a frame of its own: `CardModel.HalfTemplateName` (on the card, blank = the card's frame; the half's own `TemplateName` only ever recorded what it was made with, so older sets look unchanged), picked in **Other half's frame** under FLIP / SPLIT CARD (window test, undoable). `TemplateService.Named` looks frames up by name (installed frames, replaced on each `LoadAll`, then folders loaded with `LoadFrom`); `CardRenderer.HalfTemplate` → `SplitGeometry` gives each half its own scale and shape, so a frame of another shape fits its slot. Travels with **Share set + frames** and counts for the delete-frame warning (`TemplateService.FramesUsed`); CHECKS `half-frame-missing`; the other half's checks use its own frame; CSV `split_frame`. The Fuse / Room bar is each half's colour at its end (`SplitBarFill`, from the half's cost, lightened, blended at the gutter). Also fixed: plain rules text on a frame with a medallion set into the text box (PCC, most of Jacob's) now stops above it instead of running under it (`DrawTextBox` ornament; short text unchanged); the medallion cache is keyed by frame image. `SplitFramesTests` (8); `--qa` gains two cases (48 renders, 0 errors). Review pass: in `--rendercards templates=<dir>` a folder's frame now wins for the other half too, as it already did for the card (`TemplateService.Named` checks folder frames first).
- ✅ **Token example (1.6.11):** `examples/05-tokens-emblems` redone for the token layout: nine tokens (vanilla, with rules, an emblem, one on a picture frame) across nine frames with original art made for it, rendered with `--batch`, plus the 3×3 print sheet; the User Guide's token picture comes from it.
- ✅ **Frames set aside by mistake come back (1.6.19):** before 1.6.17, a moment of low memory or a locked file set a picture frame aside as `frame.png.corrupt-…` and drew a generated placeholder in its place, for good. Now a set-aside frame that reads fine is put back — newest first, only over a missing frame or that exact placeholder (the frame its spec generates, byte for byte), never over a frame the user put there since, and never with a file that's really corrupt (`RestoreSetAsideFrame`). Also tested now: **Fill blanks** gives a back face it adds the set's defaults (that code shipped untested in 1.6.15). `LooseEndsTests`; each fails with its fix taken out.
- ✅ **Regular printings (1.6.24)** — a card looked up by name alone came as Scryfall's default printing, which is
  sometimes a special one (Lightning Bolt's is a Marvel crossover: Thor's art). A special printing (Universes Beyond,
  promo, digital, joke/memorabilia/Alchemy set) found by name now gives way to the same card's regular paper printing
  (`!"name" game:paper -is:ub -is:promo not:funny`, same card only — not a card with a face of that name); one with
  none keeps it, and a deck list's printing hint or a CSV's set + number is never swapped. Single look-ups and batch
  imports both. `RegularPrintingTests`; each part fails its test when taken out.
- ✅ **Every frame (1.6.23)** — awkward real cards (planeswalker, saga, class, leveler, battle, split, adventure,
  double-faced, basic land, long names and text) rendered on all 18 installed frames and looked over. Fixed: ability
  badge, loyalty and defense numbers were white on the pale Parchment and Sealed Gate badges (now a dark ink when white
  can't read: contrast under 1.7); Midnight's P/T numbers were near-black on its near-black box (the template's P/T ink
  now gives way when its contrast is under 2.5 — Alchemist's Steel, at 2.8, keeps its own); a basic land's big symbol
  was drawn over the sample frames' seal; Sealed Gate's footer was dark on its stone texture (now light, outlined).
  Only those cards change (checked image by image across all 18 frames). `ReadabilityTests`; each fails with its fix
  taken out.
- ✅ **Seal (1.6.22)** — from the same trial run: a planeswalker's or saga's long text ran under the medallion the
  sample frames set into the text box's bottom edge (ordinary rules text already stopped above it). The planeswalker,
  saga and class layouts now stop above it too. `FrameSealTests`; fails with the fix taken out.
- ✅ **Trial run (1.6.21)** — the released exe driven on real deck exports (Moxfield CSV, Arena and plain lists:
  double-faced, split, fuse, adventure, saga, planeswalker, basic land, not-found cards): a row's set + collector number
  that is a different card (`Phyrexian Arena, 2xm, 95` is Glaze Fiend) replaced the card the row named — now that
  printing is used only when its name matches (ignoring case, accents, punctuation; either face), else the name is
  looked up; a sample frame is refreshed on upgrade only when neither its picture nor its layout was changed (a
  layout tuned to the old picture no longer gets a new picture under it). `TrialRunTests`; each fix fails its test
  when taken out.
- ✅ **Third review (1.6.20), of 1.6.12–1.6.19:** a crash's recovery copy was overwritten by a later session working on the same set (the file was named by set alone — now by set and session; a reused process id no longer hides an old copy); **Don't save** on a recovered copy now keeps it aside like **Discard**; **Export all** and **Print sheet** fell back to the first frame for a card whose frame isn't installed, where the preview uses the set's house frame; a frame imported, renamed or saved as new under a name that's taken now gets "Name (2)" (two frames of one name moved cards between them and hid one); frame restore decides once (`frame.png.kept-…`) instead of redrawing the frame on every load, recognises placeholders by a tag (`frame.png.placeholder`) and never restores a set-aside placeholder over the picture; a deck site's **Edition** / **Edition Code** / **Card Number** columns are read; bundles inside bundles are opened two levels deep at most. `Review3Tests`; each of the 13 fixes fails its test when taken out.
- ✅ **Test-run memory (1.6.18):** the test process peaked at **14 GB** (9.5 GB from the window tests alone), close to the 16 GB of GitHub's Windows runners, and on a busy machine it ran out of memory. No test ever let go of its `MainWindow`: WPF's `Application` keeps every window until it's closed, and a Dispatcher that's never shut down keeps its timers (and so the window) alive after its thread ends. Measured: closing alone or shutting down alone changes nothing; both together (`TestHelpers.EndUiThread`, in every `OnAppThread` helper) bring the window tests to 0.7 GB and the whole suite to **2 GB**. `AWindowTest_LetsGoOfItsWindow_WhenItEnds` fails if either half is taken out. Test-only; the app is unchanged.
- ✅ **Loose ends after both reviews (1.6.17):** a **`.cardframe` / `.zip` dropped on the window installs** the frame, through the same code as **Import…** (M10). On a **print sheet**, a back face with no frame of its own now wears its **front's** frame, as on screen and in the batch export, instead of the first installed frame. The split-card tests' change box ignores rows and columns with only a stray changed pixel or two, so a one-pixel blip elsewhere on the card can't fail `Aftermath_TopHalfDrawsAtTheTop_OtherHalfBelowRunningDown` (it did once on CI and couldn't be reproduced); where each name is drawn is still checked across the whole card. **A picture frame that can't be read for a moment is left alone:** running out of memory, or the file being locked by a sync client or scanner, used to count as a corrupt file, so the frame was set aside and a placeholder drawn over it for good (seen on every shipped picture frame in a memory-starved test run); now the frame is just skipped that time, and only a real decode failure sets it aside (`LooseEndsTests`).
- ✅ **First independent review, Tier 3 — CI and tests (1.6.16):** the release job runs one at a time (`concurrency`), both jobs time out after 30 minutes, and the **published single-file exe is run before it's released** (`--version` must match, `--selftest` must render). The **golden images run on CI** (a probe run showed CI's renders match the dev machine's baselines within tolerance), and `CARDINATOR_UPDATE_GOLDEN=1` no longer passes silently: it rewrites only baselines that really changed, fails naming each, and is refused on CI. `Planeswalker_LoyaltyBadge_IsDrawn` no longer changes the type line as well (which made it pass whatever the shield did): only the loyalty differs, and the change must land in the shield's place. The version test reads `<Version>` from the csproj instead of comparing the assembly with itself. **Sample frames reach upgrades:** a shipped frame file is refreshed when a new version ships it differently, but only if it's still exactly what an earlier version wrote (`.bundled-frames.json` records the hashes) — a frame the user changed is never overwritten (`CiAndTestHygieneTests`).
- ✅ **Second independent review, Tier B + docs (1.6.15):** each fix has a test in `Review2TierBTests` that fails without it (mutation-checked). **Frames:** two frames with one name resolve to the same (first) copy everywhere — picker, sheet, Export all, split-half catalog (stable sort + first wins); a back face follows its front's frame unless it has one of its own, the picker shows and edits the shown face's frame, and a back face's frame counts as in use (Share set, the delete warning); renaming a built-in with "Overwrite built-in" saves a copy instead of losing the built-in; a card on a frame that isn't installed shows an empty picker, is drawn on the house frame, and Design / Delete / Export refuse rather than act on another frame; Frame Design only puts a *new* frame (Save as new) on the card; Save as new copies the flip / landscape / token layouts; bundles carry `token/`, and a frame slugged `flip` is a frame; an image that can't be decoded fails the import. **Card editing:** the double-faced / flip / split / meld controls re-sync after Ctrl+L, Edit details… and Fill blanks; every fill path keeps an adventure; CHECKS shows notes (ℹ no art, shared name). **Rendering:** `−X:` / `+X:` loyalty abilities get a badge; rules text keeps the spacing as typed (`{2}{R}` together, `{T}:` with no gap) and wraps only at spaces, so a cost never splits; a word longer than the line breaks between letters; text still too long at the smallest size is a CHECKS warning (`text-overflow`); loose costs keep everything (`2{G}`, `W/U`, `2/W`, `G/P`), split_cost is normalised; a set-symbol image keeps its shape. **Sets:** the Set defaults symbol is copied into the set folder; closing during an export asks first. **Imports:** a wrong deck-list printing hint is replaced by the printing found (a CSV's own set code is kept), a set-only hint asks Scryfall for that set; a `qty` column; links served as `binary/octet-stream` (or with no image extension) are kept when the bytes are a picture. **Docs:** QUICKSTART's CSV example has its header row; A4 / bleed / DPI are command-line features; split cards import as one; the set symbol is set-wide or per selection; save each set in its own folder; where CHECKS is; Ctrl+L keeps your art; Help's backup count and recovery copy; the AppData fallback; the search picker; DEVELOPING's render branches; these stale findings marked.
- ✅ **Second independent review, Tier A (1.6.14):** each fix has a test in `Review2TierATests` that fails without it (mutation-checked). **Unsaved state:** save → undo → a new edit stays unsaved (`CommitHistory` drops a saved index that was in the redo tail); saving art from outside the set no longer brings the ● back (the history entry is re-baselined after `LocalizeImages`); art that lands on a card the user clicked away from marks the set unsaved, and art for a card that was undone goes nowhere. **Recovery copies** (`RecoveryStore`): every minute while unsaved, on Windows session end and on an unhandled error; offered at the next launch, opened as the set it belongs to; a save or "Don't save" removes this session's copy, "Discard" keeps it in `recovery/declined`. **Import (1.6.13 regressions):** only a quote that starts a field opens a quoted one, `#` comment lines never do, and an unclosed one falls back to line-by-line — a stray `"` can't swallow later rows or crash; a lowercase bracket word is a set code only with a collector number after it. **Lookups** never replace a back face or other half the card already has (same card: blanks filled). **Frames:** a procedural template's `frame.png` with no `.hash`, or written after it, is hand-made and never drawn over; `RenderFormatVersion` 2 redraws frames for the 1.6.13 modern-panel fix. **Backups:** a set in a folder named "backups" is a backup only when timestamped, or when a set file sits in the folder above. **Scryfall:** search/named/random/collection spaced 500 ms (2/s), a 429 waits out the 30 s lockout. **Search adds:** a back face / other half takes the front's frame (`CardDetailsFill.TakeFrame`) and the set defaults; split art is cut into halves. **Frame Design:** Esc/Enter in an open dropdown don't cancel/apply the window. **Edit details:** a download timeout is an ordinary failure, not "Lookup failed"; a meld reply after Cancel doesn't touch the card.
- ✅ **Independent review, second tier (1.6.13):** each fix has a test in `ReviewTier2Tests` / `WindowSmokeTests` that fails without it (mutation-checked). **Art:** EXIF orientation applied (`ImageIntake.LoadOriented`), so phone photos aren't sideways. **Exports:** single and Export all PNGs are tagged with the DPI that prints them at card size (`CardExporter.AtCardSize`, 600 at 1500 px). **Scryfall search** returns a double-faced card as one card with its back (and a split/flip card with its other half): `MapSearch` uses `AttachFaces`; the back's art is downloaded too. **Batch lookups:** a failed `/cards/collection` chunk loses only its own cards (they fall to the fuzzy pass). **Rendering:** rules text wraps around the P/T box on full-art/borderless/overlay frames (`CardRenderer.RulesAvoid`); the modern frame bakes `EffectiveTextBox`, so with the footer on the border the panel reaches the text. **Import:** quoted CSV fields may hold line breaks; semicolon delimiters; Excel's Windows-1252 files (`ImportService.ReadListFile`); `split_art` resolved beside the list; deck lists take lowercase set codes and skip headings like "Sideboard:" / "Creatures (25)". **Edit details:** closing it (Cancel included) stops a lookup still running from writing into the card. **Frames:** deleting a frame no longer re-frames the selected card (it stays flagged); import still applies the new frame and says so; frame links go through the art downloader's checks (32 MB cap, no HTML); Frame Design number boxes read "0,5" as a half; derived flip/token/landscape frames are bounded in memory and on disk (`TemplateService.Remember`). **Symbols:** ½ and ∞ no longer share a cache file (`SymbolService.SafeFile`); an HTML page is never cached as a symbol. **Share set** offers to save unsaved changes first, finds each frame by its real folder (a frame renamed in place keeps its old one) and includes its flip/sideways layouts.
- ✅ **Independent review, data-safety batch (1.6.12):** six read-only reviewers (rendering, data safety, UI, network, docs, tests/CI); every finding below was checked in the code, and each fix has a test that fails without it. **Backups:** an unchanged file adds no copy, and pruning keeps the first backup of each of the last 30 days on top of the 15 newest (`ProjectBackup.Prune`), so Ctrl+S after a mistake can't flush the good copies. **Unsaved ●:** Set defaults / Add art from folder mark set-level changes (`_metaDirty`) that the card history can't see; a restored or directly opened backup has no history entry matching disk (`MarkUnsavedAgainstDisk`); Save commits the pending undo step first. **Opening:** a card/frame `.json` is refused (`NotAProjectException`, no `.corrupt-backup`); a file in `backups/` saves over its set file (`ProjectBackup.ProjectFileForBackup`). **House frame** is saved as loaded, even when not installed here (and Set defaults keeps listing it). **Frame Design rename in place** moves every card face/half and the house frame to the new name (`RenameFrameReferences`). Docs: wrong button names (Import file…, Fill blanks from Scryfall, Add art from folder…, Duplicate, Scryfall search…, Print sheet's Front + back), README's broken token image and legal wording, a CSV column reference in the User Guide, `--editframe` in CLI.md, `--frames builtin` (the frames page had empty art windows: a stale sample-art path), TEST_PLAN's stale gaps marked resolved.
- ✅ **Tokens and emblems (done, 1.6.10):** `CardModel.IsToken` — "Token" or "Emblem" among the types before the dash (Scryfall's own type lines, so lookups, `t:token` search imports and CSV rows need nothing new). `TemplateService.ResolveFor` gives a token its frame's token layout (`TemplateSpec.ToToken`, `CardLayout` "token"): title, P/T box and credits stay put, the art grows down, the text box keeps its bottom edge and is sized to the text (`TokenTextLines`: a rough line count, at least 3 lines, at most the frame's box); a vanilla token has no text box (the generator skips zero-height panels, `EffectiveTextBox` doesn't stretch it, the P/T box sits under the type line). Drawn frames are generated for it (cached in `cache/token`); picture frames are rearranged (`RearrangeForToken`: the art's middle stretched, rows cut from the text box above its middle, so title, borders and medallion keep their pixels — no visible seam on the textured ones); the medallion found on the frame is reused (`CardRenderer.KnowOrnament`) and the box made taller for it; a picture frame's "token/" variant folder is used if it ships one; CHECKS `no-token-frame` only if that fails. The name is centred when there's no cost. **Add token** (CARDS) starts a vanilla 1/1 Soldier. `TokenTests` (17) + a window test; `--qa` gains six token cases (55 renders, 0 errors).
- ✅ **A real 1.6.8 set as a fixture (1.6.9):** `fixtures/layouts-1.6.8/` is a set with every layout (the QA cards, a double-faced card, a meld pair, art in `art\`) saved by 1.6.8's own `ProjectWriter` from a worktree of commit 8b44477. `LegacyFixtureTests`: it loads every layout; a load + save keeps every value it wrote at every depth (only new fields may appear; checked to fail when a field is dropped); every card renders and its split cards keep the card's frame. Never regenerate it; add a new fixture for a new format instead.
- ✅ **Station and Case (done, 1.6.8):** both reuse the level up bands (`CardModel.HasBands`; `LevelBand.Kind` picks the badge). Station (`IsStation`: a "7+ | …" line): `ParseStation` — prelude band, then one band per threshold holding every line after it; the P/T box on the band named by "artifact creature at N+" (else the last), never the base band; the badge is the threshold. Case (`IsCase`: a "Solved —" line): `ParseCase` — opening ability, To solve band (magnifying glass), Solved band (check mark), wording kept. `CardRenderer.BottomOrnament` finds a medallion set into the text box's bottom edge from the frame image (PCC frames and most of Jacob's; none of the drawn frames), and the bottom band's text stays above it. CHECKS: `station-no-thresholds`. `StationCaseTests` (11); `--qa` gains a Station and a Case (medallion frame).
- ✅ **Level Up (done, 1.6.4):** `CardModel.IsLevelUp` (a "LEVEL n-m" / "LEVEL n+" line); `CardRenderer.ParseLevelUp` reads Scryfall's wording into bands (base band = everything before the first LEVEL line, with the card's P/T; each LEVEL line → its range, the bare "3/3" after it → that band's P/T, the rest its rules). `LevelUpLayout` stacks the bands down the text box at ONE shared rules size (shrunk until all fit, spare height shared out), and `DrawLevelUp` shades each band a little darker, draws an arrow LEVEL badge (frame colours, ink picked for contrast) and a P/T plate per band (capped to the band height so short full-art text boxes aren't crowded); no corner P/T box. Works on every frame incl. Jacob's. Check `levelup-no-levels` (Info) when "Level up" has no LEVEL lines. `LevelUpTests` (9); `--qa` gains a Level up case. Original plan: e.g. *Student of Warfare*. e.g. *Student of Warfare*. The text box splits into bands, each with an arrow-shaped LEVEL badge on the left ("2-6", "7+") and its own P/T box on the right; the base P/T sits in the first band instead of the usual corner box. No new data — parsed from the rules text the way Class levels are (Scryfall writes `LEVEL 2-6
3/3
First strike`), drawn with the existing badged-row code.
- ✅ **Adventure fix (done, 1.6.5):** the old sub-box squeezed the spell's rules to a few pixels. Now the modern storybook layout: `CardRenderer.AdventureLayout` splits the text box into two pages at ONE shared rules size (shrunk until both fit, floor 9) — the left page holds the spell's name bar (tinted by its cost via `PrototypeTint`, dark ink, cost at the right), its type line and its rules; the right page holds the creature's rules and flavor, always wrapping around the P/T (or defense) box. Works on every frame incl. Jacob's. `AdventureTests` (4: layout, shrinking, pixel test that the spell only touches the left page and the creature only the right, every frame inspected); `--qa` gains two adventure cases.
- ✅ **Prototype + Mutate (done, 1.6.4):** `CardRenderer.ParseTopBand` reads the rules' FIRST line as Scryfall writes it — `Prototype {cost} — p/t (reminder)` (any dash, spaced P/T tolerated) or `Mutate {cost} (reminder)`; the rest of the rules is drawn below. `TopBandLayout` puts the band across the top of the text box at one rules size shared with the rest (shrunk until both fit; a prototype's cost + P/T plate column at the right end, the plate capped for short full-art boxes); `DrawTopBandCard` tints a prototype band by its cost (`PrototypeTint`: its colour / gold for 2+ / grey) with dark ink (readable on frames with white rules text), shades a mutate band (darker on light text boxes, lighter on dark ones), and the rules below wrap around the card's P/T box on every frame. `PrototypeMutateTests` (15); `--qa` gains Prototype + Mutate cases. Original plan: e.g. *Blitz Automaton*. e.g. *Blitz Automaton*. A band in the prototype's colour across the top of the text box holds the reminder text, with its own small mana cost and P/T box at the right end; the full-size cost and P/T stay in the usual places. No new data — parsed from the rules text (`Prototype {2}{R} — 3/2`); band colour from the prototype cost's colours.
- ✅ **Mutate** (done with Prototype, above). Original plan: e.g. *Gemrazer*. A shaded band across the top of the text box holds `Mutate {cost}` + its reminder, set apart from the rest of the rules. No new data — parsed from the rules text. Smallest of the lot; likely bundled with Prototype (same "band at the top of the text box" drawing).
- ✅ **Meld (done, 1.6.5):** each part is a double-faced card whose BACK FACE is the melded card; `CardModel.MeldHalf` ("top"/"bottom", kept in step on the back like `DfcStyle`) says which half that back prints, `MeldWith` names the partner. `CardRenderer.MeldPart`: the melded card is drawn whole at ×1.4, turned a quarter counter-clockwise across the two backs laid side by side (top half on the left card), and each back shows its half inside its own black border (`DrawMeldBack`) — every frame works, no new frame art; exports/print sheets get it for free since a back is a back. Fronts get a meld icon (`DfcStyle` "meld"). Scryfall: a meld part's `all_parts` gives the partner and the melded card's URI (`ScryfallMapper.ReadMeldParts`, `ScryfallClient.LookupUriAsync`); `CardDetailsFill.AttachMeld` makes the back the melded card and picks the half from collector numbers (the melded card's "15b" = the bottom half's part, which carries the credits) — on Ctrl+L, Edit details lookup and batch import (one fetch per melded card). UI: MELD section — Make meld card / Add meld partner / Back: top|bottom half / Show melded card; edits to the melded card through one part are copied to the partner's back property by property (`SyncMeldBack`); dragging on a meld back pans the turned art the right way. The inspector inspects the melded card whole. `MeldTests` (9) + 2 window tests; `--qa` gains two meld backs; `--rendercards` now also writes `-back.png`. CSV meld columns (1.6.6): `meld_name` / `meld_half` / `meld_with` / `meld_cost|type|rules|flavor|pt|art`; rows naming the same melded card are partners sharing one melded card (text from either row) on opposite halves (`ImportService.PairMeldRows`); a looked-up row keeps what the list wrote and Scryfall fills the rest, picking the halves when the list gave none (`ImportedCard.MeldHalfGiven`). 3 tests in `MeldTests`. Original plan: e.g. *Bruna, the Fading Light* + *Gisela, the Broken Blade* → *Brisela, Voice of Nightmares*. Two separate double-faced cards whose BACKS are the two halves of one oversized card: laid side by side they form a single sideways card. Fronts show a meld icon in the title bar and the melded card's P/T as a small hint. Needs a link between two cards in the set plus the melded card's own fields/art; the melded card is drawn once at double size (builds on the 1.5.0 sideways layout) and cut in half to become each part's back, so printing double-sided just works. Scryfall marks these `layout: meld` with `all_parts` naming the partner and the result.
- ✅ **M5 live RenderInspector (done, 1.3.2):** the live CHECKS panel now runs the pixel inspector on a clean render (skipped during drag), merged via `LiveChecks.Merge`, so a corrupt-but-existing art file that renders blank is flagged live. Hardened `NearArtBacking` to a tight band around (8,8,10) so legitimately pure-black art isn't false-flagged.
- ✅ **M14 bulk-edit on a selection (done, 1.3.2):** "Set fields on all" offers to target a multi-selected subset (vs all) via a new `ApplyBulkEdit(targets, …)` overload.

### Fresh-eyes review hardening (plan: `REVIEW-2026-10-04.md`)
A full four-area review at 1.4.0 (rendering / UI / data+services / tests+docs). Findings and the batch
plan live in **[REVIEW-2026-10-04.md](REVIEW-2026-10-04.md)**; batches ship in order, patch-bumped.
- ✅ **Batch A — data safety (done, 1.4.1):** ✅ **A1** back-face art now travels with a set — `CardModel.Faces()` is the one place that walks front+back, used by `SetFolder.LocalizeImages`/`CountMissingArt` and `CardProject.MakeArtRelative`/`ResolveArt` (a DFC's back art used to stay an absolute path into the app cache, so a moved/zipped/shared set rendered the back blank with no warning). ✅ **A2** back-face edits reach dirty-tracking/undo/preview — `MainWindow` now tracks the back face's `PropertyChanged` too (`AttachCardEvents`/`SyncBackFaceSubscription`); previously pan/zoom/paste on the flipped preview never set ● and was lost on close. ✅ **A3** an unreadable **user-imported** frame (CustomFrame, no `FrameSrc`) is set aside as `frame.png.corrupt-<stamp>` instead of deleted — it used to be deleted and then overwritten by a generated placeholder, i.e. permanent loss from one bad decode. ✅ **A4** Restore… fixed three ways: art resolves against the set folder via `ProjectBackup.ArtRootFor` (not `backups\`), the real project file stays the save target (one Save restores in place), and a failed load leaves the open project untouched (`LoadProjectFile` now returns success). ✅ **A5** `ProjectBackup.ListBackups` matches only `<name>.<timestamp>[-n]<ext>`, so pruning `MySet` can no longer delete `MySet.v2`'s backups. Tested by `DataSafetyTests` (8) + 2 `WindowSmokeTests`.
- ✅ **Batch B — DFC correctness (done, 1.4.2):** ✅ **B1/G6** every lookup path now agrees — one shared `CardDetailsFill.AttachFaces` turns a real double-faced card into ONE card with a back face (+ default sun/moon) on Ctrl+L and in the details-editor search, not two unlinked cards as before. ✅ **B2** the attach is gated on `ScryfallMapper.IsTwoSidedLayout` (transform / modal_dfc / reversible_card / double_faced_token / battle), so a **split/flip/aftermath** card no longer becomes a bogus DFC with a sun indicator and a spurious `-back.png`; single-card lookup adds its other half as its own card and batch import logs it. ✅ **B3** `CopyFrom` deep-clones `BackFace` instead of aliasing the other card's instance (two cards could have silently shared one mutable back; this is also what makes the details-editor Cancel correctly drop a filled-in back face). ✅ **B4** `SetValidator` validates the back face too, labelled "Back face: …" (dup-name/collector excluded — the back isn't its own card). ✅ **B5** the indicator dropdown is disabled on single-faced cards, and a cancelled back-face edit no longer leaves a stuck ● (`CommitHistory`'s no-change early-out re-derives `Dirty`). Tested by `DfcCorrectnessTests` (18). Help window synced to the real UI (DFC section, batch button names, backups/Restore section added, Ctrl+L instead of a non-existent Search button); README/USER_GUIDE/QUICKSTART button names corrected.
- ✅ **Batch C — renderer correctness (done, 1.4.3):** ✅ **C1** badged rows (planeswalker / saga / class) draw through `DrawGlyphRun`, so a shadowed font gets its outline — white rules text on full-art/showcase/cinematic was unreadable on light art. ✅ **C2** `ParseClassLevels` uses `int.TryParse` — "Level 2147483648" threw `OverflowException` out of the whole render and could abort a batch export. ✅ **C3** planeswalker ability text wraps around the starting-loyalty shield (shared `LoyaltyRect` + an avoid band in `LayoutPw`, the same approach rules text already used for the P/T box); the shield is drawn *over* the text, so the overlap silently ate words. Asserted geometrically via the new `InspectPlaneswalkerLayout` QA hook, since a pixel diff can't see hidden text. ✅ **C4** the full-art scrim is sized from `EffectiveTextBox`, so with the footer on the border the last lines aren't left on bare art. ✅ **C5** `RenderInspector` measures the bottom border left of the footer band when `FooterPlacement="border"` — a long collector/artist line crossing the centre column produced a false "border too thin" Error on a good card (a genuinely missing bottom border is still caught; both directions tested). ✅ **C6** one shared frame-image loader (`CustomFrameComposer.LoadBitmap`), so the bytes-not-UriSource discipline that keeps a corrupt frame recoverable can't drift between two copies. ✅ **C7** footer-on-border font no longer clamps to 1px on a 9–18px border; `DrawTexture` covers the real canvas instead of a hardcoded 760×1060; the footer-placement test reads `FooterPlacement` (it tested `FrameStyle`); pip-derived caches are cleared on `SymbolService.Updated`; `SheetExporter` single- and double-sided sheets share ONE layout path and stream pages to disk lazily (a 100-card double-sided deck held ~0.8 GB of pages in memory); `TemplateImporter` finds zip entries written with backslash separators. Tested by `RendererCorrectnessTests` (8, each verified to fail without its fix) + `--qa` harness (28 renders, 0 errors).
- ✅ **Batch D — UX guards & robustness (done, 1.4.4):** ✅ **D1** Ctrl+N/O/D and Ctrl+Z/Y respected the Busy gate the buttons already had — Ctrl+N mid-import used to clear the project while the background fill kept writing into the captured cards, then report success over an empty project. ✅ **D2** art pasted from a URL is applied to the face captured at paste time (`SetArt(face, path)`), not whatever is selected when the download finishes. ✅ **D3** `CardProject.IsFromNewerVersion` + a load-time dialog: a file from a newer Cardinator still opens (tolerant reads), but the user is told before saving over it drops what this build doesn't know. ✅ **D4** hand-edited `null` strings are coalesced on load (they used to NRE in IsPlaneswalker/IsSaga/IsLand); duplicate-import detection keys on the front half of a `A // B` name so a re-imported DFC is recognised; `{G/U/P}` two-colour Phyrexian and `{HW}` half symbols are no longer flagged as typos; `SetPackager` swaps the zip with an overwrite move instead of delete-then-move; the Scryfall-search add path applies the set profile like every other add path; Esc no longer discards a frame-design session while typing in a field; the dead `IsEditable` on the land-style combo is gone (the theme has no editable part). Tested by `RobustnessGuardTests` (12) + 1 `WindowSmokeTests`.
- ✅ **Batch E — test & docs hygiene (done, 1.4.5):** ✅ the suite is now **offline** — constructing a real window primed the Scryfall symbology on every run (hidden network I/O in CI, a fire-and-forget task outliving the test's STA thread, and symbol rendering that depended on run history); `SymbolService.SuppressPrime` is set by a module initializer (or `CARDINATOR_NO_NETWORK=1`). ✅ `GoldenTests` no longer self-baselines a missing baseline — a renamed/mistyped key used to pass green having compared nothing; it now fails and points at `CARDINATOR_UPDATE_GOLDEN=1`. ✅ `WorkflowLifecycleTests.FullyPopulated` throws on a property type it doesn't populate, so a future persisted field can't silently dodge the round-trip guarantee (it now covers `BackFace`, compared recursively). ✅ **A real legacy file is committed** at `tests/Cardinator.Tests/fixtures/legacy-1.0-set.cardinator` — a 1.0-era set, loaded field-by-field, re-saved without loss, and rendered even though one of its frames no longer exists (`LegacyFixtureTests`); inline JSON only covers shapes we remembered. ✅ The save pipeline's ORDER is now testable and tested: `ProjectWriter` owns localize → relativize → **backup the previous file** → atomic write, and a test proves the backup holds the OLD bytes (moving the backup after the write would have kept all tests green while destroying the rollback). ✅ Double-sided sheets are tested to print a DFC's **real** back rather than the generic one. ✅ `Arrow_AlsoDraws_OnBothFaces` now actually checks the back face.

**Docs:** screenshots regenerated at 1.4.5; Help/README/USER_GUIDE/QUICKSTART button names match the UI; the three stale fix-order markers corrected. Review plan: [REVIEW-2026-10-04.md](REVIEW-2026-10-04.md).

