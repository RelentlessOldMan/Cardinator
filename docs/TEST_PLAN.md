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
- ⚠️ **Gap:** there's no set-level home for "this set's code/symbol/numbering/frame." It's reconstructed
  by applying fields to all cards. Re-opening a set and remembering "what was the house style?" relies on
  inspecting a card. A lightweight **set-profile** (saved in the project file) would make identity
  first-class and let new cards inherit it automatically.

### W2 — Build the set (get cards in)
Entry paths, often mixed within one set:
- **Blank card → fill by hand** (original designs).
- **Recreate real cards** (name + Ctrl+L, or details → Fill from Scryfall).
- **Import list / CSV** (names, or name+art+frame columns).
- **Scryfall search import** (e.g. `t:dragon c:r`) to seed a themed batch with real art.

Review: ✅ The multiple on-ramps are a strength. ⚠️ Mixing them means **inconsistent state** mid-build
(some cards framed, some not; some numbered, some not). The consistency pass (W4) is what reconciles it —
so the intended rhythm is **build loosely → normalize in a pass**, which should be made obvious in docs.

### W3 — Art direction across a set
- Per-card art (Change art / paste / drag), or **Match art folder…** to bind a folder of images by
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

Review: ✅ These three are exactly the right tools. ⚠️ CHECKS is **per selected card**; there's no
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
  preview **click (focus)** first; tooltip-only discoverability. ⚠️ No **reset-position** control.
- **B6 Replace existing art.** ✅ SetArt resets pan/zoom — confirm that's desired vs. keeping framing.

### C. Frames
- **C1 Pick (per-card) / C2 Design+Apply / C3 Design→Save as new / C4 Edit built-in (guarded) / C5 Import
  PNG·.cardframe·.zip·URL / C6 Export bundle / C7 Delete (user only).**
- ✅ C3/C4/C7 close this pass's gaps (save-as-new doesn't mutate original; delete protects built-ins).
- ⚠️ C5: a **user-made zip with multiple template folders imports only the first** (one template per
  file). Fine for shipped bundles; note it.

### D. Sets / batch
- **D1 Import list/CSV / D2 Scryfall search import / D3 Match art folder / D4 Look up missing / D5 Number
  cards / D6 Set fields on all / D7 reorder·duplicate / D8 Export all·Print sheet (+backs).**
- ✅ Mixed frames + bulk re-frame both work; D6 "blank = unchanged" is right.
- 🐞 **D8 Export all** — confirm it defaults to the set's `out/` (Export PNG & Print sheet do now).

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
- ⚠️ **G6 Double-faced card via details search fills only the FRONT** (the main-page Ctrl+L path adds the
  back as a second card; details search does not). Decide if that asymmetry is intended.

---

## Part 3 — Top friction / open questions (action-level, ranked)
1. **Two lookup paths (A2/G6):** Ctrl+L (own name, +art, +DFC back) vs details Fill-from-Scryfall
   (details only). Clarify the story; maybe retire Ctrl+L or let details-search optionally fetch art/back.
2. **Export-all output dir (D8):** verify it honors the set `out/`.
3. **Keyboard art control discoverability (B5):** arrows/± need a preview click; only a tooltip hints it.
4. **Multi-template zip import (C5):** one-per-file; note or support multi.
5. **No reset-art-position control (B5/B6).**
6. **Replace-art resets framing (B6):** confirm intended.

*(The higher-impact, set-level gaps are ranked separately at the end of Part 1.)*

---

## Part 4 — Deep-review findings (multi-angle audit)

A three-angle audit (single-card, set-building, portability/transfer) against the code surfaced the
following, de-duplicated and ranked. 🐞 = confirmed bug · ⚠️ = gap/ease · each has a code-grounded cause.
These drive the fix backlog.

### High
- **H1 🐞 Set-symbol image doesn't travel with a set.** `SaveProject`/`LocalizeArtInto`/`CloneWithRelativeArt`
  only handle `ArtPath`; `CardModel.SetSymbolPath` is saved as a raw absolute path and never
  copied/relativized/resolved. Move/zip a set → every card's custom set symbol breaks (silently falls back
  to the drawn pip). *(Both set & transfer reviewers.)*
- **H2 🐞 Missing frame silently renders the WRONG frame.** `BatchService.ExportAll` and the live preview
  resolve an unknown `TemplateName` to `templates[0]` with no warning. Open a received project referencing a
  frame you lack → cards export with an arbitrary installed frame. (Compounds W5 "frames don't travel.")
- **H3 ⚠️ No editing UI for whole card types.** Adventure, Subtitle, Land-symbol override, and Layout are in
  `CardModel`/`CardRenderer` but have no `DetailsWindow` fields — only reachable via import or hand-edited
  JSON. A from-scratch author can't make an adventure/subtitle card in the GUI.
