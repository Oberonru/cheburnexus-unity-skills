// dump-graph.cs - READ-ONLY dump of a Unity Behavior graph + checks against the Animator Controller.
// HOW: call the MCP tool `script_exec` and pass THIS WHOLE FILE as `code` (it is a method body, not a class).
// First edit the inputs right below. Keep the default sandbox ("loose"): the script uses System.Type.
// Saves nothing, marks nothing dirty. The output is the returned string = the `result` field of the reply.
// Also read `compile_errors`, `runtime_error` and `logs` of the reply.
// Unity.Behavior nodes and the runtime graph are `internal`, so everything is read by reflection by member name
// (written for com.unity.behavior 1.0.x). ASCII only.
using System.Reflection;
using System.Text;

string graphPath      = "Assets/Graphs/MyGraph.asset"; // REQUIRED: the Behavior graph .asset
string controllerPath = "";                             // optional: the .controller; empty = find it through a prefab (may be slow in a huge project)
int    maxChars       = 12000;                          // output cap; the tree is cut first, blackboard and checks are kept
int    maxDepth       = 40;                             // tree depth limit

var inv = System.Globalization.CultureInfo.InvariantCulture;
var bf  = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var bfd = bf | BindingFlags.DeclaredOnly;

var tb          = new StringBuilder();                 // tree text
var seen        = new List<object>();                  // visited nodes (cycle guard)
var usedVars    = new Dictionary<string, int>();       // blackboard var name -> reference count
var customTypes = new List<System.Type>();             // distinct custom (non Unity.Behavior assembly) node types
var animUse     = new List<string[]>();                // {node#, kind, param, mode, name-from-variable}
var nullFields  = new List<string>();                  // BlackboardVariable fields that are null
var animWarn    = new List<string>();                  // SetAnimator* nodes whose Animator is a literal null
int  nodeNo     = 0;
bool depthCut   = false;
bool dupSeen    = false;

// ---------------- reflection helpers ----------------
// Reads a field or property (public or not) by name, searching the type and all its base types.
bool TryM(object o, string name, out object val)
{
    val = null;
    if (o == null) return false;
    for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
    {
        try
        {
            var f = t.GetField(name, bfd);
            if (f != null) { try { val = f.GetValue(o); } catch { val = null; } return true; }
            var p = t.GetProperty(name, bfd);
            if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
            {
                try { val = p.GetValue(o, null); } catch { val = null; }
                return true;
            }
        }
        catch { }
    }
    return false;
}
object M(object o, string name) { object v; TryM(o, name, out v); return v; }

bool HasBase(System.Type t, string baseName)
{
    for (; t != null; t = t.BaseType) if (t.Name == baseName) return true;
    return false;
}
bool IsBBV(object o)  { return o != null && HasBase(o.GetType(), "BlackboardVariable"); }
bool IsNode(object o) { return o != null && HasBase(o.GetType(), "Node"); }
bool IsCustom(System.Type t) { return !t.Assembly.GetName().Name.StartsWith("Unity.Behavior"); }
bool Declares(System.Type t, string method)
{
    try { return t.GetMethod(method, bfd) != null; } catch { return false; }
}

string TN(System.Type t)
{
    if (t == null) return "?";
    if (t.IsArray) return TN(t.GetElementType()) + "[]";
    if (t.IsGenericType)
    {
        var n = t.Name; int i = n.IndexOf('`'); if (i > 0) n = n.Substring(0, i);
        var a = t.GetGenericArguments(); var parts = new string[a.Length];
        for (int k = 0; k < a.Length; k++) parts[k] = TN(a[k]);
        return n + "<" + string.Join(",", parts) + ">";
    }
    return t.Name;
}

List<FieldInfo> AllFields(object o)
{
    var res = new List<FieldInfo>();
    for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
        foreach (var f in t.GetFields(bfd)) if (!f.Name.StartsWith("<")) res.Add(f);
    return res;
}

