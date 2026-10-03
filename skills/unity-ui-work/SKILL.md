---
name: unity-ui-work
description: Building, fixing, auditing, inspecting, or debugging UI in a live Unity project via the ${product_short} MCP bridge - Canvas, RectTransform, buttons, text, layouts, scroll views, UI Toolkit (UIDocument/UXML). Use for any task that creates, arranges, reviews or debugs UI. Complements `unity-live-work` (general live-scene rules: inspection order, verifying, compile polling) - load both; this skill only adds UI specifics.
license: MIT
metadata:
  title: Unity UI Work
  author: Oberonru
  version: 1.0.0
  category: ui
  tags: "ui, canvas, ui-toolkit, rect-transform"
  kind: auto
---

# UI work in a live Unity scene

General rules (look first, wait for compile, save, verify, report) are in `unity-live-work`.
This skill adds only what is specific to UI.

## Tools

Use these before Bash/raw file edits; fall back to `script_exec` only for what they can't do, and say so.

- Find and read: `scene_list_objects`, `scene_find_components` (e.g. `ScrollRect`, `HorizontalLayoutGroup`), `scene_get_object` (serialized fields). For a `.prefab`: `prefab_get` (flat hierarchy with component types; pass `path` for one object's fields).
- Edit the scene: `scene_create_object` (`parent_path`, `components`, `rect`), `scene_add_component`, `scene_set_property` (Image color/sprite via `{"asset_path": ...}`, layout flags, `CanvasGroup`), `scene_set_transform` (RectTransform: `anchor_min`/`anchor_max`, `pivot`, `anchored_position`, `size_delta`; values are absolute), `scene_reparent`, `scene_remove_component`, `scene_delete_object`, `scene_save_scene`. Blocked in Play Mode. No `onClick`/UnityEvent wiring tool: that goes through `script_exec`.
- Edit a prefab asset: `prefab_batch` (several ops, one save), `prefab_set_transform`, `prefab_set_property`. `prefab_batch` also has `add_component` / `remove_component` ops (in order, so `add_component` then `property` works). It also has `create` / `delete` / `reparent` ops for objects. Call `prefab_get` first (watch its shared-prefab `_notice`). To make a NEW prefab use `prefab_create` (`ui: true` gives a RectTransform root; `ops` are the same as in `prefab_batch`); it refuses an existing path. `prefab_batch` is all-or-nothing: if any op fails nothing is saved.
- Check the result: `scene_inspect` (fields + computed rect + layout in one call), `scene_get_runtime_rect` / `scene_get_hierarchy_runtime` (`path` required, depth up to 10) for the size Unity really computed, `scene_get_layout_info`, `scene_diagnose_object` (`can_be_clicked` with the blocking layer, `effective_alpha`, `is_visible`), `scene_analyze_anchor_issues` (static; run first), `scene_analyze_responsive_layout` (per device; `include_snapshots` only when needed), `scene_get_snapshot` (`look_at` zooms into one object), `scene_get_logs`.
- UI Toolkit (`.uxml`/`.uss`) has no dedicated tool: edit the files, then `assets_refresh`; read `UIDocument` fields with `scene_get_object`.
- `script_exec` is the fallback; result-checking rules are in `unity-live-work`. It saves nothing: save the scene afterwards.

## 1. Approach

- **Detect the UI system first.** `.uxml` files / `UIDocument` = UI Toolkit; `Canvas` /
  `RectTransform` = uGUI. Follow what the project uses; if it has neither, default to uGUI.
- **One change at a time, then verify.** Don't destroy and recreate objects to fix them — that
  cascades into null references. If a fix didn't work, revert it before trying the next one.
- No new scripts unless asked. A "working UI" means a valid hierarchy, not extra code.
- In generated code use fully qualified `UnityEngine.UI.Image` / `UnityEngine.UI.Button` (they
  collide with other `Image`/`Button` types).
- Honor exact user values (hex colors, pixel sizes) — don't round or "improve" them.

## 2. Interaction readiness (uGUI)

- Exactly one `EventSystem` in the scene (with an input module that matches the project's input system).
- `GraphicRaycaster` on the Canvas.
- Raycast Target on for interactive graphics, off for decoration (backgrounds, icons, labels) —
  a decorative full-screen Image with Raycast Target on silently blocks every button under it.

## 3. RectTransform and visibility

- Set in this order: anchor preset, then pivot (match the anchor), then position/size.
- An element is visible only if: size > 0, inside its parent, not covered by a sibling drawn later,
  alpha > 0 (also check any `CanvasGroup` above it), and the object and its parents are active.

## 4. Layouts

- A Layout Group with Control Child Size on overrides the children's own sizes — set sizes through
  `LayoutElement` instead.
- Don't put `ContentSizeFitter` on the same object as a Layout Group that controls its size — they conflict.
- ScrollRect structure: `ScrollView > Viewport (Mask + Image) > Content (VerticalLayoutGroup +
  ContentSizeFitter)`. Clear Content before repopulating it.
- `CanvasScaler`: use Scale With Screen Size. If it is on Constant Pixel Size, flag it to the user
  before changing it.

## 5. Text

- Use TextMeshPro for all text.
- TMP trap: never run the menu item `Window/TextMeshPro/Import TMP Essential Resources` — its modal
  dialog blocks the main thread. Use `script_exec` with
  `TMPro.TMP_PackageResourceImporter.ImportResources(true, false, false)`.

## 6. Verify like a user

- Enter Play Mode, take a `scene_get_snapshot` and look at it, click the control (or call the
  button's onClick path the way the game will), then check the Console for errors.
- Test at least one different screen size/aspect if the layout is meant to scale.
- Save the scene and report anything not checked.
