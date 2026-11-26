using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SwitchEnv
{
    [DataContract]
    public class DescriptionChange
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public int Scope { get; set; }
        [DataMember] public string Before { get; set; }
        [DataMember] public string After { get; set; }
        public string Key { get { return Scope + ":" + Name.ToUpperInvariant(); } }
    }

    public static class LocalDescriptions
    {
        public static string Get(State state, string key)
        {
            string value;
            return state.LocalDescriptions != null && state.LocalDescriptions.TryGetValue(key, out value) ? value : null;
        }

        public static void Set(State state, string key, string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                if (state.LocalDescriptions != null) state.LocalDescriptions.Remove(key);
                return;
            }
            if (state.LocalDescriptions == null) state.LocalDescriptions = new Dictionary<string, string>();
            state.LocalDescriptions[key] = text;
        }

        public static void AddChange(Plan plan, State state, string name, int scope, string text)
        {
            var change = new DescriptionChange { Name = name, Scope = scope, After = String.IsNullOrEmpty(text) ? null : text };
            change.Before = Get(state, change.Key);
            if (change.Before != change.After) plan.DescriptionChanges.Add(change);
        }

        public static void Apply(State state, IEnumerable<DescriptionChange> changes)
        {
            foreach (var change in changes) Set(state, change.Key, change.After);
        }

        public static void Validate(State state, IEnumerable<DescriptionChange> changes)
        {
            foreach (var change in changes)
                if (Get(state, change.Key) != change.Before)
                    throw new ConflictException(change.Name + " 的本机描述已修改，请重新预览。");
        }
    }
}