// ---------------- value formatting ----------------
string Fmt(object val, System.Type t)
{
    if (val == null) return "null";
    var uo = val as UnityEngine.Object;
    if (uo != null) return "\"" + uo.name + "\"(" + val.GetType().Name + ")";
    if (val is string) return "\"" + (string)val + "\"";
    if (val is bool) return ((bool)val) ? "true" : "false";
    if (val is System.Enum) return val.ToString();
    if (t != null && t.IsEnum && (val is int || val is long))
    {
        try { return System.Enum.ToObject(t, val).ToString(); } catch { }
    }
    if (val is float) return ((float)val).ToString("0.####", inv);
    if (val is double) return ((double)val).ToString("0.####", inv);
    var list = val as System.Collections.IList;
    if (list != null)
    {
        var items = new List<string>();
        for (int i = 0; i < list.Count && i < 4; i++) items.Add(Fmt(list[i], null));
        return "[" + list.Count + (list.Count > 0 ? ": " + string.Join(",", items.ToArray()) + (list.Count > 4 ? ",..." : "") : "") + "]";
    }
    try { return Convert.ToString(val, inv); } catch { return val.GetType().Name; }
}

// A cast wrapper (GameObject <-> Component etc.) keeps the real variable in m_LinkedVariable.
object Resolve(object v, out bool viaCast)
{
    viaCast = false;
    for (int n = 0; n < 6 && v != null; n++)
    {
        object lk;
        if (TryM(v, "m_LinkedVariable", out lk) && lk != null) { v = lk; viaCast = true; } else break;
    }
    return v;
}

void Mark(string nm) { int c; usedVars.TryGetValue(nm, out c); usedVars[nm] = c + 1; }

// Literal -> its value; linked -> "->VarName" (counts as a use of that variable).
string Show(object raw)
{
    if (raw == null) return "<null>";
    if (!IsBBV(raw)) return Fmt(raw, null);
    bool cast; var v = Resolve(raw, out cast);
    var nm = M(v, "Name") as string;
    if (!string.IsNullOrEmpty(nm))
    {
        Mark(nm);
        var s = "->" + nm;
        if (cast) s += "(as " + TN(M(raw, "Type") as System.Type) + ")";
        return s;
    }
    return Fmt(M(v, "ObjectValue"), M(v, "Type") as System.Type);
}

// String value of a variable (literal, or the default of the linked one); via = linked var name or "".
string StrOf(object raw, out string via)
{
    via = "";
    if (raw == null) return null;
    bool cast; var v = Resolve(raw, out cast);
    var nm = M(v, "Name") as string;
    if (!string.IsNullOrEmpty(nm)) via = nm;
    var ov = M(v, "ObjectValue");
    return ov == null ? null : Convert.ToString(ov, inv);
}

