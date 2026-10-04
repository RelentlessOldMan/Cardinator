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
