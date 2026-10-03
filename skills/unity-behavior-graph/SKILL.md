---
name: unity-behavior-graph
description: Reading, auditing, inspecting, debugging, building, or changing Unity Behavior (com.unity.behavior) graphs - Behavior Graph, BehaviorGraphAgent, BehaviorAuthoringGraph .asset files, blackboard variables, EventChannels, custom Action/Condition nodes, NPC / enemy AI, and the Animator states and transitions a graph drives (hit, death, attack, idle). Use for any build, audit or debug of enemy or NPC behavior built on Behavior graphs. Complements `unity-live-work` (live-scene rules) - load both whenever the task reads or changes the scene, prefabs, Animator or Play Mode.
license: MIT
metadata:
  title: Unity Behavior Graph
  author: Oberonru
  version: 1.0.0
  category: ai
  tags: "behavior, npc, ai, graph"
  kind: manual
---

# Unity Behavior graphs

Rules for scene work (look first, wait for compile, save, report) are in `unity-live-work`.
This skill covers only Behavior graphs. Facts here are for com.unity.behavior 1.0.x; check the
project's package version first (Packages/manifest.json, Library/PackageCache).

## Tools

Use these before Bash/raw file edits; fall back to `script_exec` only for what they can't do, and say so.

- Code recon: `find_class` (custom nodes, components), `get_class` / `get_file_content` (read node classes whole), `who_calls`, `get_class_deps`; `grep_compiler` for text like `[NodeDescription]`.
- Assets: `who_uses_asset` (path or guid of the graph `.asset`; reads disk, no Editor needed), `find_asset` (by name/type), `prefab_get` (the agent's prefab: `BehaviorGraphAgent` component and its fields).
- `animator_get_controller` — the paired controller: parameters, states, transitions.
- `animator_apply` — edit the paired controller. Use it instead of `script_exec` or YAML edits. It handles layers, sub-state machines (state path `Combat/Melee/Attack`) and state behaviours; fall back to `script_exec` only for sub-machine entry/exit transitions and 2D blend trees.
- **There is NO Behavior Graph tool yet.** Nothing in the bridge or the MCP server reads or edits a graph. Read the `.asset` by hand as in section 2 (`get_file_content` returns only an outline for a long file: pass `line_range`).
- `${skill_files}/scripts/dump-graph.cs` — run via `script_exec` to print the graph tree, blackboard and checks against the controller; read-only; set graphPath/controllerPath at the top.
- C# changes (nodes, components, channels): `code_write` (`.cs` only, does not wait for compile) + `scene_get_compile_status` + `scene_get_logs`.
- Graph changes: only as in section 5 — the Behavior editor (ask the user), or an editor script. Write the script with `code_write` under `Assets/Editor`, or run it once via `script_exec` (fallback; say so; result-checking rules are in `unity-live-work`). Then Validate All Graphs. `SaveAsset()` yourself.

## 1. First steps

- Find graphs: `Assets/**/*.asset` containing `BehaviorAuthoringGraph`, or `t:BehaviorAuthoringGraph` in the Project search.
- Find who runs them: prefabs/scenes with a `BehaviorGraphAgent` component (`prefab_get`, `who_uses_asset` on the graph asset).
- Take the Animator Controller of the same prefab (`animator_get_controller`) — the graph and the controller are one system.
- Find custom nodes: `find_class`, or grep `[NodeDescription]`. Read node classes and the components they call.
- Every agent instantiates its own runtime copy of the graph; the shared asset is not changed at runtime.

## 2. Reading a graph from the .asset YAML

Run `${skill_files}/scripts/dump-graph.cs` first; read the YAML only for what it doesn't cover. Until a dedicated graph tool exists, the .asset is read by hand. One file holds several objects:
runtime `BehaviorGraph` (first document, generated), `RuntimeBlackboardAsset`, the authoring
`BehaviorAuthoringGraph` (main object, source of truth), authoring blackboard, debug info.

- **Use the runtime `BehaviorGraph` part for flow.** Managed objects live in `references: RefIds:` as `rid: N` entries; `rid: -2` = null.
- Links: `m_Parent`, `m_Child` (Start and modifiers), `m_Children: [rid...]` (composites; list order = execution order).
- Class name = node meaning: `Start` = On Start (`Repeat`), `SequenceComposite` (stops at first Failure), `SelectorComposite` = Try In Order (first Success wins), `ParallelAllComposite` = Run In Parallel, `SwitchComposite` (enum var, one child per value), `RepeaterModifier`, `AbortModifier`, `RestartModifier`, `StartOnEvent`, `SetAnimatorBoolAction` / `Float` / `Int` / `Trigger`, `SetVariableValueAction`, `WaitAction`, `NavigateToTargetAction`, `Patrol`. Custom nodes appear with their own class, `ns`, `asm`.
- Parameters: a `BlackboardVariable<T>` field points to a rid. Literal = entry with GUID 0/0, empty `Name`, value in `m_Value` (e.g. `Parameter -> m_Value: IsWalk`). Linked = points at the blackboard variable entry (non-zero GUID, real `Name`). `Self` (GUID `m_Value0: 1`) is the agent's GameObject.
- Blackboard: `Blackboard.m_Variables` rid list -> GUID, Name, default value. `{fileID: 0}` = unassigned here; per-instance objects are overridden on the agent in the scene/prefab.
- Enums are stored as ints (values like 1/2/4). Map them through the enum source.
- Conditions live in `m_Conditions` on Abort / Restart / Guard nodes (`VariableComparisonCondition`, `VariableValueChangedCondition`, `CheckDistanceCondition`; operator is an int).
- Resolve every rid you rely on. Do not report a link you did not follow to the end.
- Authoring part (`m_Nodes`, `m_NodeModelsInfo`, port `m_Connections`) is for positions and broken types; edges are port connections, there is no edge list.
- **Verified vs inferred:** in the report, mark which facts you read in the YAML/code and which you concluded. Before saying a C# member is missing, read the whole class.

## 3. Architecture rules

- **Graph = decisions and sequencing. Components = execution** (movement, health, perception, animation drivers). Nodes call thin methods or read properties.
- **One writer per blackboard variable:** either the graph (`Set Variable Value`) or code (`agent.SetVariableValue`). Sensors write facts (Target, IsDead) or send events; the graph issues intents.
- Code that writes the blackboard by string: the name must match an existing variable and type; check the returned bool (false = missing, wrong type, edit mode, or agent not initialized).
- **Events over polling** for hit/death: EventChannel + `Start On Event Message` (Default ignores messages while busy; Restart restarts the branch; Once; Queue 1.0.10+) or `Wait for Event Message`. Code sends with `channel.SendEventMessage(...)`.
- **Interrupts:** wrap the behavior in `Abort` (e.g. `IsDead == true`) under Try In Order, or use Abort / Restart guards. Abort runs `OnEnd` of running children — put cleanup there.
- **Restart** re-runs the child every tick while its condition is true; the child never progresses. The condition must be edge-like or self-clearing.
- **Animator:** locomotion animation is driven by a component (NavMeshAgent velocity -> Animator floats) so it works from any branch. `SetAnimator*` nodes only for one-shots (Attack trigger, Die bool). Every bool set `true` needs a `false` path.
- **Waiting for an animation to end:** not a hardcoded `Wait` that copies the clip length. Use a custom Action that sets the trigger in `OnStart` and returns `Running` until a flag/event from an Animation Event, StateMachineBehaviour, or component arrives.
- Navigation nodes (`Patrol`, `NavigateToTarget/Location`) write the agent speed to the Animator param `AnimatorSpeedParam` (default `SpeedMagnitude`) and reset it to 0 in `OnEnd`. Match your controller's parameter or clear the field.
- Keep graphs small: many variables (~15-20+) -> split into subgraphs (Run Subgraph). Netcode: run the graph on the authority only.

## 4. Custom nodes

- `[Serializable, GeneratePropertyBag]`, `[NodeDescription(name, story, category, id)]`. `Action` clashes with `System.Action`: `using Action = Unity.Behavior.Action;`.
- Story `[Word]` becomes a link field; the field name must equal the word. Fields: `[SerializeReference] public BlackboardVariable<T> X;`.
- **`id` (32 hex chars) is the binding key. Never change it**; a new node needs a fresh unique id. When renaming or moving a class keep the `id`; then run Validate All Graphs and diff the .asset.
- Lifecycle: `OnStart` (default Running) -> `OnUpdate` each tick (**default returns Success**) -> `OnEnd` (always called, also on interruption). A node that overrides only `OnStart` finishes instantly.
- If an action takes time, return `Running` and finish in `OnUpdate`. Instant actions return Success/Failure.
- No `GetComponent` / `Find*` / LINQ / new collections / string concat per tick — cache in `OnStart`.
- Node objects live as long as the graph instance and are reused: reset all per-run state in `OnStart`, keep no static state, clean up in `OnEnd`. Do not call `EndNode` from `OnEnd`.
- `LogFailure("reason")` for diagnostics. The custom-node asmdef must reference `Unity.Behavior`.
- Renaming a C# type used by a blackboard variable can make the variable disappear (known in 1.0.11).

## 5. Changing graphs

**Never hand-edit rids, GUID pairs, ports, or structure in the YAML.** Two representations (authoring and generated runtime) must stay in sync; one bad rid breaks the graph and can fail the build.

- **(a) Ask the user to do it in the Behavior editor.** Give an exact step list: which node, where to attach, which fields and values, which variable to create.
- **(b) Editor script** in `Assets/Editor` (Assembly-CSharp-Editor has InternalsVisibleTo to the package). Load `AssetDatabase.LoadAssetAtPath<BehaviorAuthoringGraph>(path)`; use `CreateNode`, `ConnectEdge`, `DeleteNode`, then `BuildRuntimeGraph` / `RebuildGraphAndBlackboardRuntimeData()` and `SaveAsset()`. This API is internal, unofficial, and version-fragile: check the package version first, say so to the user, and do not claim it is verified end to end.
- The one tolerable YAML edit: a literal `m_Value` — in BOTH the authoring `LocalValue` entry and the matching runtime entry, with the graph closed in the editor. Then Validate All Graphs.
- After any change: `Tools > Behavior > Validate All Graphs` (or `BehaviorAuthoringGraphUtilities.ValidateAllGraphs()` from a script), read the Console, diff the .asset.
- After changing an enum used by a blackboard variable: Validate All Graphs and reopen the graph (before 1.0.16 enum changes are not tracked).
- Code changes (nodes, components, channels, enums) are normal C# edits; keep `[NodeDescription]` ids stable.

## 6. Pitfalls

| Symptom | Cause / fix |
|---|---|
| Animation never plays / never returns | `Parameter` string in `SetAnimator*` and `AnimatorSpeedParam` are not validated against the controller — compare names and types |
| Animator warns about missing `SpeedMagnitude` | Navigation nodes write it by default — add the param or clear the field |
| Node "does nothing", sequence races on | Default `OnUpdate` returns Success; a stub that overrides only `OnStart` (or only `OnUpdate` with an instant return) finishes instantly |
| Branch never progresses | `Restart` condition stays true every tick |
| Parent Sequence fails after interrupt | `Abort` returns Failure to its parent — put it under Try In Order or a Succeeder |
| Infinite Repeat burns a frame per loop | Child completes instantly |
| `SetVariableValue("Name", v)` has no effect | By-string call returned false: wrong name/type, edit mode, agent not initialized. Check the bool, or use the GUID overload |
| Vague node failure | An unassigned variable (null GameObject/Animator) — check the agent's overrides in the scene/prefab |
| "Apply to Prefab" fails on `BehaviorGraphAgent` | Known type-mismatch issue in 1.0.14-1.0.16; apply per variable or Overrides > Apply All |
| Missing types / broken nodes after rename | Type or `id` changed; editor windows close on managed-reference errors, build fails the pre-build check — restore, run Check Assets Integrity |
| `OnSetup`, `OnTeardown`, ObserverAbort not found | They exist only in newer packages (1.0.15-1.0.16+); check the version |
| Initially disabled agent misbehaves | Create it disabled and enable later, as the docs say |

## 7. Final checklist

- Every animator parameter string in the graph and in code exists in the controller, with the right type.
- Every blackboard variable name used from code exists in the graph, with a matching type.
- Each variable has exactly one writer.
- No node finishes instantly by accident; no Restart loop; every `true` flag has a matching `false`.
- Validate All Graphs was run and the Console is clean; the .asset diff contains only what you meant.
- Report what was NOT verified. Play Mode is the user's job if a project skill says the user tests in Play Mode themselves; tell them what to check: graph Debug toolbar -> pick the agent, watch node status.