// ---------------- tree walk ----------------
void Walk(object node, int depth, string label)
{
    var ind = new string(' ', depth * 2);
    if (depth > maxDepth) { depthCut = true; tb.Append(ind).Append("... (maxDepth reached)\n"); return; }
    foreach (var s in seen)
        if (object.ReferenceEquals(s, node)) { dupSeen = true; tb.Append(ind).Append(label).Append("(node already shown above - cycle or shared)\n"); return; }
    seen.Add(node);
    nodeNo++;
    int myNo = nodeNo;

    var nt = node.GetType();
    var tn = nt.Name;
    bool custom = IsCustom(nt);
    if (custom && !customTypes.Contains(nt)) customTypes.Add(nt);

    var parts = new List<string>();
    var fv = new Dictionary<string, object>();
    var conds = new List<object>();
    object subRoot = null;
    foreach (var f in AllFields(node))
    {
        object val; try { val = f.GetValue(node); } catch { continue; }
        var fn = f.Name;
        if (HasBase(f.FieldType, "BlackboardVariable"))
        {
            fv[fn] = val;
            if (val == null) nullFields.Add("#" + myNo + " " + tn + "." + fn);
            parts.Add(fn + "=" + Show(val));
        }
        else if (fn == "Repeat" && val is bool) parts.Add("Repeat=" + ((bool)val ? "true" : "false"));
        else if (fn == "m_RequiresAllConditions" && val is bool) parts.Add("requireAll=" + ((bool)val ? "true" : "false"));
        else if (val != null && fn != "Graph" && val.GetType().Name == "BehaviorGraphModule") subRoot = M(val, "Root");
        else if (val is System.Collections.IList)
        {
            foreach (var it in (System.Collections.IList)val)
                if (it != null && HasBase(it.GetType(), "Condition")) conds.Add(it);
        }
    }

    // --- collect facts for the checks ---
    if (tn.StartsWith("SetAnimator") && tn.EndsWith("Action"))
    {
        var kind = tn.Substring(11, tn.Length - 11 - 6); // Bool | Float | Int | Trigger
        object praw; fv.TryGetValue(kind == "Trigger" ? "Trigger" : "Parameter", out praw);
        string pvia; var pval = StrOf(praw, out pvia);
        string mode = "set";
        if (kind == "Bool")
        {
            object vraw; string vvia; fv.TryGetValue("Value", out vraw);
            var vs = StrOf(vraw, out vvia);
            mode = vvia != "" ? "dyn" : (vs == "True" ? "true" : (vs == "False" ? "false" : "dyn"));
        }
        else if (kind == "Trigger")
        {
            object vraw; string vvia; fv.TryGetValue("TriggerState", out vraw);
            var vs = StrOf(vraw, out vvia);
            mode = (vvia == "" && vs == "False") ? "reset" : "set";
        }
        animUse.Add(new string[] { "#" + myNo, kind, pval ?? "", mode, pvia });
        object araw;
        if (fv.TryGetValue("Animator", out araw) && araw != null)
        {
            bool ac2; var av = Resolve(araw, out ac2);
            var anm = M(av, "Name") as string;
            if (string.IsNullOrEmpty(anm) && M(av, "ObjectValue") == null)
                animWarn.Add("#" + myNo + " " + tn + ": Animator is a literal null (not linked to a blackboard variable) - the node fails with 'No Animator set'");
        }
    }
    object sraw;
    if (fv.TryGetValue("AnimatorSpeedParam", out sraw))
    {
        string svia; var sval = StrOf(sraw, out svia);
        animUse.Add(new string[] { "#" + myNo, "Speed", sval ?? "", "set", svia });
    }

    // --- print the node line ---
    var line = new StringBuilder();
    line.Append(ind).Append(label).Append('#').Append(myNo).Append(' ').Append(tn);
    if (custom) line.Append(" [CUSTOM ").Append(string.IsNullOrEmpty(nt.Namespace) ? "global ns" : nt.Namespace).Append(", ").Append(nt.Assembly.GetName().Name).Append(']');
    foreach (var p in parts) line.Append("  ").Append(p);
    tb.Append(line.ToString()).Append('\n');

    foreach (var c in conds)
    {
        var ct = c.GetType();
        var cl = new StringBuilder();
        cl.Append(ind).Append("    ? ").Append(ct.Name);
        if (IsCustom(ct)) cl.Append(" [CUSTOM ").Append(string.IsNullOrEmpty(ct.Namespace) ? "global ns" : ct.Namespace).Append(']');
        foreach (var cf in AllFields(c))
        {
            if (!HasBase(cf.FieldType, "BlackboardVariable")) continue;
            object cv; try { cv = cf.GetValue(c); } catch { continue; }
            if (cv == null) nullFields.Add("#" + myNo + " condition " + ct.Name + "." + cf.Name);
            cl.Append("  ").Append(cf.Name).Append('=').Append(Show(cv));
        }
        tb.Append(cl.ToString()).Append('\n');
    }

    // --- children ---
    bool isComposite = HasBase(nt, "Composite");
    bool isModifier  = HasBase(nt, "Modifier");
    if (isComposite)
    {
        var kids = M(node, "Children") as System.Collections.IList;
        if (kids == null) kids = M(node, "m_Children") as System.Collections.IList;
        string[] caseNames = null;
        if (kids != null && tn == "SwitchComposite")
        {
            bool sc; var ev = Resolve(M(node, "EnumVariable"), out sc);
            var et = M(ev, "Type") as System.Type;
            if (et != null && et.IsEnum)
            {
                var arr = System.Enum.GetValues(et);
                caseNames = new string[arr.Length];
                for (int i = 0; i < arr.Length; i++) caseNames[i] = arr.GetValue(i).ToString();
            }
        }
        if (kids != null)
        {
            for (int i = 0; i < kids.Count; i++)
            {
                var lab = (caseNames != null && i < caseNames.Length) ? "case " + caseNames[i] + ": " : "";
                if (!IsNode(kids[i])) { tb.Append(ind).Append("  ").Append(lab).Append("(no branch)\n"); continue; }
                Walk(kids[i], depth + 1, lab);
            }
        }
    }
    if (isModifier)
    {
        var one = M(node, "Child");
        if (one == null) one = M(node, "m_Child");
        if (IsNode(one)) Walk(one, depth + 1, "");
        else tb.Append(ind).Append("  (no child)\n");
    }
    if (IsNode(subRoot)) Walk(subRoot, depth + 1, "subgraph: ");
}

