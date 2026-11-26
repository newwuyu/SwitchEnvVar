using System;
using System.Collections.Generic;
using System.Linq;

namespace SwitchEnv
{
    // ActiveGroups is ordered from lowest to highest priority. Entries are the
    // last applied definitions, independent of edits still waiting to be applied.
    public static class GroupState
    {
        public static List<Lease> Copy(IEnumerable<Lease> groups) { return groups.Select(g => g.Copy()).ToList(); }
        public static List<Lease> FromLegacy(Lease active)
        {
            var groups = active == null ? new List<Lease>() : new List<Lease> { active.Copy() };
            foreach (var g in groups) NormalizeLease(g);
            return groups;
        }
        static void NormalizeLease(Lease g)
        {
            if (g == null || String.IsNullOrWhiteSpace(g.ProfileId) || g.Original == null || g.Applied == null) throw new Exception("配置组的恢复信息损坏。");
            if (g.Entries == null) g.Entries = g.Applied.Select(v => new Entry { Name = v.Name, Scope = v.Scope, Kind = v.Kind, Value = v.Value ?? "", Mode = v.Exists ? "Set" : "Delete" }).ToList();
            if (g.Entries.Count != g.Original.Count || g.Entries.Count != g.Applied.Count || g.Entries.Select(e => e.Key).Distinct().Count() != g.Entries.Count ||
                !new HashSet<string>(g.Entries.Select(e => e.Key)).SetEquals(g.Original.Select(v => v.Key)) || !new HashSet<string>(g.Entries.Select(e => e.Key)).SetEquals(g.Applied.Select(v => v.Key))) throw new Exception("配置组变量的备份不完整。");
            foreach (var e in g.Entries) Planner.Validate(e);
        }
        public static void NormalizeHistory(History h)
        {
            if (h.BeforeGroups == null) h.BeforeGroups = FromLegacy(h.BeforeLease);
            if (h.AfterGroups == null) h.AfterGroups = FromLegacy(h.AfterLease);
            foreach (var g in h.BeforeGroups.Concat(h.AfterGroups)) NormalizeLease(g);
        }
        public static void Normalize(State state)
        {
            if (state.Version != 1 && state.Version != 2) throw new Exception("数据格式不受支持。");
            if (state.ActiveGroups == null || state.Version == 1) state.ActiveGroups = FromLegacy(state.Active);
            var ids = new HashSet<string>();
            foreach (var g in state.ActiveGroups) { NormalizeLease(g); if (!ids.Add(g.ProfileId)) throw new Exception("启用的配置组有重复 ID。"); var profile = state.Profiles.FirstOrDefault(p => p.Id == g.ProfileId); if (profile != null) g.ProfileName = profile.Name; }
            foreach (var h in state.History) NormalizeHistory(h);
            state.Version = 2;
            // Kept only for the version-1 planner adapter and old snapshots.
            state.Active = state.ActiveGroups.Count == 1 ? state.ActiveGroups[0].Copy() : null;
        }
        public static void Assign(State state, IEnumerable<Lease> groups) { state.ActiveGroups = Copy(groups); foreach (var g in state.ActiveGroups) { var p = state.Profiles.FirstOrDefault(v => v.Id == g.ProfileId); if (p != null) g.ProfileName = p.Name; } state.Version = 2; state.Active = state.ActiveGroups.Count == 1 ? state.ActiveGroups[0].Copy() : null; }
        public static bool Enabled(State state, string id) { return state.ActiveGroups.Any(g => g.ProfileId == id); }
        public static Dictionary<string, Variable> Effective(IEnumerable<Lease> groups)
        {
            var result = new Dictionary<string, Variable>();
            foreach (var g in groups) foreach (var v in g.Applied) result[v.Key] = v.Copy();
            return result;
        }
        public static List<Lease> Owners(IEnumerable<Lease> groups, string key) { return groups.Where(g => g.Entries.Any(e => e.Key == key)).ToList(); }
        public static Snapshot Foundation(Snapshot current, IEnumerable<Lease> groups)
        {
            var foundation = current.Copy(); var seen = new HashSet<string>();
            foreach (var g in groups) foreach (var v in g.Original) if (seen.Add(v.Key)) foundation.Put(v);
            return foundation;
        }
        public static bool Equal(IEnumerable<Lease> first, IEnumerable<Lease> second)
        {
            var a = first.ToList(); var b = second.ToList(); if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].ProfileId != b[i].ProfileId || a[i].Entries.Count != b[i].Entries.Count) return false;
                if (!a[i].Entries.Zip(b[i].Entries, (x, y) => x.SameOperation(y)).All(v => v)) return false;
                if (a[i].Original.Count != b[i].Original.Count || a[i].Applied.Count != b[i].Applied.Count) return false;
                if (!a[i].Original.Zip(b[i].Original, (x, y) => x.Key == y.Key && Variable.Equal(x, y)).All(v => v) || !a[i].Applied.Zip(b[i].Applied, (x, y) => x.Key == y.Key && Variable.Equal(x, y)).All(v => v)) return false;
            }
            return true;
        }
    }

    public static class GroupPlanner
    {
        public static Variable Resolve(Variable baseline, Entry entry)
        {
            Planner.Validate(entry);
            if (baseline.Exists && baseline.Kind != 1 && baseline.Kind != 2) throw new Exception(baseline.Name + " 不是字符串变量，无法安全修改。");
            var value = new Variable { Name = baseline.Exists ? baseline.Name : entry.Name, Scope = entry.Scope, Kind = entry.Kind, Exists = entry.Mode != "Delete", Value = entry.Value };
            if (entry.Mode == "Prepend" || entry.Mode == "Append")
            {
                value.Value = Planner.MergePath(baseline.Exists ? baseline.Value : "", entry.Value, entry.Mode == "Prepend");
                value.Kind = baseline.Kind == 2 || entry.Kind == 2 ? 2 : 1;
            }
            if (value.Value.Length > 32766) throw new Exception(entry.Name + " 合并后的值过长。");
            return value;
        }
        public static Lease Definition(Profile p)
        {
            if (p.Entries.Count == 0) throw new Exception("配置组还没有变量。先添加变量，再启用。");
            if (p.Entries.Select(e => e.Key).Distinct().Count() != p.Entries.Count) throw new Exception("同一范围内有重复变量。");
            foreach (var e in p.Entries) Planner.Validate(e);
            return new Lease { ProfileId = p.Id, ProfileName = p.Name, Entries = p.Entries.Select(e => e.Copy(false)).ToList(), Original = new List<Variable>(), Applied = new List<Variable>() };
        }
        public static Plan Reconcile(Snapshot current, State state, List<Lease> definitions, IEnumerable<string> keys, string label, bool keepExternal, Snapshot foundation = null, IEnumerable<Variable> touchedValues = null)
        {
            var before = GroupState.Copy(state.ActiveGroups); var working = foundation == null ? GroupState.Foundation(current, before) : foundation.Copy();
            var touched = new HashSet<string>(keys); var oldValues = GroupState.Effective(before); var templates = new Dictionary<string, Variable>();
            foreach (var g in before) foreach (var v in g.Applied) templates[v.Key] = v;
            foreach (var g in definitions) foreach (var e in g.Entries) templates[e.Key] = Variable.Missing(e.Name, e.Scope);
            if (touchedValues != null) foreach (var v in touchedValues) templates[v.Key] = v;
            var conflicts = new List<string>();
            foreach (var key in touched)
            {
                Variable expected;
                if (oldValues.TryGetValue(key, out expected) && !Variable.Equal(current.Get(expected.Name, expected.Scope), expected))
                {
                    if (keepExternal && !definitions.Any(g => g.Entries.Any(e => e.Key == key))) working.Put(current.Get(expected.Name, expected.Scope));
                    else conflicts.Add(expected.Name + "（" + (expected.Scope == 1 ? "系统" : "用户") + "）");
                }
            }
            if (conflicts.Count > 0) throw new ConflictException("这些变量被其他软件修改：\n\n" + String.Join("、", conflicts) + "\n\n请核对外部修改。停用时仅能保留不再由其他配置组管理的外部值；也可停用全部配置组并保留外部修改。");
            var next = new List<Lease>();
            foreach (var definition in definitions)
            {
                var g = definition.Copy(); g.Original.Clear(); g.Applied.Clear();
                foreach (var e in g.Entries) { var original = working.Get(e.Name, e.Scope); var applied = Resolve(original, e); g.Original.Add(original); g.Applied.Add(applied); working.Put(applied); }
                next.Add(g);
            }
            var plan = new Plan { Label = label, BeforeGroups = before, NextGroups = next };
            foreach (var key in touched)
            {
                Variable template; if (!templates.TryGetValue(key, out template)) throw new Exception("缺少变量的范围与名称。");
                var actual = current.Get(template.Name, template.Scope); var after = working.Get(template.Name, template.Scope);
                if (actual.Exists && actual.Kind != 1 && actual.Kind != 2) throw new Exception(actual.Name + " 的注册表类型不是字符串。");
                plan.Preconditions.Add(actual);
                if (!Variable.Equal(actual, after)) plan.Changes.Add(new Change { Before = actual, After = after });
            }
            plan.Changes = plan.Changes.OrderBy(c => c.After.Scope).ThenBy(c => c.After.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var shared = next.SelectMany(g => g.Entries).GroupBy(e => e.Key).Where(g => g.Count() > 1).Where(g => touched.Contains(g.Key)).ToList();
            plan.CompositionNote = "默认按启用顺序叠加，优先级数字越大越高。设置/删除由高优先组覆盖；前置/追加依次合并。更新保持优先级，停用时重新计算其余组。";
            if (shared.Count > 0) plan.CompositionNote += "\n\n重叠变量：\n" + String.Join("\n", shared.Select(g => g.First().Name + "（" + (g.First().Scope == 1 ? "系统" : "用户") + "）：" + String.Join(" → ", GroupState.Owners(next, g.Key).Select(o => o.ProfileName))));
            return plan;
        }
        public static Plan SetEnabled(Snapshot current, State state, Profile profile, bool enabled, bool keepExternal = false)
        {
            var definitions = GroupState.Copy(state.ActiveGroups); int index = definitions.FindIndex(g => g.ProfileId == profile.Id);
            var keys = new HashSet<string>(); if (index >= 0) foreach (var e in definitions[index].Entries) keys.Add(e.Key);
            if (enabled && profile.Entries.Count == 0 && index >= 0) { definitions.RemoveAt(index); }
            else if (enabled)
            {
                var next = Definition(profile); foreach (var e in next.Entries) keys.Add(e.Key);
                if (index >= 0) definitions[index] = next; else definitions.Add(next);
            }
            else { if (index < 0) throw new Exception("此配置组尚未启用。"); definitions.RemoveAt(index); }
            return Reconcile(current, state, definitions, keys, (enabled ? index >= 0 ? "更新 · " : "启用 · " : "停用 · ") + profile.Name, keepExternal && !enabled);
        }
        public static Plan DisableAll(Snapshot current, State state, bool keepExternal = false)
        {
            return Reconcile(current, state, new List<Lease>(), state.ActiveGroups.SelectMany(g => g.Entries).Select(e => e.Key), "停用全部配置组", keepExternal);
        }
        public static Plan Prioritize(Snapshot current, State state, Profile profile)
        {
            var definitions = GroupState.Copy(state.ActiveGroups); var group = definitions.FirstOrDefault(g => g.ProfileId == profile.Id);
            if (group == null) throw new Exception("请先启用此配置组。");
            definitions.Remove(group); definitions.Add(group);
            return Reconcile(current, state, definitions, group.Entries.Select(e => e.Key), "提高优先级 · " + profile.Name, false);
        }
        public static Plan Edit(Snapshot current, State state, Entry entry)
        {
            var owners = GroupState.Owners(state.ActiveGroups, entry.Key);
            if (owners.Count > 0) throw new Exception("此变量由“" + String.Join("、", owners.Select(g => g.ProfileName)) + "”管理。请在配置组中编辑，或先停用相关组。");
            var plan = Planner.Edit(current, null, entry);
            plan.BeforeGroups = GroupState.Copy(state.ActiveGroups);
            plan.NextGroups = GroupState.Copy(state.ActiveGroups);
            LocalDescriptions.AddChange(plan, state, entry.Name, entry.Scope, entry.Mode == "Delete" ? null : entry.Description);
            return plan;
        }
    }
}
