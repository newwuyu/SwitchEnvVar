using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;
using SwitchEnv;

public class MemoryEnvironment : IEnvironment
{
    public Snapshot Data = new Snapshot();
    public int Writes; public int FailWrite; public bool FailAfterWrite; public bool NotifyThrows;
    public Action<int> AfterWrite;
    public Snapshot Read() { return Data.Copy(); }
    public void EnsureWritable(IEnumerable<int> scopes) { }
    public void Write(Variable v)
    {
        Writes++;
        if (Writes == FailWrite && !FailAfterWrite) throw new Exception("Injected write failure");
        Data.Put(v); if (AfterWrite != null) AfterWrite(Writes);
        if (Writes == FailWrite && FailAfterWrite) throw new Exception("Injected post-write failure");
    }
    public void Notify() { if (NotifyThrows) throw new Exception("Injected notification failure"); }
}
public class MemoryStore : IStore
{
    public State Data = new State(); public Pending Pending; public bool FailCommit;
    public State Load() { return Json.Copy(Data); }
    public void Save(State s) { if (FailCommit) throw new Exception("Injected disk failure"); Data = Json.Copy(s); }
    public Pending LoadPending() { return Pending == null ? null : Json.Copy(Pending); }
    public void SavePending(Pending p) { Pending = Json.Copy(p); }
    public void ClearPending() { Pending = null; }
}
public class MemoryStartupTasks : IStartupTasks
{
    public readonly Dictionary<string, StartupTask> Items = new Dictionary<string, StartupTask>(StringComparer.OrdinalIgnoreCase);
    public bool FailRegister, FailDelete;
    public StartupTask Read(string name) { StartupTask value; return Items.TryGetValue(name, out value) ? value.Copy() : null; }
    public void Register(string name, string xml, string sid)
    {
        if (FailRegister) throw new Exception("Injected registration failure");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Items[name] = new StartupTask { Xml = xml, Enabled = (string)XDocument.Parse(xml).Root.Element(ns + "Settings").Element(ns + "Enabled") != "false" };
    }
    public void Delete(string name) { if (FailDelete) throw new Exception("Injected deletion failure"); Items.Remove(name); }
}
public static class Tests
{
    static int count;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
    static string[] ParseCommand(string command)
    {
        int length; IntPtr pointer = CommandLineToArgvW(command, out length);
        if (pointer == IntPtr.Zero) throw new Exception("CommandLineToArgvW failed");
        try { return Enumerable.Range(0, length).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))).ToArray(); }
        finally { LocalFree(pointer); }
    }
    static string StartupFingerprint()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(WindowsStartup.RunKey, false))
        {
            if (key == null) return "<missing>";
            return String.Join("\n", key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n =>
            {
                object value = key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string raw = value is byte[] ? Convert.ToBase64String((byte[])value) : value is string[] ? String.Join("\0", (string[])value) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(n)) + ":" + (int)key.GetValueKind(n) + ":" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
            }));
        }
    }
    static string CurrentSid() { using (var identity = WindowsIdentity.GetCurrent()) return identity.User.Value; }
    static string UserSid(string account) { return account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? new SecurityIdentifier(account).Value : new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value; }
    static WindowsStartup Startup(MemoryStartupTasks tasks, string key, string exe, string data)
    {
        return new WindowsStartup(exe, data, CurrentSid(), tasks, key);
    }
    static string ScheduledFingerprint()
    {
        var task = new ScheduledStartupTasks().Read("SwitchEnv.Startup." + CurrentSid());
        return task == null ? "<missing>" : task.Enabled + "\n" + task.Xml;
    }
    static void ReleaseCom(params object[] values)
    {
        foreach (object value in values) if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
    static void CleanupScratch(string path)
    {
        Registry.CurrentUser.DeleteSubKeyTree(path, false);
        bool empty; using (var root = Registry.CurrentUser.OpenSubKey("Software\\SwitchEnv.Tests")) empty = root != null && root.SubKeyCount == 0 && root.ValueCount == 0;
        if (empty) Registry.CurrentUser.DeleteSubKey("Software\\SwitchEnv.Tests", false);
    }
    static Variable V(string n, string v, int scope, int kind) { return new Variable { Name = n, Value = v, Exists = true, Scope = scope, Kind = kind }; }
    static Entry E(string n, string v, int scope, string mode) { return new Entry { Name = n, Value = v, Scope = scope, Kind = v.Contains("%") ? 2 : 1, Mode = mode }; }
    static Profile P(string name, params Entry[] e) { return new Profile { Name = name, Entries = e.ToList() }; }
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Throws(Action action) { bool threw = false; try { action(); } catch { threw = true; } Assert(threw, "Expected an error"); }
    static void Same(Snapshot a, Snapshot b) { Assert(a.Values.Count == b.Values.Count && a.Values.Values.All(v => Variable.Equal(v, b.Get(v.Name, v.Scope))), "Environment differs"); }
    static void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
    static State Switch(MemoryEnvironment env, MemoryStore db, State state, Profile p) { return new Engine(env, db).Execute(Planner.Switch(env.Read(), state.Active, p, false), state); }
    static State Stop(MemoryEnvironment env, MemoryStore db, State state) { return Switch(env, db, state, null); }
    static State Move(MemoryEnvironment env, MemoryStore db, State state, string name, int fromScope, string fromProfile, int toScope, string toProfile)
    {
        return new Engine(env, db).Execute(MovePlanner.Move(env.Read(), state, new MoveRequest { Name = name, SourceScope = fromScope, SourceProfileId = fromProfile, TargetScope = toScope, TargetProfileId = toProfile }), state);
    }
    static State Undo(MemoryEnvironment env, MemoryStore db, State state) { return new Engine(env, db).Execute(Planner.Undo(env.Read(), state.History[0], state), state); }
    static MoveRequest R(string name, int fromScope, string fromProfile, int toScope, string toProfile)
    { return new MoveRequest { Name = name, SourceScope = fromScope, SourceProfileId = fromProfile, TargetScope = toScope, TargetProfileId = toProfile }; }
    static State MoveMany(MemoryEnvironment env, MemoryStore db, State state, params MoveRequest[] requests)
    { return new Engine(env, db).Execute(MovePlanner.MoveBatch(env.Read(), state, requests), state); }

    static State Enable(MemoryEnvironment env, MemoryStore db, State state, Profile p)
    {
        if (!state.Profiles.Any(g => g.Id == p.Id)) state.Profiles.Add(p);
        return new Engine(env, db).Execute(GroupPlanner.SetEnabled(env.Read(), state, p, true), state);
    }
    static State Disable(MemoryEnvironment env, MemoryStore db, State state, Profile p) { return new Engine(env, db).Execute(GroupPlanner.SetEnabled(env.Read(), state, p, false), state); }
    static void MultiGroupTests(string folder)
    {
        Test("independent groups coexist and stop separately", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "base A", 0, 1)); var baseline = env.Read(); var db = new MemoryStore(); var a = P("Java", E("A", "java", 0, "Set")); var b = P("Proxy", E("B", "proxy", 1, "Set"));
            var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); Assert(s.ActiveGroups.Count == 2 && env.Read().Get("A", 0).Value == "java" && env.Read().Get("B", 1).Value == "proxy", "Groups did not coexist");
            s = Disable(env, db, s, a); Assert(s.ActiveGroups.Single().ProfileId == b.Id && env.Read().Get("A", 0).Value == "base A" && env.Read().Get("B", 1).Value == "proxy", "Stopping one group affected the other"); Disable(env, db, s, b); Same(baseline, env.Read());
        });
        Test("overlap uses later priority and reveals the earlier group", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "%BASE%", 0, 2)); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("a", "two", 0, "Set"));
            var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); Assert(env.Read().Get("A", 0).Value == "two" && GroupState.Owners(s.ActiveGroups, "0:A").Count == 2, "Priority or ownership failed");
            s = Disable(env, db, s, b); Assert(env.Read().Get("A", 0).Value == "one", "Earlier group was not restored"); Disable(env, db, s, a); Assert(env.Read().Get("A", 0).Value == "%BASE%" && env.Read().Get("A", 0).Kind == 2, "True baseline was lost");
        });
        Test("stopping a lower group preserves the highest value and true baseline", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "base", 0, 1)); var db = new MemoryStore(); var a = P("low", E("A", "low", 0, "Set")); var b = P("high", E("A", "high", 0, "Set"));
            var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); int writes = env.Writes; s = Disable(env, db, s, a); Assert(env.Writes == writes && env.Read().Get("A", 0).Value == "high" && s.ActiveGroups.Single().Original[0].Value == "base", "Lower group leaked into baseline"); Disable(env, db, s, b); Assert(env.Read().Get("A", 0).Value == "base", "Final stop restored an overlay");
        });
        Test("three PATH groups compose and can stop out of order", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("Path", "%BASE%", 0, 2)); var db = new MemoryStore(); var a = P("Java", E("Path", "A", 0, "Prepend")); var b = P("Node", E("Path", "B;a", 0, "Append")); var c = P("Tools", E("Path", "C", 0, "Prepend"));
            var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s = Enable(env, db, s, c); Assert(env.Read().Get("Path", 0).Value == "C;A;%BASE%;B", "PATH order or deduplication failed");
            s = Disable(env, db, s, a); Assert(env.Read().Get("Path", 0).Value == "C;%BASE%;B;a", "Removed PATH group remained"); s = Disable(env, db, s, b); Assert(env.Read().Get("Path", 0).Value == "C;%BASE%", "Middle PATH stop failed"); Disable(env, db, s, c); Assert(env.Read().Get("Path", 0).Value == "%BASE%" && env.Read().Get("Path", 0).Kind == 2, "Raw PATH baseline changed");
        });
        Test("PATH set overrides lower contributions and append resumes on stop", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base", 0, 1)); var db = new MemoryStore(); var a = P("append", E("Path", "A", 0, "Append")); var b = P("set", E("Path", "%CUSTOM%", 0, "Set")); var c = P("prepend", E("Path", "C", 0, "Prepend"));
            var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s = Enable(env, db, s, c); Assert(env.Read().Get("Path", 0).Value == "C;%CUSTOM%", "Set did not reset the lower value"); s = Disable(env, db, s, b); Assert(env.Read().Get("Path", 0).Value == "C;base;A", "Remaining PATH contributions were not recomputed");
        });
        Test("delete group masks values and restores remaining owners", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "%BASE%", 1, 2)); var db = new MemoryStore(); var a = P("set", E("A", "set", 1, "Set")); var b = P("delete", E("A", "", 1, "Delete"));
            var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); Assert(!env.Read().Get("A", 1).Exists, "Delete overlay failed"); s = Disable(env, db, s, a); Assert(!env.Read().Get("A", 1).Exists, "Lower stop removed the delete override"); Disable(env, db, s, b); Assert(env.Read().Get("A", 1).Value == "%BASE%" && env.Read().Get("A", 1).Kind == 2, "Delete baseline lost");
        });
        Test("groups keep same-name user and system variables independent", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("user", E("A", "user", 0, "Set")); var b = P("system", E("A", "system", 1, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s = Disable(env, db, s, a); Assert(!env.Read().Get("A", 0).Exists && env.Read().Get("A", 1).Value == "system", "Scopes were conflated");
        });
        Test("updating a masked group retains priority and updates its future value", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "base", 0, 1)); var db = new MemoryStore(); var a = P("low", E("A", "low", 0, "Set")); var b = P("high", E("A", "high", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b);
            var edited = s.Profiles.First(g => g.Id == a.Id); edited.Entries[0].Value = "updated"; s = Enable(env, db, s, edited); Assert(s.ActiveGroups[0].ProfileId == a.Id && env.Read().Get("A", 0).Value == "high", "Update changed priority"); s = Disable(env, db, s, b); Assert(env.Read().Get("A", 0).Value == "updated", "Updated lower value was lost"); Disable(env, db, s, a); Assert(env.Read().Get("A", 0).Value == "base", "Update recaptured the baseline");
        });
        Test("removing an entry recomputes later PATH groups", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base", 0, 1)); var db = new MemoryStore(); var a = P("first", E("Path", "A", 0, "Prepend"), E("KEEP", "keep", 0, "Set")); var b = P("second", E("Path", "B", 0, "Append")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b);
            var edited = s.Profiles.First(g => g.Id == a.Id); edited.Entries.RemoveAll(e => e.Name == "Path"); s = Enable(env, db, s, edited); Assert(env.Read().Get("Path", 0).Value == "base;B" && env.Read().Get("KEEP", 0).Value == "keep", "Removed entry survived in another baseline");
        });
        Test("composition does not apply drafts in unrelated active groups", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base", 0, 1)); var db = new MemoryStore(); var a = P("first", E("Path", "A", 0, "Prepend")); var b = P("second", E("Path", "B", 0, "Append")); var c = P("third", E("Path", "C", 0, "Prepend")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s.Profiles.First(g => g.Id == b.Id).Entries[0].Value = "draft";
            s = Enable(env, db, s, c); Assert(env.Read().Get("Path", 0).Value == "C;A;base;B" && s.Profiles.First(g => g.Id == b.Id).Entries[0].Value == "draft", "Pending edit was applied or discarded");
        });
        Test("enabling an already active group never duplicates it", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "base", 0, 1)); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, a); Assert(s.ActiveGroups.Count == 1 && s.ActiveGroups[0].Original[0].Value == "base", "Repeated enable duplicated ownership");
        });
        Test("raising priority uses applied definitions and can be undone", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("A", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s.Profiles.First(g => g.Id == a.Id).Entries[0].Value = "draft";
            s = new Engine(env, db).Execute(GroupPlanner.Prioritize(env.Read(), s, a), s); Assert(env.Read().Get("A", 0).Value == "one" && s.ActiveGroups.Last().ProfileId == a.Id, "Priority applied a pending edit"); s = Undo(env, db, s); Assert(env.Read().Get("A", 0).Value == "two" && s.ActiveGroups.Last().ProfileId == b.Id && s.Profiles.First(g => g.Id == a.Id).Entries[0].Value == "draft", "Priority undo lost group order or drafts");
        });
        Test("undoing enable restores all previous groups", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("B", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); var before = env.Read(); s = Enable(env, db, s, b); s = Undo(env, db, s); Same(before, env.Read()); Assert(s.ActiveGroups.Count == 1 && s.ActiveGroups[0].ProfileId == a.Id, "Enable undo removed other groups");
        });
        Test("undoing an out-of-order stop restores PATH and order", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base", 0, 1)); var db = new MemoryStore(); var a = P("one", E("Path", "A", 0, "Prepend")); var b = P("two", E("Path", "B", 0, "Append")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); var before = env.Read(); s = Disable(env, db, s, a); s = Undo(env, db, s); Same(before, env.Read()); Assert(s.ActiveGroups.Select(g => g.ProfileId).SequenceEqual(new[] { a.Id, b.Id }), "Stop undo changed ordering");
        });
        Test("multi-group state and history roundtrip through JSON", () => {
            var env = new MemoryEnvironment(); var store = new FileStore(Path.Combine(folder, "groups-persistence")); var a = P("one", E("Path", "%A%", 0, "Prepend")); var b = P("two", E("Path", "B", 0, "Append")); var s = new State(); s.Profiles.Add(a); s.Profiles.Add(b); store.Save(s);
            s = new Engine(env, store).Execute(GroupPlanner.SetEnabled(env.Read(), s, a, true), s); s = new Engine(env, store).Execute(GroupPlanner.SetEnabled(env.Read(), s, b, true), s); s = store.Load(); Assert(s.Version == 2 && s.ActiveGroups.Count == 2 && s.ActiveGroups[0].Entries[0].Mode == "Prepend", "Applied modes were not persisted");
            s = new Engine(env, store).Execute(Planner.Undo(env.Read(), s.History[0], s), s); Assert(s.ActiveGroups.Count == 1 && env.Read().Get("Path", 0).Value == "%A%", "Persisted undo failed");
        });
        Test("group-state changes after preview are rejected before writes", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("B", "two", 0, "Set")); var c = P("three", E("C", "three", 0, "Set")); var s = Enable(env, db, db.Load(), a); var plan = GroupPlanner.SetEnabled(env.Read(), s, b, true); s = Enable(env, db, s, c); int writes = env.Writes; Throws(() => new Engine(env, db).Execute(plan, s)); Assert(env.Writes == writes && s.ActiveGroups.Count == 2, "Stale preview overwrote enabled groups");
        });
        Test("second-group write failure preserves earlier groups", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("B", "two", 0, "Set"), E("C", "three", 0, "Set")); var s = Enable(env, db, db.Load(), a); var before = env.Read(); env.FailWrite = env.Writes + 2; Throws(() => Enable(env, db, s, b)); Same(before, env.Read()); Assert(db.Load().ActiveGroups.Count == 1 && db.Pending == null, "Failure changed existing groups");
        });
        Test("multi-group commit failure rolls back environment and group state", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("A", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); var before = env.Read(); db.FailCommit = true; Throws(() => Enable(env, db, s, b)); Same(before, env.Read()); Assert(db.Data.ActiveGroups.Count == 1 && db.Pending == null, "Commit failure lost the previous state");
        });
        Test("interrupted multi-group change restores the full earlier state", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("A", "two", 0, "Set"), E("B", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); var before = env.Read(); var plan = GroupPlanner.SetEnabled(env.Read(), s, b, true);
            db.Pending = new Pending { BeforeState = Json.Copy(s), Transaction = new History { Id = "multi-crash", Changes = plan.Changes, BeforeGroups = plan.BeforeGroups, AfterGroups = plan.NextGroups } }; env.Write(plan.Changes[0].After); s = new Engine(env, db).Recover(s); Same(before, env.Read()); Assert(s.ActiveGroups.Count == 1 && db.Pending == null, "Recovery lost the existing group");
        });
        Test("unrelated external changes do not block independent activation", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("B", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); env.Data.Put(V("A", "external", 0, 1)); s = Enable(env, db, s, b); Assert(env.Read().Get("A", 0).Value == "external" && env.Read().Get("B", 0).Value == "two", "Independent activation clobbered an external value"); Throws(() => GroupPlanner.DisableAll(env.Read(), s));
        });
        Test("external shared values require all owners to release management", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("A", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); env.Data.Put(V("A", "external", 0, 1)); Throws(() => GroupPlanner.SetEnabled(env.Read(), s, b, false, true));
            s = new Engine(env, db).Execute(GroupPlanner.DisableAll(env.Read(), s, true), s); Assert(s.ActiveGroups.Count == 0 && env.Read().Get("A", 0).Value == "external", "Release-all overwrote the external value");
        });
        Test("moving shared entries between active groups preserves other drafts", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "base", 0, 1)); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set"), E("LEFT", "left", 0, "Set")); var b = P("two", E("A", "two", 0, "Set"), E("KEEP", "keep", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s.Profiles.First(g => g.Id == b.Id).Entries.First(e => e.Name == "KEEP").Value = "draft";
            s = Move(env, db, s, "A", 0, a.Id, 0, b.Id); Assert(env.Read().Get("A", 0).Value == "one" && env.Read().Get("KEEP", 0).Value == "keep" && s.ActiveGroups.Count == 2, "Move changed an unrelated active entry"); s = new Engine(env, db).Execute(GroupPlanner.DisableAll(env.Read(), s), s); Assert(env.Read().Get("A", 0).Value == "base", "Move replaced the true baseline");
        });
        Test("batch moves recompute a third group's PATH and undo all ownership", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base", 0, 1)); var db = new MemoryStore(); var a = P("one", E("Path", "A", 0, "Prepend"), E("LEFT", "left", 0, "Set")); var b = P("two", E("Path", "B", 0, "Append"), E("KEEP", "keep", 0, "Set")); var c = P("three", E("Path", "C", 0, "Prepend")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s = Enable(env, db, s, c); var before = env.Read(); int records = s.History.Count;
            s = MoveMany(env, db, s, R("Path", 0, a.Id, 0, b.Id), R("LEFT", 0, a.Id, 0, b.Id)); Assert(s.ActiveGroups.Count == 2 && s.History.Count == records + 1 && env.Read().Get("Path", 0).Value == "C;A;base", "Batch move lost third-group composition"); s = Undo(env, db, s); Same(before, env.Read()); Assert(s.ActiveGroups.Count == 3 && s.ActiveGroups[0].ProfileId == a.Id, "Batch undo lost group ordering");
        });
        Test("moving a shared managed value to local cannot bypass other owners", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("A", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); var before = env.Read(); Throws(() => MovePlanner.Move(env.Read(), s, R("A", 0, a.Id, 0, null))); Same(before, env.Read()); Assert(s.ActiveGroups.Count == 2 && s.Profiles.First(g => g.Id == a.Id).Entries.Count == 1, "Failed move changed ownership");
        });
        Test("undoing ownership-only detachment rejects later external changes", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Move(env, db, s, "A", 0, a.Id, 0, null); Assert(s.History[0].Changes.Count == 0 && s.ActiveGroups.Count == 0, "Detachment should retain the value"); env.Data.Put(V("A", "external", 0, 1)); Throws(() => Planner.Undo(env.Read(), s.History[0], s)); Assert(env.Read().Get("A", 0).Value == "external", "Undo bypassed external changes");
        });
        Test("version-one migration preserves applied values and old history", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("Path", "X;base", 0, 1)); var db = new MemoryStore(); var a = P("legacy", E("Path", "pending edit", 0, "Prepend")); var lease = new Lease { ProfileId = a.Id, ProfileName = a.Name, Original = new List<Variable> { V("Path", "base", 0, 1) }, Applied = new List<Variable> { V("Path", "X;base", 0, 1) } };
            var old = new State { Version = 1, ActiveGroups = null, Active = lease }; old.Profiles.Add(a); old.History.Add(new History { Id = "old-enable", Label = "old", Changes = new List<Change> { new Change { Before = V("Path", "base", 0, 1), After = V("Path", "X;base", 0, 1) } }, BeforeLease = null, AfterLease = lease.Copy() });
            var path = Path.Combine(folder, "legacy-state.json"); Json.Write(path, old); var s = Json.Read<State>(path); FileStore.Check(s); Assert(s.Version == 2 && s.ActiveGroups.Count == 1 && s.Profiles[0].Entries[0].Value == "pending edit", "Migration discarded drafts or ownership");
            var restored = new Engine(env, db).Execute(Planner.Undo(env.Read(), s.History[0], s), s); Assert(restored.ActiveGroups.Count == 0 && env.Read().Get("Path", 0).Value == "base", "Legacy history cannot be undone"); env.Data.Put(V("Path", "X;base", 0, 1)); var b = P("new", E("Path", "B", 0, "Append")); s = Enable(env, db, s, b); Assert(env.Read().Get("Path", 0).Value == "X;base;B", "Migration applied a legacy draft"); s = Disable(env, db, s, a); Assert(env.Read().Get("Path", 0).Value == "base;B", "Migrated baseline was lost");
        });
        Test("legacy pending recovery migrates the restored group state", () => {
            var env = new MemoryEnvironment(); env.Data.Put(V("A", "changed", 0, 1)); var db = new MemoryStore(); var a = P("legacy", E("A", "old", 0, "Set")); var lease = new Lease { ProfileId = a.Id, ProfileName = a.Name, Original = new List<Variable> { Variable.Missing("A", 0) }, Applied = new List<Variable> { V("A", "old", 0, 1) } }; var old = new State { Version = 1, ActiveGroups = null, Active = lease }; old.Profiles.Add(a);
            db.Pending = new Pending { BeforeState = old, Transaction = new History { Id = "legacy-crash", Changes = new List<Change> { new Change { Before = V("A", "old", 0, 1), After = V("A", "changed", 0, 1) } } } }; var s = new Engine(env, db).Recover(new State()); Assert(s.Version == 2 && s.ActiveGroups.Count == 1 && env.Read().Get("A", 0).Value == "old", "Legacy recovery lost ownership");
        });
        Test("direct edits cannot bypass any enabled group", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("B", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); Throws(() => GroupPlanner.Edit(env.Read(), s, E("A", "external", 0, "Set"))); Throws(() => GroupPlanner.Edit(env.Read(), s, E("B", "external", 0, "Set")));
        });
        Test("unchanged shared values are rechecked after other writes", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "same", 0, "Set")); var b = P("two", E("A", "same", 0, "Set"), E("B", "new", 0, "Set")); var s = Enable(env, db, db.Load(), a); env.AfterWrite = i => env.Data.Put(V("A", "external", 0, 1));
            Throws(() => Enable(env, db, s, b)); Assert(env.Read().Get("A", 0).Value == "external" && !env.Read().Get("B", 0).Exists && db.Data.ActiveGroups.Count == 1 && db.Pending == null, "Unchanged-value race committed stale ownership");
        });
        Test("legacy ownership-only undo detects external edits", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var s = Enable(env, db, db.Load(), a); var originalLease = s.ActiveGroups[0].Copy(); s = Move(env, db, s, "A", 0, a.Id, 0, null); var h = s.History[0]; h.BeforeGroups = null; h.AfterGroups = null; h.BeforeLease = originalLease; h.AfterLease = null; h.AfterConditions = null;
            env.Data.Put(V("A", "external", 0, 1)); Throws(() => Planner.Undo(env.Read(), h, s)); Assert(env.Read().Get("A", 0).Value == "external", "Legacy undo ignored external changes");
        });
        Test("undo cannot reactivate a deleted group", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Disable(env, db, s, a); s.Profiles.Clear(); Throws(() => Planner.Undo(env.Read(), s.History[0], s)); Assert(!env.Read().Get("A", 0).Exists && s.ActiveGroups.Count == 0, "Undo resurrected an orphaned group");
        });        Test("undo preserves current group names in provenance", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("old name", E("A", "one", 0, "Set")); var b = P("other", E("B", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); s.Profiles.First(g => g.Id == a.Id).Name = "new name"; GroupState.Normalize(s); s = Undo(env, db, s); Assert(s.ActiveGroups.Single().ProfileName == "new name", "Undo restored an obsolete display name");
        });        Test("applying an emptied group stops only that group", () => {
            var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("one", E("A", "one", 0, "Set")); var b = P("two", E("B", "two", 0, "Set")); var s = Enable(env, db, db.Load(), a); s = Enable(env, db, s, b); var empty = s.Profiles.First(g => g.Id == a.Id); empty.Entries.Clear(); s = Enable(env, db, s, empty); Assert(s.ActiveGroups.Count == 1 && s.ActiveGroups[0].ProfileId == b.Id && !env.Read().Get("A", 0).Exists && env.Read().Get("B", 0).Value == "two", "Empty update affected another group");
        });
    }
    public static int Main(string[] args)
    {
        try
        {
            var realBefore = new WindowsEnvironment().Read();
            string startupBefore = StartupFingerprint();
            string scheduledBefore = ScheduledFingerprint();
            Test("activation preserves unrelated variables", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("JAVA_HOME", "old", 0, 1)); env.Data.Put(V("UNRELATED", "keep", 1, 1));
                var db = new MemoryStore(); var s = Switch(env, db, db.Load(), P("A", E("JAVA_HOME", "new", 0, "Set")));
                Assert(env.Read().Get("UNRELATED", 1).Value == "keep" && env.Read().Get("JAVA_HOME", 0).Value == "new", "Wrong activation");
                Assert(s.Active.Original[0].Value == "old" && db.Pending == null, "Backup missing");
            });
            Test("deactivation restores existing and absent values", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "%USERPROFILE%\\raw", 0, 2)); var before = env.Read(); var db = new MemoryStore();
                var s = Switch(env, db, db.Load(), P("A", E("A", "new", 0, "Set"), E("B", "temporary", 0, "Set"))); s = Stop(env, db, s); Same(before, env.Read()); Assert(s.Active == null, "Lease retained");
            });
            Test("switching overlapping profiles restores true baseline", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "original", 0, 1)); var db = new MemoryStore();
                var s = Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set"), E("B", "one", 0, "Set")));
                s = Switch(env, db, s, P("B", E("A", "two", 0, "Set"), E("C", "two", 0, "Set")));
                Assert(!env.Read().Get("B", 0).Exists && env.Read().Get("A", 0).Value == "two", "Old overlay leaked");
                Stop(env, db, s); Assert(env.Read().Get("A", 0).Value == "original" && !env.Read().Get("C", 0).Exists, "Original lost");
            });
            Test("active profile update restores removed entries", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var profile = P("A", E("A", "one", 0, "Set"), E("B", "one", 0, "Set"));
                var s = Switch(env, db, db.Load(), profile); profile.Entries.RemoveAt(1); profile.Entries[0].Value = "updated"; s = Switch(env, db, s, profile);
                Assert(!env.Read().Get("B", 0).Exists && env.Read().Get("A", 0).Value == "updated", "Update failed"); Stop(env, db, s); Assert(env.Read().Values.Count == 0, "Baseline failed");
            });
            Test("PATH prepend keeps precedence and removes duplicates", () => {
                string result = Planner.MergePath("C:\\one;C:\\two\\;C:\\three", "c:\\two;D:\\new", true);
                Assert(result == "c:\\two;D:\\new;C:\\one;C:\\three", "Order changed");
            });
            Test("PATH append retains original precedence", () => { Assert(Planner.MergePath("A;B", "b;C", false) == "A;B;C", "Append order wrong"); });
            Test("drive roots and drive-relative paths stay distinct", () => { Assert(Planner.MergePath("C:", "C:\\", false) == "C:;C:\\", "Root path was conflated"); });
            Test("same name in both scopes stays separate", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "user", 0, 1)); env.Data.Put(V("A", "machine", 1, 1)); var db = new MemoryStore();
                var s = Switch(env, db, db.Load(), P("A", E("A", "new", 0, "Set"))); Assert(env.Read().Get("A", 1).Value == "machine", "Wrong scope"); Stop(env, db, s);
            });
            Test("delete overlay restores raw value and kind", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "%RAW%", 1, 2)); var db = new MemoryStore();
                var s = Switch(env, db, db.Load(), P("delete", E("A", "", 1, "Delete"))); Assert(!env.Read().Get("A", 1).Exists, "Delete failed"); Stop(env, db, s); Assert(env.Read().Get("A", 1).Kind == 2 && env.Read().Get("A", 1).Value == "%RAW%", "Kind changed");
            });
            Test("external modifications block automatic overwrite", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var s = Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set"))); env.Data.Put(V("A", "external", 0, 1));
                Throws(() => Planner.Switch(env.Read(), s.Active, null, false)); Assert(env.Read().Get("A", 0).Value == "external", "Conflict overwritten");
            });
            Test("stop can keep external edits and restore other entries", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("B", "baseline", 0, 1)); var db = new MemoryStore(); var s = Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set"), E("B", "two", 0, "Set")));
                env.Data.Put(V("A", "external", 0, 1)); s = new Engine(env, db).Execute(Planner.Switch(env.Read(), s.Active, null, true), s);
                Assert(env.Read().Get("A", 0).Value == "external" && env.Read().Get("B", 0).Value == "baseline" && s.Active == null, "External preservation failed");
            });
            Test("preview race rejects the transaction before writes", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var plan = Planner.Switch(env.Read(), null, P("A", E("A", "one", 0, "Set")), false); env.Data.Put(V("A", "raced", 0, 1));
                Throws(() => new Engine(env, db).Execute(plan, db.Load())); Assert(env.Writes == 0 && db.Pending == null, "Raced write occurred");
            });
            Test("mid-transaction race rolls back earlier writes only", () => {
                var env = new MemoryEnvironment(); var before = env.Read(); var db = new MemoryStore(); env.AfterWrite = i => { if (i == 1) env.Data.Put(V("B", "external", 0, 1)); };
                Throws(() => Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set"), E("B", "two", 0, "Set"))));
                Assert(!env.Read().Get("A", 0).Exists && env.Read().Get("B", 0).Value == "external", "Race clobbered");
            });
            Test("write failure rolls back earlier entries", () => {
                var env = new MemoryEnvironment { FailWrite = 2 }; var before = env.Read(); var db = new MemoryStore();
                Throws(() => Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set"), E("B", "two", 0, "Set")))); Same(before, env.Read()); Assert(db.Pending == null && db.Load().Active == null, "Rollback incomplete");
            });
            Test("external edit immediately after write is preserved", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); env.AfterWrite = i => { if (i == 1) env.Data.Put(V("A", "external", 0, 1)); };
                Throws(() => Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set")))); Assert(env.Read().Get("A", 0).Value == "external" && db.Pending != null, "External final value lost");
            });
            Test("post-write failure rolls back the failing entry", () => {
                var env = new MemoryEnvironment { FailWrite = 2, FailAfterWrite = true }; var before = env.Read(); var db = new MemoryStore();
                Throws(() => Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set"), E("B", "two", 0, "Set")))); Same(before, env.Read());
            });
            Test("failed state save rolls back registry values", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore { FailCommit = true }; var before = env.Read();
                Throws(() => Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set")))); Same(before, env.Read()); Assert(db.Pending == null, "Journal retained unnecessarily");
            });
            Test("notification failure does not undo a committed change", () => {
                var env = new MemoryEnvironment { NotifyThrows = true }; var db = new MemoryStore(); var s = Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set")));
                Assert(env.Read().Get("A", 0).Value == "one" && s.Active != null, "Commit lost");
            });
            Test("interrupted transaction recovery restores originals", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var plan = Planner.Switch(env.Read(), null, P("A", E("A", "one", 0, "Set"), E("B", "two", 0, "Set")), false);
                db.SavePending(new Pending { BeforeState = db.Load(), Transaction = new History { Id = "interrupted", Changes = plan.Changes } }); env.Write(plan.Changes[0].After);
                new Engine(env, db).Recover(db.Load()); Assert(env.Read().Values.Count == 0 && db.Pending == null, "Recovery failed");
            });
            Test("recovery preserves foreign edits and keeps journal", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var plan = Planner.Switch(env.Read(), null, P("A", E("A", "one", 0, "Set")), false);
                db.SavePending(new Pending { BeforeState = db.Load(), Transaction = new History { Id = "crash", Changes = plan.Changes } }); env.Data.Put(V("A", "foreign", 0, 1));
                Throws(() => new Engine(env, db).Recover(db.Load())); Assert(db.Pending != null && env.Read().Get("A", 0).Value == "foreign", "Foreign edit lost");
            });
            Test("committed journal is cleaned without extra writes", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); db.Data.History.Add(new History { Id = "done" }); db.SavePending(new Pending { Transaction = new History { Id = "done" }, BeforeState = new State() });
                new Engine(env, db).Recover(db.Load()); Assert(env.Writes == 0 && db.Pending == null, "Commit replayed");
            });
            Test("undo reinstates the previous active profile", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var a = P("A", E("A", "one", 0, "Set")); var s = Switch(env, db, db.Load(), a); s = Switch(env, db, s, P("B", E("B", "two", 0, "Set")));
                s = new Engine(env, db).Execute(Planner.Undo(env.Read(), s.History[0]), s); Assert(s.Active.ProfileId == a.Id && env.Read().Get("A", 0).Value == "one" && !env.Read().Get("B", 0).Exists, "Undo wrong"); Stop(env, db, s); Assert(env.Read().Values.Count == 0, "Undo baseline corrupted");
            });
            Test("direct editing cannot bypass active lease", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var s = Switch(env, db, db.Load(), P("A", E("A", "one", 0, "Set"))); Throws(() => Planner.Edit(env.Read(), s.Active, E("A", "two", 0, "Set")));
            });
            Test("duplicate names compare without case", () => { Throws(() => Planner.Switch(new Snapshot(), null, P("A", E("Path", "A", 0, "Set"), E("PATH", "B", 0, "Set")), false)); });
            Test("protected OS variables cannot be modified", () => { Throws(() => Planner.Validate(E("SystemRoot", "changed", 1, "Set"))); Throws(() => Planner.Validate(E("ComSpec", "", 0, "Delete"))); });
            Test("unsupported registry kinds are refused", () => { var s = new Snapshot(); s.Put(V("A", "42", 0, 4)); Throws(() => Planner.Switch(s, null, P("A", E("A", "new", 0, "Set")), false)); });
            Test("invalid names and null characters are refused", () => { Throws(() => Planner.Validate(E("A=B", "one", 0, "Set"))); Throws(() => Planner.Validate(E("A", "bad\0", 0, "Set"))); });
            Test("long raw values are not truncated or expanded", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); string value = "%DO_NOT_EXPAND%\\" + new string('界', 8000); var s = Switch(env, db, db.Load(), P("long", E("A", value, 0, "Set"))); Assert(env.Read().Get("A", 0).Value == value && env.Read().Get("A", 0).Kind == 2, "Value changed"); Stop(env, db, s);
            });
            Test("file persistence retains backups and raw metadata", () => {
                string path = Path.Combine(args[0], "persistence"); var store = new FileStore(path); var state = new State(); state.Profiles.Add(P("中文方案", E("Path", "%RAW%\\路径", 0, "Prepend"))); store.Save(state); state.Profiles[0].Note = "第二次保存"; store.Save(state);
                var loaded = store.Load(); Assert(loaded.Profiles[0].Entries[0].Value == "%RAW%\\路径" && File.Exists(Path.Combine(path, "state.json.bak")), "Persistence failed");
            });
            Test("Windows registry adapter roundtrip in isolated scratch key", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N"); var adapter = new WindowsEnvironment(scratch);
                try
                {
                    var raw = V("RAW_TEST", "%USERPROFILE%\\" + new string('界', 6000), 0, 2); adapter.Write(raw); Assert(Variable.Equal(raw, adapter.Read().Get("RAW_TEST", 0)), "Native expansion/truncation failure");
                    var db = new MemoryStore(); var s = new Engine(adapter, db).Execute(Planner.Switch(adapter.Read(), null, P("native", E("RAW_TEST", "replaced", 0, "Set"), E("ADDED_TEST", "added", 0, "Set")), false), db.Load());
                    s = new Engine(adapter, db).Execute(Planner.Switch(adapter.Read(), s.Active, null, false), s); Assert(Variable.Equal(raw, adapter.Read().Get("RAW_TEST", 0)) && !adapter.Read().Get("ADDED_TEST", 0).Exists, "Native restore failure");
                    Throws(() => adapter.Write(V("FORBIDDEN_MACHINE", "value", 1, 1)));
                }
                finally
                {
                    Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
                    bool empty; using (var root = Registry.CurrentUser.OpenSubKey("Software\\SwitchEnv.Tests")) empty = root != null && root.SubKeyCount == 0 && root.ValueCount == 0;
                    if (empty) Registry.CurrentUser.DeleteSubKey("Software\\SwitchEnv.Tests", false);
                }
            });
            Test("portable startup command parses paths with spaces and Unicode", () => {
                string exe = @"C:\工具 With Spaces\SwitchEnv.exe";
                var startup = new WindowsStartup(exe, @"C:\工具 With Spaces\data\"); var parsed = ParseCommand(startup.Command);
                Assert(parsed.Length == 2 && parsed[0] == exe && parsed[1] == "--startup", "Default data directory changed or executable path split");
            });
            Test("startup command preserves custom and drive-root data directories", () => {
                foreach (string data in new[] { @"D:\自定义 Data", @"D:\" })
                {
                    var startup = new WindowsStartup(@"C:\Tools\SwitchEnv.exe", data); var parsed = ParseCommand(startup.Command);
                    Assert(parsed.Length == 4 && parsed[1] == "--startup" && parsed[2] == "--data-dir" && Path.GetFullPath(parsed[3]) == Path.GetFullPath(data), "Data argument did not roundtrip");
                }
            });
            Test("highest startup registration persists and disabling preserves other entries", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N");
                try
                {
                    var tasks = new MemoryStartupTasks(); tasks.Items["OtherApplication"] = new StartupTask { Enabled = true, Xml = "preserve me" };
                    var startup = Startup(tasks, scratch, @"C:\Tools\SwitchEnv.exe", @"C:\Tools\data");
                    Assert(!startup.IsEnabled, "Startup enabled by default"); startup.SetEnabled(false);
                    using (var absent = Registry.CurrentUser.OpenSubKey(scratch)) Assert(absent == null, "Read or disable created a key");
                    using (var key = Registry.CurrentUser.CreateSubKey(scratch)) key.SetValue("OtherApplication", "preserve me", RegistryValueKind.String);
                    startup.SetEnabled(true); startup.SetEnabled(true);
                    Assert(Startup(tasks, scratch, @"C:\Tools\SwitchEnv.exe", @"C:\Tools\data").IsEnabled, "Startup registration not persisted");
                    Assert(tasks.Items.Count == 2 && tasks.Read(startup.TaskName).Xml.Contains("HighestAvailable"), "Highest task missing");
                    startup.SetEnabled(false); startup.SetEnabled(false);
                    using (var key = Registry.CurrentUser.OpenSubKey(scratch)) Assert(key.ValueCount == 1 && (string)key.GetValue("OtherApplication") == "preserve me", "Another startup entry changed");
                    Assert(!startup.IsEnabled && tasks.Items.Count == 1 && tasks.Read("OtherApplication").Xml == "preserve me", "Another task changed or startup still enabled");
                }
                finally { CleanupScratch(scratch); }
            });
            Test("disabling does not remove a changed startup registration", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N");
                try
                {
                    var tasks = new MemoryStartupTasks();
                    var first = Startup(tasks, scratch, @"C:\Tools\SwitchEnv.exe", @"C:\Tools\data"); first.SetEnabled(true);
                    var other = Startup(tasks, scratch, @"D:\Tools\SwitchEnv.exe", @"D:\Tools\data"); other.SetEnabled(true);
                    Assert(!first.IsEnabled && other.IsEnabled, "Changed registration not detected"); Throws(() => first.SetEnabled(false));
                    Assert(other.IsEnabled, "Changed registration was deleted"); other.SetEnabled(false);
                }
                finally { CleanupScratch(scratch); }
            });
            Test("scheduled startup stores long combined commands in separate fields", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N");
                try
                {
                    var tasks = new MemoryStartupTasks(); string exe = @"C:\" + new string('a', 120) + @"\SwitchEnv.exe", data = @"D:\" + new string('b', 120);
                    var startup = Startup(tasks, scratch, exe, data);
                    Assert(startup.Command.Length > 260, "Test command too short"); startup.SetEnabled(true);
                    XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task"; var exec = XDocument.Parse(tasks.Read(startup.TaskName).Xml).Descendants(ns + "Exec").Single();
                    var parsed = ParseCommand(AdminBootstrap.Quote((string)exec.Element(ns + "Command")) + " " + (string)exec.Element(ns + "Arguments"));
                    Assert(parsed[0] == exe && parsed[3] == data && parsed[5] == CurrentSid() && startup.IsEnabled, "Executable and data did not roundtrip");
                    using (var key = Registry.CurrentUser.OpenSubKey(scratch)) Assert(key == null, "A Run key was created");
                }
                finally { CleanupScratch(scratch); }
            });
            Test("legacy startup migrates only after a highest task is registered", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N");
                try
                {
                    var tasks = new MemoryStartupTasks(); var startup = Startup(tasks, scratch, @"C:\工具 With Spaces\SwitchEnv.exe", @"D:\Custom Data\");
                    using (var key = Registry.CurrentUser.CreateSubKey(scratch)) { key.SetValue("SwitchEnv", startup.Command); key.SetValue("OtherApplication", "keep"); }
                    Assert(startup.IsLegacyEnabled && !startup.IsEnabled, "Legacy registration was not recognized"); startup.MigrateLegacyIfNeeded();
                    Assert(startup.IsEnabled && !startup.IsLegacyEnabled, "Migration left the legacy entry or missed the task");
                    using (var key = Registry.CurrentUser.OpenSubKey(scratch)) Assert(key.ValueCount == 1 && (string)key.GetValue("OtherApplication") == "keep", "Unrelated entry changed");
                    string before = tasks.Read(startup.TaskName).Xml; startup.MigrateLegacyIfNeeded(); Assert(tasks.Read(startup.TaskName).Xml == before, "Repeated migration changed the task");
                }
                finally { CleanupScratch(scratch); }
            });
            Test("failed registration keeps legacy startup and the previous task", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N");
                try
                {
                    var tasks = new MemoryStartupTasks(); var startup = Startup(tasks, scratch, @"C:\Tools\SwitchEnv.exe", @"C:\Tools\data");
                    using (var key = Registry.CurrentUser.CreateSubKey(scratch)) key.SetValue("SwitchEnv", startup.Command);
                    tasks.FailRegister = true; Throws(startup.MigrateLegacyIfNeeded);
                    Assert(startup.IsLegacyEnabled && tasks.Read(startup.TaskName) == null, "Failed migration changed legacy startup");
                    tasks.FailRegister = false; startup.SetEnabled(true); string before = tasks.Read(startup.TaskName).Xml;
                    tasks.FailRegister = true; Throws(() => startup.SetEnabled(true)); Assert(startup.IsEnabled && before == tasks.Read(startup.TaskName).Xml, "Failed update replaced a task");
                    tasks.FailDelete = true; Throws(() => startup.SetEnabled(false)); Assert(startup.IsEnabled, "Failed deletion lost the task");
                }
                finally { CleanupScratch(scratch); }
            });
            Test("migration leaves other locations and disabled legacy startup alone", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N");
                try
                {
                    var tasks = new MemoryStartupTasks(); var startup = Startup(tasks, scratch, @"C:\Tools\SwitchEnv.exe", @"C:\Tools\data");
                    string other = "\"D:\\Other\\SwitchEnv.exe\" --startup";
                    using (var key = Registry.CurrentUser.CreateSubKey(scratch)) key.SetValue("SwitchEnv", other);
                    startup.MigrateLegacyIfNeeded(); Assert(!startup.IsEnabled && tasks.Items.Count == 0, "An unrelated registration was migrated");
                    startup.SetEnabled(true); startup.SetEnabled(false);
                    using (var key = Registry.CurrentUser.OpenSubKey(scratch)) Assert((string)key.GetValue("SwitchEnv") == other, "Another location's legacy entry was deleted");
                }
                finally { CleanupScratch(scratch); }
            });
            Test("startup status rejects disabled or non-elevated and foreign-user tasks", () => {
                var tasks = new MemoryStartupTasks(); var startup = Startup(tasks, "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N"), @"C:\Tools\SwitchEnv.exe", @"C:\Tools\data");
                tasks.Register(startup.TaskName, startup.TaskXml, CurrentSid()); Assert(startup.IsEnabled, "Expected highest task was not recognized");
                tasks.Items[startup.TaskName].Enabled = false; Assert(!startup.IsEnabled, "Disabled task was reported enabled");
                tasks.Items[startup.TaskName] = new StartupTask { Enabled = true, Xml = startup.TaskXml.Replace("HighestAvailable", "LeastPrivilege") }; Assert(!startup.IsEnabled, "Ordinary task was reported elevated");
                tasks.Items[startup.TaskName].Xml = startup.TaskXml.Replace(CurrentSid(), "S-1-5-18"); Assert(!startup.IsEnabled, "Another user's task was accepted");
                Throws(() => startup.SetEnabled(false)); Assert(tasks.Items.Count == 1, "Foreign task was deleted");
            });
            Test("Task Scheduler accepts native highest interactive logon XML without registering", () => {
                var startup = new WindowsStartup(@"C:\工具 With Spaces\SwitchEnv.exe", @"D:\Custom Data\");
                dynamic service = null, definition = null, principal = null, actions = null, action = null, triggers = null, trigger = null, settings = null;
                try
                {
                    service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); service.Connect(); definition = service.NewTask(0); definition.XmlText = startup.TaskXml;
                    principal = definition.Principal; actions = definition.Actions; action = actions.Item(1); triggers = definition.Triggers; trigger = triggers.Item(1); settings = definition.Settings;
                    Assert((int)principal.RunLevel == 1 && (int)principal.LogonType == 3, "Incorrect run level or logon type");
                    Assert(UserSid((string)principal.UserId) == CurrentSid(), "Principal resolved to a different user");
                    Assert((int)actions.Count == 1 && (string)action.Path == startup.Executable && (string)action.Arguments == startup.Arguments && (string)action.WorkingDirectory == Path.GetDirectoryName(startup.Executable), "Incorrect executable or arguments");
                    Assert((int)triggers.Count == 1 && (int)trigger.Type == 9 && UserSid((string)trigger.UserId) == CurrentSid() && (string)trigger.Delay == "PT5S", "Logon trigger did not target the current user");
                    Assert(!(bool)settings.DisallowStartIfOnBatteries && !(bool)settings.StopIfGoingOnBatteries && (string)settings.ExecutionTimeLimit == "PT0S", "Tray process would stop on battery or timeout");
                    var tasks = new MemoryStartupTasks(); var normalized = Startup(tasks, "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N"), startup.Executable, @"D:\Custom Data\");
                    tasks.Register(normalized.TaskName, (string)definition.XmlText, CurrentSid()); Assert(normalized.IsEnabled, "Windows-normalized task XML was not recognized");
                }
                finally { ReleaseCom((object)settings, (object)trigger, (object)triggers, (object)action, (object)actions, (object)principal, (object)definition, (object)service); }
            });
            Test("elevation preserves Windows arguments and replaces the expected account", () => {
                string[] argsForChild = { "--data-dir", @"D:\", "--startup", "--expected-user-sid", "old", "--error-log", @"C:\日志 with spaces\错误.txt", "literal \"quotes\" and \\slashes\\", "" };
                var parsed = ParseCommand("SwitchEnv.exe " + AdminBootstrap.ChildArguments(argsForChild, CurrentSid())).Skip(1).ToArray();
                var expected = argsForChild.Take(3).Concat(argsForChild.Skip(5)).Concat(new[] { "--expected-user-sid", CurrentSid() }).ToArray();
                Assert(parsed.SequenceEqual(expected), "Elevation changed startup, data, logging or quoted arguments");
                Assert(parsed.Count(a => a == "--expected-user-sid") == 1, "Expected account appeared more than once");
            });
            Test("elevation rejects an account change before opening user data", () => {
                AdminBootstrap.CheckOwner(CurrentSid(), CurrentSid()); AdminBootstrap.CheckOwner(null, CurrentSid());
                Throws(() => AdminBootstrap.CheckOwner(CurrentSid(), "S-1-5-18"));
            });
            Test("scope moves preserve raw values and restore overwritten destinations", () => {
                foreach (int from in new[] { 0, 1 })
                {
                    var env = new MemoryEnvironment(); var db = new MemoryStore(); env.Data.Put(V("RAW", "%REF%\\中文", from, 2)); env.Data.Put(V("raw", "destination-before", 1 - from, 1)); env.Data.Put(V("KEEP", "unchanged", 0, 1)); var original = env.Read();
                    var s = Move(env, db, db.Load(), "RAW", from, null, 1 - from, null);
                    Assert(!env.Read().Get("RAW", from).Exists && env.Read().Get("RAW", 1 - from).Value == "%REF%\\中文" && env.Read().Get("RAW", 1 - from).Kind == 2, "Scope move lost value or kind");
                    s = Undo(env, db, s); Same(original, env.Read());
                }
            });
            Test("local to inactive profile commits removal and saved definition together", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("JAVA_HOME", "%SDK%\\jdk", 1, 2)); var db = new MemoryStore(); var target = P("target"); db.Data.Profiles.Add(target);
                var s = Move(env, db, db.Load(), "JAVA_HOME", 1, null, 0, target.Id); var entry = s.Profiles[0].Entries.Single();
                Assert(!env.Read().Get("JAVA_HOME", 1).Exists && !env.Read().Get("JAVA_HOME", 0).Exists && entry.Scope == 0 && entry.Kind == 2 && entry.Value == "%SDK%\\jdk", "Local transfer changed semantics");
                Assert(s.History[0].ProfileChanges.Count == 1 && db.Pending == null, "Definition not journaled"); s = Undo(env, db, s);
                Assert(env.Read().Get("JAVA_HOME", 1).Value == "%SDK%\\jdk" && s.Profiles[0].Entries.Count == 0, "Transfer undo incomplete");
            });
            Test("profile to local evaluates PATH operation and undo restores declaration", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base;shared", 0, 1)); var db = new MemoryStore(); var source = P("source", E("Path", "%SDK%\\bin;shared", 0, "Prepend")); db.Data.Profiles.Add(source); var original = env.Read();
                var s = Move(env, db, db.Load(), "Path", 0, source.Id, 0, null);
                Assert(env.Read().Get("Path", 0).Value == "%SDK%\\bin;shared;base" && env.Read().Get("Path", 0).Kind == 2 && s.Profiles[0].Entries.Count == 0, "PATH transfer did not apply operation");
                s = Undo(env, db, s); Same(original, env.Read()); Assert(s.Profiles[0].Entries[0].Mode == "Prepend", "PATH operation was lost");
            });
            Test("between-profile moves preserve mode and undo restores overwritten entry", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var source = P("source", E("Path", "%RAW%\\bin", 1, "Append"), E("KEEP", "stay", 0, "Set")); var target = P("target", E("Path", "old-target", 0, "Set")); db.Data.Profiles.Add(source); db.Data.Profiles.Add(target);
                var s = Move(env, db, db.Load(), "Path", 1, source.Id, 0, target.Id);
                Assert(s.Profiles[0].Entries.Single().Name == "KEEP" && s.Profiles[1].Entries.Single().Mode == "Append" && s.Profiles[1].Entries[0].Kind == 2 && env.Writes == 0, "Definition move touched environment or lost metadata");
                s = Undo(env, db, s); Assert(s.Profiles[0].Entries.Count == 2 && s.Profiles[1].Entries.Single().Value == "old-target" && env.Writes == 0, "Definition undo overwrote unrelated content");
            });
            Test("a profile variable can change scope and same-location moves are refused", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var source = P("source", E("RAW", "%REF%", 0, "Set")); db.Data.Profiles.Add(source);
                var s = Move(env, db, db.Load(), "RAW", 0, source.Id, 1, source.Id); Assert(s.Profiles[0].Entries.Single().Scope == 1 && env.Writes == 0, "Profile scope change failed");
                Throws(() => Move(env, db, s, "RAW", 1, source.Id, 1, source.Id)); s = Undo(env, db, s); Assert(s.Profiles[0].Entries.Single().Scope == 0, "Profile scope undo failed");
            });
            Test("active profile to local detaches ownership and stop preserves moved value", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "base", 0, 1)); var db = new MemoryStore(); var source = P("source", E("A", "active", 0, "Set"), E("KEEP", "managed", 0, "Set")); db.Data.Profiles.Add(source);
                var s = Switch(env, db, db.Load(), source); s = Move(env, db, s, "A", 0, source.Id, 0, null);
                Assert(s.Active.Applied.All(v => v.Name != "A") && env.Read().Get("A", 0).Value == "active", "Local transfer still leased");
                s = Undo(env, db, s); Assert(s.Active.Applied.Any(v => v.Name == "A") && s.Profiles[0].Entries.Count == 2, "Undo did not restore ownership");
                s = Move(env, db, s, "A", 0, source.Id, 0, null); s = Stop(env, db, s); Assert(env.Read().Get("A", 0).Value == "active" && !env.Read().Get("KEEP", 0).Exists, "Stopping changed a detached variable");
            });
            Test("active to inactive profile restores baseline and empty lease stops", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "base", 0, 1)); var db = new MemoryStore(); var source = P("source", E("A", "active", 0, "Set")); var target = P("target"); db.Data.Profiles.Add(source); db.Data.Profiles.Add(target);
                var s = Switch(env, db, db.Load(), source); s = Move(env, db, s, "A", 0, source.Id, 0, target.Id);
                Assert(s.Active == null && env.Read().Get("A", 0).Value == "base" && s.Profiles[0].Entries.Count == 0 && s.Profiles[1].Entries.Single().Value == "active", "Active move left stale override");
                s = Undo(env, db, s); Assert(s.Active.ProfileId == source.Id && env.Read().Get("A", 0).Value == "active" && s.Profiles[1].Entries.Count == 0, "Undo did not restore active source");
            });
            Test("moving into active destination retains its original baseline", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "baseline", 0, 1)); var db = new MemoryStore(); var source = P("source", E("A", "new", 0, "Set")); var target = P("active", E("A", "old-active", 0, "Set")); db.Data.Profiles.Add(source); db.Data.Profiles.Add(target);
                var s = Switch(env, db, db.Load(), target); s = Move(env, db, s, "A", 0, source.Id, 0, target.Id);
                Assert(env.Read().Get("A", 0).Value == "new" && s.Active.Original.Single().Value == "baseline", "Active destination baseline overwritten");
                s = Undo(env, db, s); Assert(env.Read().Get("A", 0).Value == "old-active" && s.Profiles[0].Entries.Single().Value == "new", "Active destination undo failed");
                s = Move(env, db, s, "A", 0, source.Id, 0, target.Id); s = Stop(env, db, s); Assert(env.Read().Get("A", 0).Value == "baseline", "Stop restored wrong active baseline");
            });
            Test("local value acquired by active profile uses the removed source as baseline", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("RAW", "local", 0, 1)); var db = new MemoryStore(); var target = P("active", E("KEEP", "active", 0, "Set")); db.Data.Profiles.Add(target);
                var s = Switch(env, db, db.Load(), target); s = Move(env, db, s, "RAW", 0, null, 0, target.Id);
                Assert(!s.Active.Original.First(v => v.Name == "RAW").Exists && env.Read().Get("RAW", 0).Value == "local", "Transfer kept the source baseline");
                s = Undo(env, db, s); Assert(s.Active.Applied.All(v => v.Name != "RAW") && s.Profiles[0].Entries.Count == 1 && env.Read().Get("RAW", 0).Value == "local", "Acquisition undo removed original local value");
                s = Move(env, db, s, "RAW", 0, null, 0, target.Id); s = Stop(env, db, s); Assert(!env.Read().Get("RAW", 0).Exists, "Stopped profile resurrected a removed source");
            });
            Test("active profile scope move and undo retain both scope baselines", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "user-base", 0, 1)); env.Data.Put(V("A", "system-base", 1, 1)); var db = new MemoryStore(); var source = P("active", E("A", "user-active", 0, "Set"), E("A", "system-active", 1, "Set")); db.Data.Profiles.Add(source);
                var s = Switch(env, db, db.Load(), source); var before = env.Read(); s = Move(env, db, s, "A", 0, source.Id, 1, source.Id);
                Assert(env.Read().Get("A", 0).Value == "user-base" && env.Read().Get("A", 1).Value == "user-active" && s.Active.Applied.Count == 1, "Active scope migration failed");
                s = Undo(env, db, s); Same(before, env.Read()); s = Stop(env, db, s); Assert(env.Read().Get("A", 0).Value == "user-base" && env.Read().Get("A", 1).Value == "system-base", "Scope baselines not restored");
            });
            Test("managed local entries and externally edited active entries are guarded", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var active = P("active", E("A", "managed", 0, "Set")); var other = P("other", E("A", "other", 0, "Set")); db.Data.Profiles.Add(active); db.Data.Profiles.Add(other);
                var s = Switch(env, db, db.Load(), active); int writes = env.Writes;
                Throws(() => Move(env, db, s, "A", 0, null, 1, null)); Throws(() => Move(env, db, s, "A", 0, other.Id, 0, null));
                env.Data.Put(V("A", "external", 0, 1)); Throws(() => Move(env, db, s, "A", 0, active.Id, 0, other.Id));
                Assert(env.Writes == writes && env.Read().Get("A", 0).Value == "external" && s.Profiles.All(p => p.Entries.Count == 1), "Guarded move wrote changes");
            });
            Test("move preview detects destination races before deleting source", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); env.Data.Put(V("A", "source", 0, 1)); var s = db.Load(); var plan = MovePlanner.Move(env.Read(), s, new MoveRequest { Name = "A", SourceScope = 0, TargetScope = 1 });
                env.Data.Put(V("A", "external", 1, 1)); Throws(() => new Engine(env, db).Execute(plan, s));
                Assert(env.Writes == 0 && env.Read().Get("A", 0).Value == "source" && env.Read().Get("A", 1).Value == "external", "Move race lost a value");
            });
            Test("move write failure rolls back environment and keeps profile definitions", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "system-source", 1, 1)); var db = new MemoryStore(); var target = P("active", E("A", "user-active", 0, "Set")); db.Data.Profiles.Add(target); var s = Switch(env, db, db.Load(), target); var before = env.Read();
                env.FailWrite = env.Writes + 2; Throws(() => Move(env, db, s, "A", 1, null, 0, target.Id)); Same(before, env.Read());
                Assert(db.Load().Profiles[0].Entries[0].Value == "user-active" && db.Pending == null && s.Profiles[0].Entries[0].Value == "user-active", "Failed move changed definitions");
            });
            Test("move state-save failure restores the local source", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "source", 0, 1)); var db = new MemoryStore(); var target = P("target"); db.Data.Profiles.Add(target); var s = db.Load(); db.FailCommit = true;
                Throws(() => Move(env, db, s, "A", 0, null, 0, target.Id)); Assert(env.Read().Get("A", 0).Value == "source" && db.Load().Profiles[0].Entries.Count == 0 && db.Pending == null, "Failed save left a partial move");
            });
            Test("interrupted move journal restores source and complete profile state", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "source", 0, 1)); var db = new MemoryStore(); var target = P("target"); db.Data.Profiles.Add(target); var s = db.Load(); var plan = MovePlanner.Move(env.Read(), s, new MoveRequest { Name = "A", SourceScope = 0, TargetScope = 0, TargetProfileId = target.Id });
                db.Pending = new Pending { BeforeState = Json.Copy(s), Transaction = new History { Id = "interrupted-move", Changes = plan.Changes, ProfileChanges = plan.ProfileChanges } }; foreach (var change in plan.Changes) env.Write(change.After);
                s = new Engine(env, db).Recover(s); Assert(env.Read().Get("A", 0).Value == "source" && s.Profiles[0].Entries.Count == 0 && db.Pending == null, "Interrupted move not fully recovered");
            });
            Test("move undo refuses changed definitions and persisted history retains metadata", () => {
                string path = Path.Combine(args[0], "move-persistence"); var store = new FileStore(path); var env = new MemoryEnvironment(); var source = P("source", E("Path", "%RAW%\\bin", 1, "Append")); var target = P("target", E("Path", "previous", 1, "Prepend")); var s = new State(); s.Profiles.Add(source); s.Profiles.Add(target); store.Save(s);
                s = new Engine(env, store).Execute(MovePlanner.Move(env.Read(), s, new MoveRequest { Name = "Path", SourceScope = 1, SourceProfileId = source.Id, TargetScope = 1, TargetProfileId = target.Id }), s); s = store.Load();
                Assert(s.History[0].ProfileChanges.Count == 2 && s.Profiles[1].Entries[0].Mode == "Append", "Persisted move metadata missing");
                var original = Json.Copy(s); s.Profiles[1].Entries[0].Value = "external-definition"; Throws(() => Planner.Undo(env.Read(), s.History[0], s)); Assert(env.Writes == 0, "Undo changed environment before conflict");
                s = new Engine(env, store).Execute(Planner.Undo(env.Read(), original.History[0], original), original); Assert(s.Profiles[0].Entries[0].Mode == "Append" && s.Profiles[1].Entries[0].Value == "previous", "Persisted move undo failed");
            });
            Test("native local-profile moves retain raw long and empty registry strings", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N"); var adapter = new WindowsEnvironment(scratch);
                try
                {
                    foreach (var raw in new[] { V("RAW", "%NO_EXPAND%\\" + new string('界', 6000), 0, 2), V("EMPTY", "", 0, 1) })
                    {
                        adapter.Write(raw); var db = new MemoryStore(); var target = P("target"); db.Data.Profiles.Add(target); var s = db.Load();
                        s = new Engine(adapter, db).Execute(MovePlanner.Move(adapter.Read(), s, new MoveRequest { Name = raw.Name, SourceScope = 0, TargetScope = 0, TargetProfileId = target.Id }), s);
                        Assert(!adapter.Read().Get(raw.Name, 0).Exists && s.Profiles[0].Entries[0].Value == raw.Value && s.Profiles[0].Entries[0].Kind == raw.Kind, "Native transfer lost raw string");
                        s = new Engine(adapter, db).Execute(Planner.Undo(adapter.Read(), s.History[0], s), s);
                        Assert(Variable.Equal(raw, adapter.Read().Get(raw.Name, 0)) && s.Profiles[0].Entries.Count == 0, "Native transfer undo failed");
                    }
                }
                finally { CleanupScratch(scratch); }
            });
            Test("batch scope moves commit once and undo every overwritten destination", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "%RAW%\\原始", 1, 2)); env.Data.Put(V("B", "", 1, 1)); env.Data.Put(V("A", "old user", 0, 1)); env.Data.Put(V("UNRELATED", "keep", 0, 1)); var before = env.Read(); var db = new MemoryStore();
                var s = MoveMany(env, db, db.Load(), R("A", 1, null, 0, null), R("B", 1, null, 0, null));
                Assert(s.History.Count == 1 && s.History[0].Changes.Count == 4 && db.Pending == null, "Batch was not one committed record");
                Assert(env.Read().Get("A", 0).Kind == 2 && env.Read().Get("A", 0).Value == "%RAW%\\原始" && env.Read().Get("B", 0).Exists && env.Read().Get("B", 0).Value == "" && !env.Read().Get("A", 1).Exists, "Batch changed raw values or left source");
                Undo(env, db, s); Same(before, env.Read());
            });
            Test("batch local-profile transfer keeps identical names in separate scopes", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("SAME", "user", 0, 1)); env.Data.Put(V("SAME", "%SYSTEM_RAW%", 1, 2)); env.Data.Put(V("EMPTY", "", 1, 1)); var before = env.Read(); var db = new MemoryStore(); var target = P("target"); db.Data.Profiles.Add(target);
                var s = MoveMany(env, db, db.Load(), R("SAME", 0, null, 0, target.Id), R("SAME", 1, null, 1, target.Id), R("EMPTY", 1, null, 1, target.Id));
                Assert(s.History.Count == 1 && s.Profiles[0].Entries.Count == 3 && env.Read().Values.Count == 0, "Mixed scopes were collapsed or partially moved");
                Assert(s.Profiles[0].Entries.First(e => e.Scope == 1 && e.Name == "SAME").Kind == 2, "Raw kind changed"); s = Undo(env, db, s); Same(before, env.Read()); Assert(s.Profiles[0].Entries.Count == 0, "Batch definitions were not undone");
            });
            Test("batch profile-local transfer evaluates each PATH and delete operation", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base;shared", 0, 1)); env.Data.Put(V("REMOVE", "old", 0, 1)); var before = env.Read(); var db = new MemoryStore(); var source = P("source", E("Path", "%SDK%\\bin;shared", 0, "Prepend"), E("EMPTY", "", 1, "Set"), E("REMOVE", "", 1, "Delete")); db.Data.Profiles.Add(source);
                var s = MoveMany(env, db, db.Load(), R("Path", 0, source.Id, 0, null), R("EMPTY", 1, source.Id, 0, null), R("REMOVE", 1, source.Id, 0, null));
                Assert(env.Read().Get("Path", 0).Value == "%SDK%\\bin;shared;base" && env.Read().Get("Path", 0).Kind == 2 && env.Read().Get("EMPTY", 0).Exists && !env.Read().Get("REMOVE", 0).Exists && s.Profiles[0].Entries.Count == 0, "Operations were not applied together");
                s = Undo(env, db, s); Same(before, env.Read()); Assert(s.Profiles[0].Entries.First(e => e.Name == "Path").Mode == "Prepend" && s.Profiles[0].Entries.Count == 3, "Batch operation metadata was lost");
            });
            Test("batch between-profile moves preserve modes and restore old definitions", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var source = P("source", E("Path", "%RAW%\\bin", 1, "Append"), E("A", "new", 0, "Set"), E("KEEP", "stay", 0, "Set")); var target = P("target", E("Path", "old path", 1, "Prepend"), E("A", "old A", 0, "Set")); db.Data.Profiles.Add(source); db.Data.Profiles.Add(target);
                var s = MoveMany(env, db, db.Load(), R("Path", 1, source.Id, 1, target.Id), R("A", 0, source.Id, 0, target.Id));
                Assert(env.Writes == 0 && s.History.Count == 1 && s.History[0].ProfileChanges.Count == 4 && s.Profiles[0].Entries.Single().Name == "KEEP", "Definitions were split across commits or environment changed");
                s = Undo(env, db, s); Assert(s.Profiles[0].Entries.Count == 3 && s.Profiles[1].Entries.First(e => e.Name == "Path").Mode == "Prepend" && s.Profiles[1].Entries.First(e => e.Name == "A").Value == "old A" && env.Writes == 0, "Overwritten target definitions were not restored");
            });
            Test("batch moving all active entries restores baselines and retains draft values", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "base", 0, 1)); var baseline = env.Read(); var db = new MemoryStore(); var source = P("source", E("A", "active A", 0, "Set"), E("B", "active B", 1, "Set")); var target = P("target"); db.Data.Profiles.Add(source); db.Data.Profiles.Add(target);
                var s = Switch(env, db, db.Load(), source); var applied = env.Read(); s.Profiles[0].Entries.First(e => e.Name == "B").Value = "pending edit";
                s = MoveMany(env, db, s, R("A", 0, source.Id, 0, target.Id), R("B", 1, source.Id, 1, target.Id));
                Same(baseline, env.Read()); Assert(s.Active == null && s.Profiles[0].Entries.Count == 0 && s.Profiles[1].Entries.First(e => e.Name == "B").Value == "pending edit" && s.History.Count == 2, "Active lease or pending edit was lost");
                s = Undo(env, db, s); Same(applied, env.Read()); Assert(s.Active.Applied.Count == 2 && s.Profiles[0].Entries.First(e => e.Name == "B").Value == "pending edit" && s.Profiles[1].Entries.Count == 0, "Active batch undo was incomplete");
            });
            Test("batch active target retains old and newly acquired baselines", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "base A", 0, 1)); env.Data.Put(V("B", "%BASE_B%", 1, 2)); var baseline = env.Read(); var db = new MemoryStore(); var source = P("source", E("A", "new A", 0, "Set"), E("B", "%NEW_B%", 1, "Set")); var target = P("active", E("A", "old active A", 0, "Set"), E("KEEP", "active keep", 0, "Set")); db.Data.Profiles.Add(source); db.Data.Profiles.Add(target);
                var s = Switch(env, db, db.Load(), target); var beforeMove = env.Read();
                s = MoveMany(env, db, s, R("A", 0, source.Id, 0, target.Id), R("B", 1, source.Id, 1, target.Id));
                Assert(s.Active.Original.First(v => v.Name == "A").Value == "base A" && s.Active.Original.First(v => v.Name == "B").Value == "%BASE_B%" && env.Read().Get("KEEP", 0).Value == "active keep", "Batch changed restore baselines or unrelated managed values");
                s = Undo(env, db, s); Same(beforeMove, env.Read()); Assert(s.Profiles[0].Entries.Count == 2 && s.Profiles[1].Entries.Count == 2, "Active destination undo failed");
                s = MoveMany(env, db, s, R("A", 0, source.Id, 0, target.Id), R("B", 1, source.Id, 1, target.Id)); Stop(env, db, s); Same(baseline, env.Read());
            });
            Test("batch active-profile to local detaches every selected variable", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("Path", "base", 0, 1)); var db = new MemoryStore(); var source = P("active", E("Path", "new", 0, "Prepend"), E("A", "active A", 1, "Set")); db.Data.Profiles.Add(source);
                var s = Switch(env, db, db.Load(), source); var applied = env.Read();
                s = MoveMany(env, db, s, R("Path", 0, source.Id, 0, null), R("A", 1, source.Id, 1, null));
                Same(applied, env.Read()); Assert(s.Active == null && s.Profiles[0].Entries.Count == 0 && s.History[0].Changes.Count == 0, "Detached variables were restored or still leased");
                s = Undo(env, db, s); Same(applied, env.Read()); Assert(s.Active.Applied.Count == 2 && s.Profiles[0].Entries.Count == 2, "Ownership was not restored together");
            });
            Test("batch keeps entries already at their destination and rejects an all-noop move", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "already user", 0, 1)); env.Data.Put(V("B", "system", 1, 1)); var before = env.Read(); var db = new MemoryStore(); var s = db.Load();
                Throws(() => MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 0, null, 0, null) }));
                s = MoveMany(env, db, s, R("A", 0, null, 0, null), R("B", 1, null, 0, null));
                Assert(env.Read().Get("A", 0).Value == "already user" && env.Read().Get("B", 0).Value == "system" && s.History[0].Changes.All(c => c.Before.Name != "A") && s.History[0].Label.Contains("1 项已在目标位置"), "Noop item was overwritten or missing from summary");
                Undo(env, db, s); Same(before, env.Read());
            });
            Test("batch rejects duplicate sources and ambiguous targets before writes", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "user", 0, 1)); env.Data.Put(V("A", "system", 1, 1)); var before = env.Read(); var db = new MemoryStore(); var s = db.Load(); var target = P("target"); s.Profiles.Add(target);
                Throws(() => MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 0, null, 0, target.Id), R("a", 0, null, 1, target.Id) }));
                Throws(() => MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 0, null, 0, target.Id), R("A", 1, null, 0, target.Id) }));
                Throws(() => MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 0, null, 0, null), R("A", 1, null, 0, null) }));
                Throws(() => MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 0, null, 1, null), R("A", 1, null, 0, null) }));
                Assert(env.Writes == 0 && target.Entries.Count == 0 && db.Pending == null, "Rejected batch changed source, target or journal"); Same(before, env.Read());
            });
            Test("a late invalid batch source cannot partially change the input state", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "valid", 1, 1)); env.Data.Put(V("SystemRoot", "C:\\Windows", 1, 1)); var before = env.Read(); var db = new MemoryStore(); var s = db.Load(); var target = P("target"); s.Profiles.Add(target);
                Throws(() => MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 1, null, 1, target.Id), R("SystemRoot", 1, null, 1, target.Id) }));
                Assert(env.Writes == 0 && s.Profiles[0].Entries.Count == 0 && s.History.Count == 0 && db.Pending == null, "Planning modified live state before a late error"); Same(before, env.Read());
            });
            Test("a mid-batch write failure rolls back all entries and keeps definitions", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "source A", 1, 1)); env.Data.Put(V("B", "%SOURCE_B%", 1, 2)); env.Data.Put(V("A", "base A", 0, 1)); var db = new MemoryStore(); var target = P("active", E("A", "active A", 0, "Set")); db.Data.Profiles.Add(target);
                var s = Switch(env, db, db.Load(), target); var before = env.Read(); env.FailWrite = env.Writes + 4;
                Throws(() => MoveMany(env, db, s, R("A", 1, null, 0, target.Id), R("B", 1, null, 0, target.Id)));
                Same(before, env.Read()); Assert(s.Profiles[0].Entries.Count == 1 && db.Load().Profiles[0].Entries.Single().Value == "active A" && db.Pending == null && db.Load().History.Count == 1, "Failed batch committed definitions or part of its history");
            });
            Test("batch preview catches a later source or definition edit before any write", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "source A", 1, 1)); env.Data.Put(V("B", "source B", 1, 1)); var db = new MemoryStore(); var s = db.Load(); var target = P("target"); s.Profiles.Add(target);
                var plan = MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 1, null, 0, target.Id), R("B", 1, null, 0, target.Id) });
                env.Data.Put(V("B", "external", 1, 1)); Throws(() => new Engine(env, db).Execute(plan, s));
                Assert(env.Writes == 0 && env.Read().Get("A", 1).Value == "source A" && s.Profiles[0].Entries.Count == 0 && db.Pending == null, "Race partially moved an earlier variable");
                var source = P("source", E("A", "one", 0, "Set"), E("B", "two", 0, "Set")); s.Profiles.Add(source);
                plan = MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 0, source.Id, 0, target.Id), R("B", 0, source.Id, 0, target.Id) }); source.Entries[1].Value = "edited after preview";
                Throws(() => new Engine(env, db).Execute(plan, s)); Assert(target.Entries.Count == 0 && source.Entries.Count == 2 && db.Pending == null, "Definition race applied part of the batch");
            });
            Test("interrupted batch recovery restores every source and the entire state", () => {
                var env = new MemoryEnvironment(); env.Data.Put(V("A", "source A", 1, 1)); env.Data.Put(V("B", "source B", 1, 1)); var before = env.Read(); var db = new MemoryStore(); var target = P("active", E("KEEP", "active keep", 0, "Set")); db.Data.Profiles.Add(target);
                var s = Switch(env, db, db.Load(), target); before = env.Read(); var plan = MovePlanner.MoveBatch(env.Read(), s, new[] { R("A", 1, null, 0, target.Id), R("B", 1, null, 0, target.Id) });
                db.Pending = new Pending { BeforeState = Json.Copy(s), Transaction = new History { Id = "interrupted-batch", Changes = plan.Changes, ProfileChanges = plan.ProfileChanges } }; foreach (var change in plan.Changes.Take(3)) env.Write(change.After);
                s = new Engine(env, db).Recover(s); Same(before, env.Read()); Assert(s.Active.Applied.Count == 1 && s.Profiles[0].Entries.Count == 1 && s.History.Count == 1 && db.Pending == null, "Batch recovery retained partial state");
            });
            Test("native batch transfer and persisted undo preserve long raw and empty values", () => {
                string scratch = "Software\\SwitchEnv.Tests\\" + Guid.NewGuid().ToString("N"); var adapter = new WindowsEnvironment(scratch);
                try
                {
                    adapter.Write(V("LONG", "%NO_EXPAND%\\" + new string('界', 6000), 0, 2)); adapter.Write(V("EMPTY", "", 0, 1)); var before = adapter.Read();
                    var store = new FileStore(Path.Combine(args[0], "batch-persistence")); var s = new State(); var target = P("target"); s.Profiles.Add(target); store.Save(s);
                    s = new Engine(adapter, store).Execute(MovePlanner.MoveBatch(adapter.Read(), s, new[] { R("LONG", 0, null, 1, target.Id), R("EMPTY", 0, null, 0, target.Id) }), s); s = store.Load();
                    Assert(s.History.Count == 1 && s.History[0].ProfileChanges.Count == 2 && s.Profiles[0].Entries.Count == 2 && adapter.Read().Values.Count == 0, "Native batch or persisted history was incomplete");
                    s = new Engine(adapter, store).Execute(Planner.Undo(adapter.Read(), s.History[0], s), s); Same(before, adapter.Read()); Assert(s.Profiles[0].Entries.Count == 0, "Persisted batch undo lost data");
                }
                finally { CleanupScratch(scratch); }
            });
            MultiGroupTests(args[0]);
            Test("group description roundtrips without becoming an environment operation", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var s = db.Load();
                var entry = E("A", "actual", 0, "Set"); entry.Description = "用途\n第二行";
                var group = P("group", entry);
                s = Enable(env, db, s, group);
                var loaded = db.Load();
                Assert(loaded.Profiles.Single().Entries.Single().Description == entry.Description, "Description was lost on save");
                Assert(Json.Copy(loaded.Profiles.Single()).Entries.Single().Description == entry.Description, "Description was lost on copy/export");
                Assert(loaded.ActiveGroups.Single().Entries.Single().Description == null && env.Read().Get("A", 0).Value == "actual",
                    "Description leaked into the applied definition or environment value");
            });
            Test("batch moves transfer descriptions and undo restores the original locations", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var s = db.Load();
                env.Data.Put(V("Path", "user", 0, 1)); env.Data.Put(V("PATH", "system", 1, 1));
                var existing = E("Path", "target", 0, "Set"); existing.Description = "old target";
                var target = P("target", existing); s.Profiles.Add(target);
                LocalDescriptions.Set(s, "0:PATH", "user note");
                LocalDescriptions.Set(s, "1:PATH", "system note");
                s = MoveMany(env, db, s, R("path", 0, null, 0, target.Id), R("PATH", 1, null, 1, target.Id));
                var loaded = db.Load();
                Assert(LocalDescriptions.Get(loaded, "0:PATH") == null && LocalDescriptions.Get(loaded, "1:PATH") == null, "Source descriptions were left behind");
                Assert(loaded.Profiles[0].Entries.Single(e => e.Scope == 0).Description == "user note" &&
                    loaded.Profiles[0].Entries.Single(e => e.Scope == 1).Description == "system note", "Batch move conflated descriptions");
                s = Undo(env, db, loaded);
                Assert(s.Profiles[0].Entries.Single().Description == "old target" && LocalDescriptions.Get(s, "0:PATH") == "user note" &&
                    LocalDescriptions.Get(s, "1:PATH") == "system note", "Undo did not restore description locations");
                s = Move(env, db, s, "Path", 0, target.Id, 1, null);
                Assert(LocalDescriptions.Get(s, "1:PATH") == "old target", "Configuration-to-local move lost its description");
                s = Undo(env, db, s);
                Assert(LocalDescriptions.Get(s, "1:PATH") == "system note", "Undo lost the overwritten local description");
            });
            Test("deleting and restoring a local variable includes its description", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var s = db.Load();
                env.Data.Put(V("A", "value", 0, 1)); LocalDescriptions.Set(s, "0:A", "local note");
                s = new Engine(env, db).Execute(GroupPlanner.Edit(env.Read(), s, E("A", "", 0, "Delete")), s);
                Assert(LocalDescriptions.Get(s, "0:A") == null, "Deleting left an obsolete description");
                s = Undo(env, db, s);
                Assert(env.Read().Get("A", 0).Value == "value" && LocalDescriptions.Get(s, "0:A") == "local note", "Undo lost the local description");
            });
            Test("a changed description invalidates a stale move preview before any writes", () => {
                var env = new MemoryEnvironment(); var db = new MemoryStore(); var s = db.Load();
                env.Data.Put(V("A", "value", 0, 1)); LocalDescriptions.Set(s, "0:A", "original");
                var plan = MovePlanner.Move(env.Read(), s, R("A", 0, null, 1, null));
                LocalDescriptions.Set(s, "0:A", "later edit");
                Throws(() => new Engine(env, db).Execute(plan, s));
                Assert(env.Writes == 0 && LocalDescriptions.Get(s, "0:A") == "later edit", "Old preview overwrote the edited description");
            });
            Test("real user and system environment unchanged by tests", () => Same(realBefore, new WindowsEnvironment().Read()));
            Test("real Windows startup entries unchanged by tests", () => Assert(startupBefore == StartupFingerprint(), "Real startup entries changed"));
            Test("real SwitchEnv scheduled task unchanged by tests", () => Assert(scheduledBefore == ScheduledFingerprint(), "Real scheduled task changed"));
            Console.WriteLine("RESULT " + count + " tests passed. Real Windows environment, startup entries and scheduled task unchanged."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
}