// ================= main =================
graphPath = (graphPath ?? "").Replace('\\', '/');
UnityEngine.Object graphObj = null, bbAsset = null, authoringObj = null;
int nullSubs = 0;
foreach (var o in AssetDatabase.LoadAllAssetsAtPath(graphPath))
{
    if (o == null) { nullSubs++; continue; }
    var fnm = o.GetType().FullName;
    if (fnm == "Unity.Behavior.BehaviorGraph") graphObj = o;
    else if (fnm == "Unity.Behavior.RuntimeBlackboardAsset") bbAsset = o;
    else if (fnm != null && fnm.EndsWith("BehaviorAuthoringGraph")) authoringObj = o;
}
if (graphObj == null)
{
    var sbe = new StringBuilder();
    sbe.Append("ERROR: no runtime BehaviorGraph object in '" + graphPath + "'. Set graphPath to a Behavior graph .asset.\n");
    if (nullSubs > 0) sbe.Append(nullSubs + " sub-object(s) failed to load (missing script / type) - the graph may be broken.\n");
    sbe.Append("Behavior graphs in the project:\n");
    var found = AssetDatabase.FindAssets("t:BehaviorAuthoringGraph");
    for (int i = 0; i < found.Length && i < 30; i++) sbe.Append("  " + AssetDatabase.GUIDToAssetPath(found[i]) + "\n");
    return sbe.ToString();
}

var rootMod = M(graphObj, "RootGraph");
var root = M(rootMod, "Root");

// ---- blackboard: the graph's own blackboard first, then the runtime blackboard asset ----
var bbSb = new StringBuilder();
var bbNames = new List<string>();
System.Collections.IList bbVars = null;
string bbSource = "none found";
var bbCands = new List<KeyValuePair<string, object>>();
bbCands.Add(new KeyValuePair<string, object>("graph", M(M(rootMod, "BlackboardReference"), "Blackboard")));
bbCands.Add(new KeyValuePair<string, object>("runtime blackboard asset", M(bbAsset, "Blackboard")));
foreach (var bc in bbCands)
{
    var vs = bc.Value != null ? (M(bc.Value, "Variables") ?? M(bc.Value, "m_Variables")) as System.Collections.IList : null;
    if (vs != null && vs.Count > 0) { bbVars = vs; bbSource = bc.Key; break; }
}
int bbCount = bbVars != null ? bbVars.Count : 0;
if (bbVars != null)
{
    foreach (var bv in bbVars)
    {
        if (bv == null) { bbSb.Append("  <null variable entry - broken type?>\n"); continue; }
        var nm = M(bv, "Name") as string ?? "?";
        bbNames.Add(nm);
        var shared = bv.GetType().Name.StartsWith("Shared") ? " (shared)" : "";
        bbSb.Append("  ").Append(nm).Append(" : ").Append(TN(M(bv, "Type") as System.Type)).Append(" = ")
            .Append(Fmt(M(bv, "ObjectValue"), M(bv, "Type") as System.Type)).Append(shared).Append('\n');
    }
}
var groupRefs = M(rootMod, "BlackboardGroupReferences") as System.Collections.IList;
if (groupRefs != null && groupRefs.Count > 0)
{
    bbSb.Append("  + " + groupRefs.Count + " shared blackboard group(s):\n");
    foreach (var gr in groupRefs)
    {
        var gvs = M(M(gr, "Blackboard"), "Variables") as System.Collections.IList;
        if (gvs == null) continue;
        foreach (var gv in gvs)
            if (gv != null) bbSb.Append("    [group] ").Append(M(gv, "Name") as string ?? "?").Append(" : ").Append(TN(M(gv, "Type") as System.Type)).Append('\n');
    }
}
bbSb.Append("  (defaults stored in the asset; per-agent overrides live on the BehaviorGraphAgent in the prefab/scene)\n");

