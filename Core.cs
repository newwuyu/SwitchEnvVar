using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace SwitchEnv
{
    [DataContract]
    public class Variable
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Value { get; set; }
        [DataMember] public int Scope { get; set; }
        [DataMember] public int Kind { get; set; }
        [DataMember] public bool Exists { get; set; }
        public string Key { get { return Scope + ":" + (Name ?? "").ToUpperInvariant(); } }
        public Variable Copy() { return new Variable { Name = Name, Value = Value, Scope = Scope, Kind = Kind, Exists = Exists }; }
        public static Variable Missing(string name, int scope) { return new Variable { Name = name, Scope = scope, Kind = 1, Exists = false, Value = "" }; }
        public static bool Equal(Variable a, Variable b)
        {
            return a.Exists == b.Exists && (!a.Exists || (a.Kind == b.Kind && String.Equals(a.Value, b.Value, StringComparison.Ordinal)));
        }
    }

    [DataContract]
    public class Entry
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Value { get; set; }
        [DataMember] public int Scope { get; set; }
        [DataMember] public int Kind { get; set; }
        [DataMember] public string Mode { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Description { get; set; }
        public string Key { get { return Scope + ":" + (Name ?? "").ToUpperInvariant(); } }
        public Entry Copy(bool includeDescription = true)
        {
            return new Entry { Name = Name, Value = Value, Scope = Scope, Kind = Kind, Mode = Mode,
                Description = includeDescription ? Description : null };
        }
        public bool SameOperation(Entry other)
        {
            return other != null && Name == other.Name && Scope == other.Scope && Kind == other.Kind && Mode == other.Mode && Value == other.Value;
        }
    }

    [DataContract]
    public class Profile
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string Note { get; set; }
        [DataMember] public List<Entry> Entries { get; set; }
        public Profile() { Id = Guid.NewGuid().ToString("N"); Entries = new List<Entry>(); Note = ""; }
    }

    [DataContract]
    public class Lease
    {
        [DataMember] public string ProfileId { get; set; }
        [DataMember] public string ProfileName { get; set; }
        [DataMember] public List<Variable> Original { get; set; }
        [DataMember] public List<Variable> Applied { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<Entry> Entries { get; set; }
        public Lease Copy()
        {
            return new Lease { ProfileId = ProfileId, ProfileName = ProfileName,
                Original = Original.Select(v => v.Copy()).ToList(), Applied = Applied.Select(v => v.Copy()).ToList(), Entries = Entries == null ? null : Entries.Select(e => e.Copy()).ToList() };
        }
    }

    [DataContract]
    public class Change
    {
        [DataMember] public Variable Before { get; set; }
        [DataMember] public Variable After { get; set; }
    }

    [DataContract]
    public class History
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Label { get; set; }
        [DataMember] public string Time { get; set; }
        [DataMember] public List<Change> Changes { get; set; }
        [DataMember] public Lease BeforeLease { get; set; }
        [DataMember] public Lease AfterLease { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<Lease> BeforeGroups { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<Lease> AfterGroups { get; set; }
        [DataMember(EmitDefaultValue = false)] public string CompositionNote { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<Variable> AfterConditions { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<ProfileEntryChange> ProfileChanges { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<DescriptionChange> DescriptionChanges { get; set; }
    }

    [DataContract]
    public class State
    {
        [DataMember] public int Version { get; set; }
        [DataMember] public string UserSid { get; set; }
        [DataMember] public List<Profile> Profiles { get; set; }
        [DataMember] public Lease Active { get; set; }
        [DataMember(EmitDefaultValue = false)] public List<Lease> ActiveGroups { get; set; }
        [DataMember] public List<History> History { get; set; }
        [DataMember(EmitDefaultValue = false)] public Dictionary<string, string> LocalDescriptions { get; set; }
        public State() { Version = 2; Profiles = new List<Profile>(); History = new List<History>(); ActiveGroups = new List<Lease>(); }
    }

    [DataContract]
    public class Pending
    {
        [DataMember] public History Transaction { get; set; }
        [DataMember] public State BeforeState { get; set; }
    }

    public class Snapshot
    {
        public Dictionary<string, Variable> Values = new Dictionary<string, Variable>(StringComparer.OrdinalIgnoreCase);
        public Variable Get(string name, int scope)
        {
            Variable v; return Values.TryGetValue(scope + ":" + name.ToUpperInvariant(), out v) ? v.Copy() : Variable.Missing(name, scope);
        }
        public void Put(Variable v) { if (v.Exists) Values[v.Key] = v.Copy(); else Values.Remove(v.Key); }
        public Snapshot Copy() { var s = new Snapshot(); foreach (var v in Values.Values) s.Put(v); return s; }
    }

    public interface IEnvironment
    {
        Snapshot Read();
        void EnsureWritable(IEnumerable<int> scopes);
        void Write(Variable value);
        void Notify();
    }

    public interface IStore
    {
        State Load();
        void Save(State state);
        Pending LoadPending();
        void SavePending(Pending pending);
        void ClearPending();
    }

    public class Plan
    {
        public string Label;
        public List<Change> Changes = new List<Change>();
        public List<Variable> Preconditions = new List<Variable>();
        public Lease NextLease;
        public Lease BeforeLease;
        public List<Lease> BeforeGroups;
        public List<Lease> NextGroups;
        public string CompositionNote;
        public List<ProfileEntryChange> ProfileChanges = new List<ProfileEntryChange>();
        public List<DescriptionChange> DescriptionChanges = new List<DescriptionChange>();
    }

    public class ConflictException : Exception
    {
        public ConflictException(string message) : base(message) { }
    }

    public static class Planner
    {
        static readonly string[] Protected = { "SYSTEMROOT", "WINDIR", "COMSPEC", "SYSTEMDRIVE" };
        public static bool IsProtected(string name) { return Protected.Contains(name.ToUpperInvariant()); }
        public static void Validate(Entry e)
        {
            if (String.IsNullOrWhiteSpace(e.Name) || e.Name != e.Name.Trim() || e.Name.IndexOfAny(new[] { '=', '\0', '\r', '\n' }) >= 0)
                throw new Exception("变量名不能为空，不能含等号、换行或首尾空格。");
            if (e.Name.Length > 255) throw new Exception("变量名过长（最多 255 个字符）。");
            if (e.Scope != 0 && e.Scope != 1) throw new Exception("无效的变量范围。");
            if (e.Kind != 1 && e.Kind != 2) throw new Exception("只支持字符串和可展开字符串。");
            if (e.Value == null || e.Value.IndexOf('\0') >= 0 || e.Value.Length > 32766) throw new Exception("变量值不能包含空字符，且不能超过 32766 个字符。");
            if (!new[] { "Set", "Prepend", "Append", "Delete" }.Contains(e.Mode)) throw new Exception("无效的编辑方式。");
            if (IsProtected(e.Name)) throw new Exception("Windows 基础变量 " + e.Name + " 不能由配置组修改。");
            if ((e.Mode == "Append" || e.Mode == "Prepend") && String.IsNullOrWhiteSpace(e.Value)) throw new Exception("追加或前置的列表不能为空。");
        }

        public static string MergePath(string existing, string addition, bool prepend)
        {
            var first = (prepend ? addition : existing) ?? "";
            var second = (prepend ? existing : addition) ?? "";
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (first + ";" + second).Split(';'))
            {
                var p = part.Trim();
                var comparison = p.Length == 3 && p[1] == ':' && (p[2] == '\\' || p[2] == '/') ? p : p.TrimEnd('\\', '/');
                if (p.Length > 0 && seen.Add(comparison)) result.Add(p);
            }
            return String.Join(";", result);
        }

        static void CheckKind(Variable v)
        {
            if (v.Exists && v.Kind != 1 && v.Kind != 2) throw new Exception(v.Name + " 的注册表类型不是字符串，无法安全修改。");
        }

        public static Plan Switch(Snapshot current, Lease active, Profile target, bool keepExternal)
        {
            var foundation = current.Copy();
            var keys = new Dictionary<string, Variable>();
            var pre = new Dictionary<string, Variable>();
            if (active != null)
            {
                var original = active.Original.ToDictionary(v => v.Key);
                var conflicts = new List<string>();
                foreach (var expected in active.Applied)
                {
                    var actual = current.Get(expected.Name, expected.Scope); pre[actual.Key] = actual;
                    if (!Variable.Equal(actual, expected))
                    {
                        conflicts.Add(expected.Name + (expected.Scope == 1 ? "（系统）" : "（用户）"));
                        if (keepExternal && target == null) continue;
                    }
                    foundation.Put(original[expected.Key]); keys[expected.Key] = expected;
                }
                if (conflicts.Count > 0 && !(keepExternal && target == null))
                    throw new ConflictException("这些变量在配置组启用后被其他软件修改：\n\n" + String.Join("、", conflicts) + "\n\n可先停用配置组并保留外部修改，再切换到新配置组。");
            }
            Lease next = null;
            if (target != null)
            {
                if (target.Entries.Count == 0) throw new Exception("配置组还没有变量。先添加变量，再启用配置组。");
                next = new Lease { ProfileId = target.Id, ProfileName = target.Name, Entries = target.Entries.Select(e => e.Copy(false)).ToList(), Original = new List<Variable>(), Applied = new List<Variable>() };
                var duplicate = new HashSet<string>();
                foreach (var e in target.Entries)
                {
                    Validate(e);
                    if (!duplicate.Add(e.Key)) throw new Exception("同一范围内有重复变量：" + e.Name);
                    var original = foundation.Get(e.Name, e.Scope); CheckKind(original);
                    next.Original.Add(original);
                    var value = new Variable { Name = original.Exists ? original.Name : e.Name, Scope = e.Scope, Kind = e.Kind, Exists = e.Mode != "Delete", Value = e.Value };
                    if (e.Mode == "Prepend" || e.Mode == "Append")
                    {
                        value.Value = MergePath(original.Exists ? original.Value : "", e.Value, e.Mode == "Prepend");
                        value.Kind = (original.Kind == 2 || e.Kind == 2) ? 2 : 1;
                    }
                    if (value.Value.Length > 32766) throw new Exception(e.Name + " 合并后的值过长。");
                    foundation.Put(value); next.Applied.Add(value.Copy()); keys[value.Key] = value;
                    pre[value.Key] = current.Get(e.Name, e.Scope);
                }
            }
            var plan = new Plan { NextLease = next, BeforeLease = active == null ? null : active.Copy(),
                Label = target == null ? "停用配置组" : "启用 · " + target.Name, Preconditions = pre.Values.ToList() };
            foreach (var key in keys.Values)
            {
                var before = current.Get(key.Name, key.Scope); var after = foundation.Get(key.Name, key.Scope);
                CheckKind(before); CheckKind(after);
                if (!Variable.Equal(before, after)) plan.Changes.Add(new Change { Before = before, After = after });
            }
            plan.Changes = plan.Changes.OrderBy(c => c.After.Scope).ThenBy(c => c.After.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return plan;
        }

        public static Plan Edit(Snapshot current, Lease active, Entry entry)
        {
            Validate(entry);
            if (active != null && active.Applied.Any(v => v.Key == entry.Key)) throw new Exception("此变量由“" + active.ProfileName + "”管理。请编辑该配置组，或先停用它。");
            var before = current.Get(entry.Name, entry.Scope); CheckKind(before);
            var after = new Variable { Name = before.Exists ? before.Name : entry.Name, Scope = entry.Scope, Kind = entry.Kind, Exists = entry.Mode != "Delete", Value = entry.Value };
            var p = new Plan { Label = (after.Exists ? "编辑 · " : "删除 · ") + entry.Name, NextLease = active == null ? null : active.Copy(), BeforeLease = active == null ? null : active.Copy() };
            p.Preconditions.Add(before);
            if (!Variable.Equal(before, after)) p.Changes.Add(new Change { Before = before, After = after });
            return p;
        }

        public static Plan Undo(Snapshot current, History h, State state = null)
        {
            GroupState.NormalizeHistory(h);
            if (state != null && h.BeforeGroups.Any(g => !state.Profiles.Any(p => p.Id == g.ProfileId))) throw new ConflictException("要恢复的配置组已被删除，不能恢复其启用状态。请保留备份并核对记录。");
            var plan = new Plan { Label = "恢复上次状态 · " + h.Label, BeforeGroups = GroupState.Copy(h.AfterGroups), NextGroups = GroupState.Copy(h.BeforeGroups) };
            foreach (var c in h.Changes)
            {
                var actual = current.Get(c.After.Name, c.After.Scope);
                if (!Variable.Equal(actual, c.After)) throw new ConflictException(c.After.Name + " 已再次修改，不能覆盖它来恢复旧值。");
                plan.Preconditions.Add(actual); plan.Changes.Add(new Change { Before = c.After.Copy(), After = c.Before.Copy() });
            }
            if (h.AfterConditions != null) foreach (var expected in h.AfterConditions)
            {
                var actual = current.Get(expected.Name, expected.Scope);
                if (!Variable.Equal(actual, expected)) throw new ConflictException(expected.Name + " 已再次修改，不能恢复旧的管理归属。");
                plan.Preconditions.Add(actual);
            }
            if (h.AfterConditions == null && h.ProfileChanges != null)
            {
                var after = GroupState.Effective(h.AfterGroups);
                foreach (var expected in GroupState.Effective(h.BeforeGroups).Values.Where(v => !after.ContainsKey(v.Key) && !h.Changes.Any(c => c.After.Key == v.Key)))
                {
                    var actual = current.Get(expected.Name, expected.Scope);
                    if (!Variable.Equal(actual, expected)) throw new ConflictException(expected.Name + " 已再次修改，不能恢复旧的管理归属。");
                    plan.Preconditions.Add(actual);
                }
            }
            foreach (var v in GroupState.Effective(h.AfterGroups).Values)
            {
                var actual = current.Get(v.Name, v.Scope);
                if (!Variable.Equal(actual, v)) throw new ConflictException(v.Name + " 被外部修改，请先处理当前配置组的冲突。");
                plan.Preconditions.Add(actual);
            }
            if (h.ProfileChanges != null && h.ProfileChanges.Count > 0)
            {
                if (state == null) throw new Exception("恢复变量移动需要当前配置组状态。");
                plan.ProfileChanges = h.ProfileChanges.Select(c => new ProfileEntryChange { ProfileId = c.ProfileId, ProfileName = c.ProfileName,
                    Before = c.After == null ? null : c.After.Copy(), After = c.Before == null ? null : c.Before.Copy() }).ToList();
                ProfileChanges.Validate(state, plan.ProfileChanges);
            }
            if (h.DescriptionChanges != null && h.DescriptionChanges.Count > 0)
            {
                if (state == null) throw new Exception("恢复本机变量描述需要当前数据。");
                plan.DescriptionChanges = h.DescriptionChanges.Select(c => new DescriptionChange {
                    Name = c.Name, Scope = c.Scope, Before = c.After, After = c.Before
                }).ToList();
                LocalDescriptions.Validate(state, plan.DescriptionChanges);
            }
            return plan;
        }
    }

    public class Engine
    {
        readonly IEnvironment environment; readonly IStore store;
        public Engine(IEnvironment environment, IStore store) { this.environment = environment; this.store = store; }
        public State Execute(Plan plan, State state)
        {
            if (store.LoadPending() != null) throw new Exception("有未完成的修改，请先执行恢复。");
            GroupState.Normalize(state);
            var beforeGroups = plan.BeforeGroups ?? GroupState.FromLegacy(plan.BeforeLease);
            var nextGroups = plan.NextGroups ?? GroupState.FromLegacy(plan.NextLease);
            if (!GroupState.Equal(state.ActiveGroups, beforeGroups)) throw new ConflictException("预览后启用的配置组发生了变化，请重新预览。");
            ProfileChanges.Validate(state, plan.ProfileChanges);
            LocalDescriptions.Validate(state, plan.DescriptionChanges);
            foreach (var c in plan.Changes)
            {
                if (c.Before.Key != c.After.Key) throw new Exception("修改前后必须是同一变量。");
                Planner.Validate(new Entry { Name = c.After.Name, Value = c.After.Exists ? c.After.Value : "", Scope = c.After.Scope, Kind = c.After.Kind, Mode = c.After.Exists ? "Set" : "Delete" });
                if (c.Before.Exists && c.Before.Kind != 1 && c.Before.Kind != 2) throw new Exception("无法安全备份非字符串变量。");
            }
            environment.EnsureWritable(plan.Changes.Select(c => c.After.Scope).Distinct());
            var now = environment.Read();
            foreach (var v in plan.Preconditions) if (!Variable.Equal(now.Get(v.Name, v.Scope), v)) throw new ConflictException("预览后环境发生了变化：" + v.Name + "。请重新预览。");
            var h = new History { Id = Guid.NewGuid().ToString("N"), Label = plan.Label, Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Changes = plan.Changes,
                BeforeGroups = GroupState.Copy(beforeGroups), AfterGroups = GroupState.Copy(nextGroups), CompositionNote = plan.CompositionNote,
                ProfileChanges = plan.ProfileChanges.Count == 0 ? null : Json.Copy(plan.ProfileChanges),
                DescriptionChanges = plan.DescriptionChanges.Count == 0 ? null : Json.Copy(plan.DescriptionChanges) };
            var afterValues = plan.Changes.ToDictionary(c => c.After.Key, c => c.After);
            h.AfterConditions = plan.Preconditions.GroupBy(v => v.Key).Select(g => afterValues.ContainsKey(g.Key) ? afterValues[g.Key].Copy() : g.First().Copy()).ToList();
            var pending = new Pending { Transaction = h, BeforeState = Json.Copy(state) };
            store.SavePending(pending);
            var attempted = new List<Change>();
            try
            {
                foreach (var c in plan.Changes)
                {
                    var current = environment.Read().Get(c.Before.Name, c.Before.Scope);
                    if (!Variable.Equal(current, c.Before)) throw new ConflictException(c.Before.Name + " 在写入前发生了变化。");
                    attempted.Add(c); environment.Write(c.After);
                }
                var verified = environment.Read();
                foreach (var expected in h.AfterConditions)
                    if (!Variable.Equal(verified.Get(expected.Name, expected.Scope), expected)) throw new ConflictException(expected.Name + " 在写入期间被其他软件修改。");
                var next = Json.Copy(state); GroupState.Assign(next, nextGroups);
                ProfileChanges.Apply(next, plan.ProfileChanges);
                LocalDescriptions.Apply(next, plan.DescriptionChanges);
                next.History.Insert(0, h); next.History = next.History.Take(100).ToList();
                store.Save(next);
                try { store.ClearPending(); } catch { /* Committed id allows safe cleanup on next launch. */ }
                try { environment.Notify(); } catch { } return next;
            }
            catch (Exception ex)
            {
                bool restored = true;
                foreach (var c in attempted.AsEnumerable().Reverse())
                {
                    try
                    {
                        var actual = environment.Read().Get(c.After.Name, c.After.Scope);
                        if (Variable.Equal(actual, c.After)) environment.Write(c.Before);
                        else if (!Variable.Equal(actual, c.Before)) restored = false;
                    }
                    catch { restored = false; }
                }
                if (restored) { try { store.ClearPending(); } catch { restored = false; } }
                try { environment.Notify(); } catch { }
                throw new Exception(ex.Message + (restored ? "\n\n此次修改已回滚。" : "\n\n部分修改未能回滚。已保留恢复记录，请点击“恢复未完成的修改”。"), ex);
            }
        }

        public State Recover(State state)
        {
            var pending = store.LoadPending(); if (pending == null) return state;
            if (state.History.Any(h => h.Id == pending.Transaction.Id)) { store.ClearPending(); return state; }
            environment.EnsureWritable(pending.Transaction.Changes.Select(c => c.Before.Scope).Distinct());
            foreach (var c in pending.Transaction.Changes.AsEnumerable().Reverse())
            {
                var actual = environment.Read().Get(c.After.Name, c.After.Scope);
                if (Variable.Equal(actual, c.After)) environment.Write(c.Before);
                else if (!Variable.Equal(actual, c.Before)) throw new ConflictException(c.Before.Name + " 已被其他软件修改，恢复记录已保留。请手动核对 data/pending.json。");
            }
            GroupState.Normalize(pending.BeforeState); store.Save(pending.BeforeState); store.ClearPending(); environment.Notify(); return pending.BeforeState;
        }
    }
}
