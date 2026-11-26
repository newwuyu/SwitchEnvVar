using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace SwitchEnv
{
    public class MoveRequest
    {
        public string Name;
        public int SourceScope;
        public string SourceProfileId;
        public int TargetScope;
        public string TargetProfileId;
    }

    [DataContract]
    public class ProfileEntryChange
    {
        [DataMember] public string ProfileId { get; set; }
        [DataMember] public string ProfileName { get; set; }
        [DataMember] public Entry Before { get; set; }
        [DataMember] public Entry After { get; set; }
        public string Key { get { return (After ?? Before).Key; } }
    }

    public static class ProfileChanges
    {
        public static bool Equal(Entry a, Entry b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.SameOperation(b) && (a.Description ?? "") == (b.Description ?? "");
        }
        public static Entry Get(State state, string profileId, string key)
        {
            var profile = state.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile == null) throw new ConflictException("相关配置组已被删除，请重新选择移动目标或恢复记录。");
            return profile.Entries.FirstOrDefault(e => e.Key == key);
        }
        public static void Validate(State state, IEnumerable<ProfileEntryChange> changes)
        {
            var seen = new HashSet<string>();
            foreach (var c in changes)
            {
                if (c.Before == null && c.After == null) throw new Exception("无效的配置组修改。");
                if (c.Before != null && c.After != null && c.Before.Key != c.After.Key) throw new Exception("配置组修改前后必须是同一变量。");
                if (!seen.Add(c.ProfileId + ":" + c.Key)) throw new Exception("同一配置组变量不能重复修改。");
                if (!Equal(Get(state, c.ProfileId, c.Key), c.Before)) throw new ConflictException("配置组中的变量已修改：" + (c.Before ?? c.After).Name + "。请重新预览。");
                if (c.After != null) Planner.Validate(c.After);
            }
        }
        public static void Apply(State state, IEnumerable<ProfileEntryChange> changes)
        {
            foreach (var c in changes)
            {
                var profile = state.Profiles.First(p => p.Id == c.ProfileId);
                int index = profile.Entries.FindIndex(e => e.Key == c.Key);
                if (c.After == null) { if (index >= 0) profile.Entries.RemoveAt(index); }
                else if (index < 0) profile.Entries.Add(c.After.Copy());
                else profile.Entries[index] = c.After.Copy();
            }
        }
    }

    public static class MovePlanner
    {
        static Profile Profile(State state, string id)
        {
            var profile = state.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) throw new Exception("找不到移动的来源或目标配置组。");
            return profile;
        }
        static void StringKind(Variable value)
        {
            if (value.Exists && value.Kind != 1 && value.Kind != 2) throw new Exception(value.Name + " 不是字符串变量，不能移动。");
        }
        public static Plan Move(Snapshot current, State state, MoveRequest request)
        {
            if (request.SourceScope < 0 || request.SourceScope > 1 || request.TargetScope < 0 || request.TargetScope > 1) throw new Exception("无效的变量范围。");
            bool sourceLocal = String.IsNullOrEmpty(request.SourceProfileId), targetLocal = String.IsNullOrEmpty(request.TargetProfileId);
            if (sourceLocal == targetLocal && request.SourceProfileId == request.TargetProfileId && request.SourceScope == request.TargetScope) throw new Exception("请选择不同的目标位置或作用范围。");
            Profile sourceProfile = sourceLocal ? null : Profile(state, request.SourceProfileId);
            Profile targetProfile = targetLocal ? null : Profile(state, request.TargetProfileId);
            string sourceKey = request.SourceScope + ":" + request.Name.ToUpperInvariant();
            string targetKey = request.TargetScope + ":" + request.Name.ToUpperInvariant();
            Entry entry;
            if (sourceLocal)
            {
                var source = current.Get(request.Name, request.SourceScope);
                if (!source.Exists) throw new ConflictException("来源变量已不存在，请刷新后重新选择。");
                StringKind(source);
                entry = new Entry { Name = source.Name, Value = source.Value, Scope = source.Scope, Kind = source.Kind, Mode = "Set",
                    Description = LocalDescriptions.Get(state, source.Key) };
            }
            else
            {
                var source = sourceProfile.Entries.FirstOrDefault(e => e.Key == sourceKey);
                if (source == null) throw new ConflictException("来源配置组中的变量已不存在，请刷新后重新选择。");
                entry = source.Copy();
            }
            Planner.Validate(entry);
            var label = "移动 · " + entry.Name + " → " + (targetLocal ? "本机" : targetProfile.Name) + "（" + (request.TargetScope == 1 ? "系统" : "用户") + "）";
            var definitions = GroupState.Copy(state.ActiveGroups); var foundation = GroupState.Foundation(current, state.ActiveGroups);
            var changes = new List<ProfileEntryChange>(); var touched = new HashSet<string>();
            Action<Profile, Entry, Entry> definition = (p, before, after) => {
                if (!ProfileChanges.Equal(before, after)) changes.Add(new ProfileEntryChange { ProfileId = p.Id, ProfileName = p.Name, Before = before == null ? null : before.Copy(), After = after == null ? null : after.Copy() });
            };
            if (sourceLocal)
            {
                var owners = GroupState.Owners(definitions, sourceKey);
                if (owners.Count > 0) throw new Exception("此变量由“" + String.Join("、", owners.Select(g => g.ProfileName)) + "”管理。请在配置组页面移动。");
                touched.Add(sourceKey); foundation.Put(Variable.Missing(entry.Name, entry.Scope));
            }
            else
            {
                definition(sourceProfile, entry, null);
                var sourceGroup = definitions.FirstOrDefault(g => g.ProfileId == sourceProfile.Id);
                if (sourceGroup != null && sourceGroup.Entries.Any(e => e.Key == sourceKey))
                {
                    touched.Add(sourceKey); sourceGroup.Entries.RemoveAll(e => e.Key == sourceKey);
                }
            }
            var moved = entry.Copy(); moved.Scope = request.TargetScope; Planner.Validate(moved);
            if (targetLocal)
            {
                var owners = GroupState.Owners(definitions, targetKey);
                if (owners.Count > 0) throw new Exception("目标仍由“" + String.Join("、", owners.Select(g => g.ProfileName)) + "”管理。请移入对应配置组，或先停用相关组。");
                touched.Add(targetKey); foundation.Put(GroupPlanner.Resolve(foundation.Get(moved.Name, moved.Scope), moved));
            }
            else
            {
                definition(targetProfile, targetProfile.Entries.FirstOrDefault(e => e.Key == targetKey), moved);
                var targetGroup = definitions.FirstOrDefault(g => g.ProfileId == targetProfile.Id);
                if (targetGroup != null)
                {
                    touched.Add(targetKey); int index = targetGroup.Entries.FindIndex(e => e.Key == targetKey);
                    if (index < 0) targetGroup.Entries.Add(moved.Copy(false)); else targetGroup.Entries[index] = moved.Copy(false);
                }
            }
            definitions.RemoveAll(g => g.Entries.Count == 0);
            var plan = GroupPlanner.Reconcile(current, state, definitions, touched, label, false, foundation,
                new[] { Variable.Missing(entry.Name, entry.Scope), Variable.Missing(moved.Name, moved.Scope) });
            if (sourceLocal) LocalDescriptions.AddChange(plan, state, entry.Name, entry.Scope, null);
            if (targetLocal) LocalDescriptions.AddChange(plan, state, moved.Name, moved.Scope, moved.Mode == "Delete" ? null : moved.Description);
            plan.ProfileChanges = changes;
            ProfileChanges.Validate(state, plan.ProfileChanges);
            return plan;
        }
        static string Location(string profileId, int scope, string name)
        {
            return (String.IsNullOrEmpty(profileId) ? "local" : "profile:" + profileId) + ":" + scope + ":" + (name ?? "").ToUpperInvariant();
        }

        public static Plan MoveBatch(Snapshot current, State state, IEnumerable<MoveRequest> requests)
        {
            var selected = requests == null ? new List<MoveRequest>() : requests.ToList();
            if (selected.Count == 0) throw new Exception("请先选择要移动的变量。");
            var sources = new HashSet<string>(); var targets = new HashSet<string>();
            foreach (var request in selected)
            {
                if (request == null || String.IsNullOrWhiteSpace(request.Name)) throw new Exception("移动请求缺少变量名称。");
                if (request.SourceScope < 0 || request.SourceScope > 1 || request.TargetScope < 0 || request.TargetScope > 1) throw new Exception("无效的变量范围。");
                if (!sources.Add(Location(request.SourceProfileId, request.SourceScope, request.Name))) throw new Exception("同一来源变量不能重复选择：" + request.Name);
                if (!targets.Add(Location(request.TargetProfileId, request.TargetScope, request.Name)))
                    throw new Exception("多个选中变量会写入同一目标：" + request.Name + "（" + (request.TargetScope == 1 ? "系统" : "用户") + "）。请保留各变量原范围，或只选择其中一个来源。");
            }
            var moving = selected.Where(r => Location(r.SourceProfileId, r.SourceScope, r.Name) != Location(r.TargetProfileId, r.TargetScope, r.Name)).ToList();
            if (moving.Count == 0) throw new Exception("选中的变量已在目标位置，无需移动。");
            foreach (var request in moving)
                if (sources.Contains(Location(request.TargetProfileId, request.TargetScope, request.Name)))
                    throw new Exception("移动目标也是本次选中的来源：" + request.Name + "。请分开移动，避免覆盖选中的原值。");
            if (selected.Count == 1) return Move(current, state, moving[0]);

            // Compose against copies, then commit one final plan. Never execute item-by-item.
            var shadowState = Json.Copy(state); var shadow = current.Copy();
            var beforeValues = new Dictionary<string, Variable>(); var definitions = new Dictionary<string, ProfileEntryChange>();
            var descriptionKeys = new Dictionary<string, DescriptionChange>();
            foreach (var request in moving)
            {
                var step = Move(shadow, shadowState, request);
                foreach (var value in step.Preconditions)
                    if (!beforeValues.ContainsKey(value.Key)) beforeValues[value.Key] = current.Get(value.Name, value.Scope);
                foreach (var change in step.ProfileChanges)
                {
                    string key = change.ProfileId + ":" + change.Key; ProfileEntryChange combined;
                    if (!definitions.TryGetValue(key, out combined))
                    {
                        combined = new ProfileEntryChange { ProfileId = change.ProfileId, ProfileName = change.ProfileName, Before = change.Before == null ? null : change.Before.Copy() };
                        definitions.Add(key, combined);
                    }
                    combined.After = change.After == null ? null : change.After.Copy();
                }
                foreach (var change in step.Changes) shadow.Put(change.After);
                foreach (var change in step.DescriptionChanges) descriptionKeys[change.Key] = change;
                LocalDescriptions.Apply(shadowState, step.DescriptionChanges);
                ProfileChanges.Apply(shadowState, step.ProfileChanges); GroupState.Assign(shadowState, step.NextGroups);
            }
            var destinations = moving.Select(r => (String.IsNullOrEmpty(r.TargetProfileId) ? "本机" : Profile(state, r.TargetProfileId).Name) + "（" + (r.TargetScope == 1 ? "系统" : "用户") + "）").Distinct().ToList();
            var plan = new Plan { Label = "批量移动 · " + moving.Count + " 项 → " + String.Join("、", destinations) + (moving.Count < selected.Count ? " · " + (selected.Count - moving.Count) + " 项已在目标位置，保留" : ""),
                BeforeGroups = GroupState.Copy(state.ActiveGroups), NextGroups = GroupState.Copy(shadowState.ActiveGroups), CompositionNote = "批量移动保持配置组优先级，并重新计算全部受影响变量。",
                Preconditions = beforeValues.Values.Select(v => v.Copy()).ToList(), ProfileChanges = definitions.Values.Where(c => !ProfileChanges.Equal(c.Before, c.After)).ToList() };
            foreach (var change in descriptionKeys.Values)
                LocalDescriptions.AddChange(plan, state, change.Name, change.Scope, LocalDescriptions.Get(shadowState, change.Key));
            foreach (var before in beforeValues.Values)
            {
                var after = shadow.Get(before.Name, before.Scope);
                if (!Variable.Equal(before, after)) plan.Changes.Add(new Change { Before = before.Copy(), After = after });
            }
            plan.Changes = plan.Changes.OrderBy(c => c.After.Scope).ThenBy(c => c.After.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ProfileChanges.Validate(state, plan.ProfileChanges); return plan;
        }
    }
}