// ---- tree ----
if (root != null) Walk(root, 0, "");
else tb.Append("  RootGraph.Root is null - the runtime graph was never built. Open the graph in the Behavior editor / run Tools > Behavior > Validate All Graphs.\n");

// ---- controller ----
var ck = new StringBuilder();
string ctrlNote = "";
string ctrlPath = (controllerPath ?? "").Replace('\\', '/');
if (ctrlPath == "")
{
    var cands = new List<string>();
    var ctrls = new List<string>();
    var pguids = AssetDatabase.FindAssets("t:Prefab", new string[] { "Assets" });
    for (int i = 0; i < pguids.Length && i < 20000; i++)
    {
        var pp = AssetDatabase.GUIDToAssetPath(pguids[i]);
        foreach (var d in AssetDatabase.GetDependencies(pp, false))
            if (string.Equals(d, graphPath, StringComparison.OrdinalIgnoreCase)) { cands.Add(pp); break; }
    }
    foreach (var pp in cands)
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(pp);
        if (go == null) continue;
        bool hasAgent = false;
        foreach (var comp in go.GetComponentsInChildren<Component>(true))
            if (comp != null && comp.GetType().Name == "BehaviorGraphAgent") hasAgent = true;
        if (!hasAgent) continue;
        foreach (var an in go.GetComponentsInChildren<Animator>(true))
        {
            RuntimeAnimatorController rc = an.runtimeAnimatorController;
            for (int hop = 0; hop < 4 && rc is AnimatorOverrideController; hop++)
            {
                var baseRc = ((AnimatorOverrideController)rc).runtimeAnimatorController;
                if (baseRc == null) break;
                rc = baseRc;
            }
            if (rc == null) continue;
            var cp = AssetDatabase.GetAssetPath(rc);
            if (cp != "" && !ctrls.Contains(cp)) { ctrls.Add(cp); if (ctrlNote == "") ctrlNote = "found via prefab " + pp; }
        }
    }
    if (ctrls.Count > 0)
    {
        ctrlPath = ctrls[0];
        if (ctrls.Count > 1) ctrlNote += "; OTHER candidates: " + string.Join(", ", ctrls.GetRange(1, ctrls.Count - 1).ToArray());
    }
    else ctrlNote = "not found via prefabs (" + cands.Count + " prefab(s) reference the graph, none with an Animator controller) - set controllerPath";
}
else ctrlNote = "given";

var ctrlParams = new Dictionary<string, string>();
bool haveCtrl = false;
if (ctrlPath != "")
{
    RuntimeAnimatorController rac = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ctrlPath);
    for (int hop = 0; hop < 4 && rac is AnimatorOverrideController; hop++)
    {
        var baseRc = ((AnimatorOverrideController)rac).runtimeAnimatorController;
        if (baseRc == null) break;
        rac = baseRc;
    }
    var ac = rac as UnityEditor.Animations.AnimatorController;
    if (ac != null)
    {
        haveCtrl = true;
        foreach (var cpar in ac.parameters) ctrlParams[cpar.name] = cpar.type.ToString();
    }
    else ctrlNote += " | ERROR: could not load '" + ctrlPath + "' as an AnimatorController";
}

