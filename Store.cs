using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace SwitchEnv
{
    public static class Json
    {
        public static T Read<T>(string path) { using (var stream = File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        public static T Copy<T>(T item)
        {
            using (var stream = new MemoryStream()) { var s = new DataContractJsonSerializer(typeof(T)); s.WriteObject(stream, item); stream.Position = 0; return (T)s.ReadObject(stream); }
        }
        public static void Write<T>(string path, T item)
        {
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, item); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
    }

    public class FileStore : IStore
    {
        public readonly string DirectoryPath;
        string StatePath { get { return Path.Combine(DirectoryPath, "state.json"); } }
        string PendingPath { get { return Path.Combine(DirectoryPath, "pending.json"); } }
        public FileStore(string path) { DirectoryPath = Path.GetFullPath(path); Directory.CreateDirectory(DirectoryPath); }
        public State Load()
        {
            if (!File.Exists(StatePath)) return new State();
            try { var s = Json.Read<State>(StatePath); Check(s); return s; }
            catch (Exception ex) { throw new Exception("无法读取方案数据。请保留 data 文件夹，检查 state.json 或它的 .bak 备份。\n" + ex.Message); }
        }
        public static void Check(State s)
        {
            if (s == null || (s.Version != 1 && s.Version != 2) || s.Profiles == null || s.History == null) throw new Exception("数据格式不受支持。");
            var ids = new System.Collections.Generic.HashSet<string>();
            foreach (var p in s.Profiles)
            {
                if (p == null || String.IsNullOrWhiteSpace(p.Id) || String.IsNullOrWhiteSpace(p.Name) || p.Entries == null || !ids.Add(p.Id)) throw new Exception("方案数据损坏或有重复 ID。");
            }
            GroupState.Normalize(s);
        }
        public void Save(State state) { Json.Write(StatePath, state); }
        public Pending LoadPending() { return File.Exists(PendingPath) ? Json.Read<Pending>(PendingPath) : null; }
        public void SavePending(Pending pending)
        {
            Json.Write(PendingPath, pending);
            var historyPath = Path.Combine(DirectoryPath, "backups"); Directory.CreateDirectory(historyPath);
            Json.Write(Path.Combine(historyPath, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + pending.Transaction.Id + ".json"), pending);
        }
        public void ClearPending() { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
    }
}