- **H4 ⚠️ Planeswalker/Saga/Class have no authoring guidance or validation.** Layout is chosen from
  type-line substrings and parsed from rules-text syntax (`+1:`, `I, II —`, `{cost}: Level N`); wrong
  formatting silently yields empty/garbled badges, no CHECK.

### Medium
- **M1 🐞 Export all ignores the set `out/` and doesn't record where it went.** `OnExportAll` has no
  `InitialDirectory` and never sets `_lastExportDir` (unlike Export PNG / Print sheet). *(Confirms Part-3 #2.)*
- **M2 🐞 "Number cards" destroys real/imported collector numbers.** `OnNumberCards` overwrites every card's
  `CollectorNumber` unconditionally — no skip-if-set, no confirm — clobbering printing hints captured on import.
- **M3 🐞 Duplicate-collector check defeated by mixed formats.** `CardValidator` compares the raw string, so
  `5` vs `005` vs `005/20` never collide — mixed hand/auto numbering hides real duplicates (the one cross-card
  check W4 leans on).
- **M4 🐞 `DefaultTemplate` is saved but never applied on load.** `LoadProjectFile` ignores
  `project.DefaultTemplate`; new blank cards inherit the alphabetically-first installed frame, not the set's.
- **M5 🐞 Live CHECKS never runs `RenderInspector`.** `UpdateValidation` calls only `CardValidator`; the
  `art-blank`/border pixel checks run only in the QA harness/tests. A corrupt-but-existing art file renders a
  blank window yet shows "✓ No issues". *(Corrects this plan's earlier G1 claim.)*
- **M6 🐞 Ctrl+L overwrites/blanks hand-typed fields.** Main-window lookup assigns every text field from the
  face unconditionally; a vanilla result blanks a user-typed Loyalty/P-T. (Art & frame are preserved; text isn't.)
- **M7 ⚠️ Edit details… has no Cancel/discard.** Binds the live card directly; "Done"/Esc both keep edits —
  no way to back out a details session.
- **M8 🐞 Stray Loyalty hides P/T.** `IsPlaneswalker` is true if TypeLine contains "Planeswalker" OR Loyalty
  is non-blank, so a leftover Loyalty makes a creature render as a planeswalker (P/T box vanishes), no CHECK.
- **M9 🐞 DFC back faces dropped on more paths.** Beyond details-search (G6): Scryfall-search import adds
  front-only, and duplicate/print-sheet treat a real back face as just another front (generic back on sheets).
- **M10 ⚠️ `.cardframe` dropped on the window is rejected.** `OnWindowDrop` handles projects/lists/images but
  not frame bundles — the core collaboration artifact can't be installed by drag-drop.
- **M11 🐞 Cross-drive / failed art copy stays absolute silently.** If `LocalizeArtInto`'s copy throws it
  keeps the original absolute path and Save still "succeeds" — that card won't travel, with no warning.
- **M12 ⚠️ Two sets in one folder: Export-all PNGs collide.** `BatchService.ExportAll` writes deterministic
  `NNN_slug.png` into the shared `out/`, so a second set overwrites the first's exports (extends W8).
- **M13 ⚠️ No set-symbol missing CHECK / validator symbol regex false positives.** `RenderInspector` doesn't
  check `SetSymbolPath`; `CardValidator.KnownSymbol` is narrower than what renders (verify token list).
- **M14 ⚠️ Bulk "Set fields on all" can't target a selection** (always all cards) and omits collector /
  land-symbol / layout — limits per-faction set work.

### Low
- ✅ L1 Clear-art doesn't reset pan/zoom (asymmetric with Change-art). · ✅ L2 Fuzzy art-match mis-binds
  prefix/substring names silently, no report *(now `MatchIntoWithReport` splits exact vs fuzzy and the UI
  lists the guesses for review)*. · ✅ L3 Re-importing the same list appends duplicates *(import now detects names already in the project
  and offers Skip-duplicates / Add-anyway / Cancel via `CountNamesAlreadyIn` / `RemoveNamesAlreadyIn`)*. ·
  ✅ L4 No duplicate-card-NAME check. · ✅ L5 Import silently drops empty/unparseable lines
  *(now `ParseWithReport` returns a skipped count surfaced in the import status)*. · L6 Collector width is `1/9`
  not `001/9` below 10 cards (doc mismatch). · ✅ L7 Single Export has no in-progress guard during a background
  Export-all *(OnExport now bails while Busy)*. · ✅ L8 Missing whole `art/` on load gives no
  aggregate warning (only per-card). · ✅ L9 Corrupt project shows raw parser message, no backup/restore *(load failure now copies the file to a
  `.corrupt-backup` sibling via `IoUtil.BackupCorrupt` and shows a friendly dialog instead of a raw error)*. ·
  ✅ L10 Deleting a frame still referenced by other cards leaves them pointing at a missing template
  *(delete confirm now reports how many cards use it via `TemplateService.CountReferencing`)*. · ✅ L11
  Import of a bare frame can't opt into full-art regions *(import now asks Full-art / Standard and passes the
  flag to `TemplateImporter.CreateFromFile` / `CreateFromUrlAsync`)*.

**Only L6 remains open in the Low tier** — a cosmetic collector-width note; the behaviour (pad width tracks
the set total) is intended and the User Guide already describes it accurately.

### Fix order (each change ships with unit tests + coverlet)
1. ✅ **Portability batch (done, `f919490`):** H1 set-symbol travels, M11 surface non-localized art, M4 apply DefaultTemplate + inherit. *(+ M1 Export-all → `out/` + last-dir, M3 dup-collector normalize)*
2. **Batch-correctness:** ✅ M1, ✅ M3, ✅ M2 Number-cards guard, ✅ M12 save-time warning when a folder already holds another project.
3. ✅ **Validation surfacing (done):** ✅ H2 missing-frame CHECK (+ wired live), ✅ M13 set-symbol CHECK, ✅ M8 stray-Loyalty CHECK · ☐ M5 live RenderInspector (pixel checks) still pending.
4. **Authoring depth:** ✅ M7 details Cancel, ✅ M6 non-destructive Ctrl+L, ✅ H3 card-type editing UI, ✅ H4 layout authoring guidance (CHECK flags unparseable PW/Saga/Class syntax, reusing the renderer's own parsers) · ☐ M9 DFC handling.
5. **Big-ticket:** ✅ frames travel with a set — "Share set + frames" zip (`SetPackager`), ✅ whole-set validation report (W4 — "Check all cards" via `SetValidator`) · ☐ set profile (W1), multi-set manager (W8).
6. **Low batch:** ✅ L1 clear-art resets framing, ✅ L4 duplicate-name CHECK, ✅ L8 load-time missing-art summary, ✅ L2 fuzzy art-match review report, ✅ L5 import skipped-line count, ✅ L7 single-Export in-progress guard, ✅ L10 delete-frame-in-use warning, ✅ L3 re-import duplicate prompt, ✅ L9 corrupt-project backup + friendly dialog, ✅ L11 full-art option on bare-frame import.

### Agreed roadmap (post-1.1.0)
- ✅ **Backward compatibility (done, 1.1.2):** one shared `JsonCompat` options for all persisted types — case-insensitive keys, ignore unknown properties, tolerate comments/trailing-commas/quoted-numbers, null-guarded loads; `FormatVersion` stamp on projects. Golden "old format" load tests guard it (`BackwardCompatTests`). **Rule:** only ADD optional properties with defaults; never rename/retype an existing one.
- ☐ **Automated backups (next):** keep user data safe across bugs/upgrades — snapshot the project on save (rolling, timestamped copies) so a bad write/upgrade can be rolled back; complements the existing atomic-write + corrupt-project `.corrupt-backup`.
- ✅ **W1 set profile (done, 1.1.1):** `SetProfile` on the project (set code / rarity / copyright / artist / symbol) + default frame; a **Set defaults…** dialog; new/imported cards inherit blanks; optional apply-to-existing; persisted + back-compat (old files without a profile load fine).
- ☐ **Multi-sets (W8): will NOT build** — a set is already a self-contained folder; user switches via Save/Open one at a time. Decision: keep the save/load switch safe (unsaved-changes prompt already fires); no manager/recent-list UI.
- ☐ **M9 double-faced cards (big, after W1):** model a card as one entity with a **front + back face** and a **Flip** toggle in preview/edit; export both sides; print-sheet pairs the real back. Face indicators drawn by us (no WotC assets): **generic flip arrow AND sun/moon (transform)** both selectable per card. Import of a real DFC fills both faces into one card instead of two.
- ☐ **Horizontal / landscape cards (future, note):** Battles/Sieges, Planes, Schemes use a rotated canvas (≈1050×750). Needs a template **orientation** flag + rotated region layout. **Intersects DFC:** MOM Battle cards are double-faced (horizontal Siege front → vertical creature back), so the two faces can differ in orientation — design DFC's face model to allow a per-face canvas size so this drops in later.
- ☐ **M5 live RenderInspector** (pixel checks in the live CHECKS panel) and ☐ **M14 bulk-edit on a selection** — fold in around W1.