// ---- checks ----
ck.Append("[a] animator parameters used by the graph vs the controller\n");
var setNames = new List<string>();
if (!haveCtrl) ck.Append("  SKIPPED (no controller): " + ctrlNote + "\n");
var groups = new Dictionary<string, List<string>>();   // "kind|param" -> node ids
var groupOrder = new List<string>();
foreach (var u in animUse)
{
    var key = u[1] + "|" + u[2];
    if (!groups.ContainsKey(key)) { groups[key] = new List<string>(); groupOrder.Add(key); }
    groups[key].Add(u[0] + (u[3] == "true" || u[3] == "false" ? "=" + u[3] : (u[3] == "reset" ? "=reset" : "")) + (u[4] != "" ? "(name from var " + u[4] + ")" : ""));
    if (u[2] != "" && !setNames.Contains(u[2])) setNames.Add(u[2]);
}
if (groupOrder.Count == 0) ck.Append("  the graph has no SetAnimator* nodes and no AnimatorSpeedParam fields\n");
foreach (var key in groupOrder)
{
    var kp = key.Split('|'); var kind = kp[0]; var prm = key.Substring(kind.Length + 1);
    var expect = kind == "Speed" ? "Float" : kind;
    var who = string.Join(" ", groups[key].ToArray());
    var what = (kind == "Speed" ? "AnimatorSpeedParam" : "SetAnimator" + kind) + " '" + prm + "'";
    if (prm == "")
    {
        ck.Append(kind == "Speed" ? "  info: " + what + " is empty - the speed is not written to the Animator (" + who + ")\n"
                                  : "  ERROR: " + what + " - empty parameter name (" + who + ")\n");
        continue;
    }
    if (!haveCtrl) { ck.Append("  ? " + what + " (" + who + ")\n"); continue; }
    string actual;
    if (ctrlParams.TryGetValue(prm, out actual))
    {
        if (actual == expect) ck.Append("  ok: " + what + " exists as " + actual + " (" + who + ")\n");
        else ck.Append("  ERROR: " + what + " - controller has it as " + actual + ", node needs " + expect + " (" + who + ")\n");
    }
    else
    {
        string near = null;
        foreach (var kv in ctrlParams) if (string.Equals(kv.Key, prm, StringComparison.OrdinalIgnoreCase)) near = kv.Key;
        ck.Append("  ERROR: " + what + " is NOT in the controller" + (near != null ? " (differs only by case from '" + near + "')" : "") + " (" + who + ")\n");
    }
}
foreach (var w in animWarn) ck.Append("  warn: " + w + "\n");

ck.Append("[b] bool parameters the graph sets only one way (code may still set the other way)\n");
bool anyB = false;
var boolNames = new List<string>();
foreach (var u in animUse) if (u[1] == "Bool" && u[2] != "" && !boolNames.Contains(u[2])) boolNames.Add(u[2]);
foreach (var bn in boolNames)
{
    bool hasT = false, hasF = false, hasD = false; var ids = new List<string>();
    foreach (var u in animUse)
        if (u[1] == "Bool" && u[2] == bn) { ids.Add(u[0]); if (u[3] == "true") hasT = true; else if (u[3] == "false") hasF = true; else hasD = true; }
    if (hasD) continue;
    if (hasT && !hasF) { anyB = true; ck.Append("  warn: '" + bn + "' is only ever set to TRUE by the graph (" + string.Join(" ", ids.ToArray()) + ") - no node resets it to false\n"); }
    if (hasF && !hasT) { anyB = true; ck.Append("  warn: '" + bn + "' is only ever set to FALSE by the graph (" + string.Join(" ", ids.ToArray()) + ") - no node sets it to true\n"); }
}
if (!anyB) ck.Append("  none\n");

