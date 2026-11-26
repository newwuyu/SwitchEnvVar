using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SwitchEnv
{
    public sealed class StartupTask
    {
        public string Xml;
        public bool Enabled;
        public StartupTask Copy() { return new StartupTask { Xml = Xml, Enabled = Enabled }; }
    }

    public interface IStartupTasks
    {
        StartupTask Read(string name);
        void Register(string name, string xml, string sid);
        void Delete(string name);
    }

    public sealed class ScheduledStartupTasks : IStartupTasks
    {
        static bool Missing(Exception ex) { return ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x80070003); }
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        static void EnsureAdmin()
        {
            if (!WindowsEnvironment.IsAdministrator) throw new Exception("设置管理员自启需要管理员权限，请重新打开 SwitchEnv 并确认 Windows 权限提示。");
        }

        public StartupTask Read(string name)
        {
            dynamic service = null, folder = null, task = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); service.Connect(); folder = service.GetFolder("\\");
                try { task = folder.GetTask(name); } catch (Exception ex) { if (Missing(ex)) return null; throw; }
                return new StartupTask { Xml = (string)task.Xml, Enabled = (bool)task.Enabled };
            }
            finally { Release((object)task); Release((object)folder); Release((object)service); }
        }

        public void Register(string name, string xml, string sid)
        {
            EnsureAdmin(); dynamic service = null, folder = null, task = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); service.Connect(); folder = service.GetFolder("\\");
                // TASK_CREATE_OR_UPDATE, TASK_LOGON_INTERACTIVE_TOKEN. No password is saved.
                string permissions = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;" + sid + ")";
                task = folder.RegisterTask(name, xml, 6, sid, null, 3, permissions);
            }
            finally { Release((object)task); Release((object)folder); Release((object)service); }
        }

        public void Delete(string name)
        {
            EnsureAdmin(); dynamic service = null, folder = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)); service.Connect(); folder = service.GetFolder("\\");
                try { folder.DeleteTask(name, 0); } catch (Exception ex) { if (!Missing(ex)) throw; }
            }
            finally { Release((object)folder); Release((object)service); }
        }
    }

    public sealed class WindowsStartup
    {
        public const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string ValueName = "SwitchEnv";
        static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        readonly IStartupTasks tasks;
        readonly string keyPath;
        readonly string sid;
        public readonly string Executable;
        public readonly string Arguments;
        // This is the old Run command, used only to recognize a previous registration.
        public readonly string Command;
        public readonly string TaskName;
        public readonly string TaskXml;

        public WindowsStartup(string executable, string dataDirectory) : this(executable, dataDirectory, CurrentSid(), new ScheduledStartupTasks(), RunKey) { }

        // Tests inject an in-memory task store and an isolated HKCU key.
        internal WindowsStartup(string executable, string dataDirectory, string ownerSid, IStartupTasks storage, string registryKey)
        {
            keyPath = registryKey; sid = new SecurityIdentifier(ownerSid).Value; tasks = storage;
            Executable = Path.GetFullPath(executable);
            string data = NormalizeDirectory(dataDirectory);
            string adjacentData = NormalizeDirectory(Path.Combine(Path.GetDirectoryName(Executable), "data"));
            string legacyArguments = "--startup";
            if (!String.Equals(data, adjacentData, StringComparison.OrdinalIgnoreCase)) legacyArguments += " --data-dir " + QuotePath(data);
            Command = QuotePath(Executable) + " " + legacyArguments;
            Arguments = legacyArguments + " --expected-user-sid " + AdminBootstrap.Quote(sid);
            TaskName = "SwitchEnv.Startup." + sid;
            TaskXml = BuildXml();
        }

        static string CurrentSid() { using (var identity = WindowsIdentity.GetCurrent()) return identity.User.Value; }
        static string NormalizeDirectory(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }

        static string QuotePath(string path)
        {
            if (path.IndexOfAny(new[] { '"', '\0', '\r', '\n' }) >= 0) throw new Exception("自启路径含有无效字符。");
            return AdminBootstrap.Quote(path);
        }

        string BuildXml()
        {
            return new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", "SwitchEnv：当前账号登录后以管理员权限驻留托盘。")),
                new XElement(Ns + "Triggers", new XElement(Ns + "LogonTrigger", new XElement(Ns + "Enabled", "true"), new XElement(Ns + "UserId", sid), new XElement(Ns + "Delay", "PT5S"))),
                new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Owner"), new XElement(Ns + "UserId", sid), new XElement(Ns + "LogonType", "InteractiveToken"), new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"), new XElement(Ns + "DisallowStartIfOnBatteries", "false"), new XElement(Ns + "StopIfGoingOnBatteries", "false"), new XElement(Ns + "StartWhenAvailable", "true"), new XElement(Ns + "AllowStartOnDemand", "true"), new XElement(Ns + "Enabled", "true"), new XElement(Ns + "ExecutionTimeLimit", "PT0S")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Owner"), new XElement(Ns + "Exec", new XElement(Ns + "Command", Executable), new XElement(Ns + "Arguments", Arguments), new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(Executable))))).ToString(SaveOptions.DisableFormatting);
        }

        bool SameUser(string value)
        {
            if (String.Equals(value, sid, StringComparison.OrdinalIgnoreCase)) return true;
            try { return new NTAccount(value).Translate(typeof(SecurityIdentifier)).Value == sid; } catch { return false; }
        }

        bool Matches(StartupTask task)
        {
            try
            {
                var doc = XDocument.Parse(task.Xml); var root = doc.Root;
                var principals = root.Element(Ns + "Principals").Elements().ToList(); var actions = root.Element(Ns + "Actions").Elements().ToList(); var triggers = root.Element(Ns + "Triggers").Elements().ToList();
                if (principals.Count != 1 || actions.Count != 1 || triggers.Count != 1 || actions[0].Name != Ns + "Exec" || triggers[0].Name != Ns + "LogonTrigger") return false;
                return SameUser((string)principals[0].Element(Ns + "UserId")) && SameUser((string)triggers[0].Element(Ns + "UserId")) &&
                    (string)principals[0].Element(Ns + "LogonType") == "InteractiveToken" && (string)principals[0].Element(Ns + "RunLevel") == "HighestAvailable" &&
                    (string)triggers[0].Element(Ns + "Enabled") != "false" && String.Equals((string)actions[0].Element(Ns + "Command"), Executable, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals((string)actions[0].Element(Ns + "Arguments"), Arguments, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals((string)actions[0].Element(Ns + "WorkingDirectory"), Path.GetDirectoryName(Executable), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public bool IsEnabled
        {
            get
            {
                var current = tasks.Read(TaskName); return current != null && current.Enabled && Matches(current);
            }
        }

        public bool IsLegacyEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    var current = key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return current is string && String.Equals((string)current, Command, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        void RemoveLegacy()
        {
            if (!IsLegacyEnabled) return;
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
            {
                if (key == null) return;
                var current = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (current is string && String.Equals((string)current, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ValueName, false);
            }
        }

        public void MigrateLegacyIfNeeded() { if (IsLegacyEnabled) SetEnabled(true); }

        public void SetEnabled(bool enabled)
        {
            var previous = tasks.Read(TaskName);
            if (!enabled && previous != null && !Matches(previous))
                throw new Exception("管理员自启项已被其他 SwitchEnv 位置修改。请在对应版本中关闭，或重新开启当前版本的自启。");
            if (enabled) tasks.Register(TaskName, TaskXml, sid); else if (previous != null) tasks.Delete(TaskName);
            try { RemoveLegacy(); }
            catch (Exception original)
            {
                try
                {
                    if (previous == null) { if (enabled) tasks.Delete(TaskName); } else tasks.Register(TaskName, previous.Xml, sid);
                }
                catch (Exception rollback) { throw new Exception("旧版自启项清理失败，恢复任务也失败。请在任务计划程序中检查 " + TaskName + "。\n" + original.Message + "\n" + rollback.Message, original); }
                throw;
            }
        }
    }
}