ck.Append("[c] blackboard variables not referenced by any node or condition\n");
var unused = new List<string>();
foreach (var nm in bbNames) if (nm != "Self" && !usedVars.ContainsKey(nm)) unused.Add(nm);
ck.Append(unused.Count == 0 ? "  none\n" : "  " + string.Join(", ", unused.ToArray()) + "  (code may still use them via agent.SetVariableValue/GetVariable; a subgraph is not followed by name)\n");

ck.Append("[d] custom Action classes that declare OnStart but not OnUpdate (default OnUpdate returns Success: may finish instantly)\n");
bool anyD = false;
foreach (var ct in customTypes)
{
    if (!HasBase(ct, "Action")) continue;
    bool st = false, up = false;
    for (var t2 = ct; t2 != null && IsCustom(t2); t2 = t2.BaseType)
    {
        if (Declares(t2, "OnStart")) st = true;
        if (Declares(t2, "OnUpdate")) up = true;
    }
    if (st && !up) { anyD = true; ck.Append("  " + ct.FullName + " - OnStart only: check it is meant to be instant\n"); }
}
if (!anyD) ck.Append("  none\n");

ck.Append("[e] controller parameters the graph never sets (info; code may set them)\n");
if (!haveCtrl) ck.Append("  SKIPPED\n");
else
{
    var untouched = new List<string>();
    foreach (var kv in ctrlParams) if (!setNames.Contains(kv.Key)) untouched.Add(kv.Key + ":" + kv.Value);
    ck.Append(untouched.Count == 0 ? "  none\n" : "  " + string.Join(", ", untouched.ToArray()) + "\n");
}

ck.Append("[f] BlackboardVariable fields that are null (the node will throw or fail)\n");
if (nullFields.Count == 0) ck.Append("  none\n");
for (int i = 0; i < nullFields.Count && i < 15; i++) ck.Append("  " + nullFields[i] + "\n");
if (nullFields.Count > 15) ck.Append("  ... +" + (nullFields.Count - 15) + " more\n");

// ---- assemble, cap ----
var head = new StringBuilder();
head.Append("== Behavior graph dump ==\n");
head.Append("graph: " + graphPath + "  (authoring graph: " + (authoringObj != null ? "yes" : "no") + ", blackboard asset: " + (bbAsset != null ? "yes" : "no") + ")\n");
head.Append("controller: " + (ctrlPath == "" ? "none" : ctrlPath) + "  [" + ctrlNote + "]" + (haveCtrl ? "  params: " + ctrlParams.Count : "") + "\n");
if (haveCtrl)
{
    var cl2 = new List<string>();
    foreach (var kv in ctrlParams) cl2.Add(kv.Key + ":" + kv.Value);
    head.Append("controller params: " + string.Join(", ", cl2.ToArray()) + "\n");
}
head.Append("nodes: " + nodeNo + ", custom node types: " + customTypes.Count);
if (nullSubs > 0) head.Append(", WARNING " + nullSubs + " sub-object(s) failed to load");
if (depthCut) head.Append(", WARNING tree cut at maxDepth");
if (dupSeen) head.Append(", WARNING a node was reached twice");
head.Append("\n");

var sec1 = "\n== Blackboard (" + bbCount + ", from " + bbSource + ") ==\n" + bbSb.ToString();
var sec3 = "\n== Checks ==\n" + ck.ToString();
var treeTxt = tb.ToString();
var sec2 = "\n== Tree (#n = node number used in Checks; [CUSTOM] = not from Unity.Behavior; ? = condition) ==\n" + treeTxt;
int budget = maxChars - head.Length - sec1.Length - sec3.Length;
if (budget < 1500) budget = 1500;
if (sec2.Length > budget) sec2 = sec2.Substring(0, budget) + "\n... tree truncated (" + treeTxt.Length + " chars), raise maxChars\n";
var report = head.ToString() + sec1 + sec2 + sec3;
if (report.Length > maxChars + 2500) report = report.Substring(0, maxChars + 2500) + "\n... output truncated\n";
return report;
