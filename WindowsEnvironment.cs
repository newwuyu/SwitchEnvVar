using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SwitchEnv
{
    public class WindowsEnvironment : IEnvironment
    {
        const string UserPath = "Environment";
        const string MachinePath = "SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Environment";
        readonly string userPath = UserPath;
        readonly bool scratch;
        public WindowsEnvironment() { }
        public WindowsEnvironment(string scratchPath)
        {
            if (!scratchPath.StartsWith("Software\\SwitchEnv.Tests\\", StringComparison.Ordinal) || scratchPath.Contains("..")) throw new ArgumentException("Invalid isolated test path.");
            userPath = scratchPath; scratch = true;
        }
        public static bool IsAdministrator { get { using (var i = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(i).IsInRole(WindowsBuiltInRole.Administrator); } }
        public Snapshot Read()
        {
            var result = new Snapshot();
            for (int scope = 0; scope < (scratch ? 1 : 2); scope++)
            using (var key = (scope == 0 ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(scope == 0 ? userPath : MachinePath, false))
            {
                if (key == null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    var value = raw is string[] ? String.Join(";", (string[])raw) : Convert.ToString(raw);
                    result.Put(new Variable { Name = name, Scope = scope, Exists = true, Kind = (int)key.GetValueKind(name), Value = value });
                }
            }
            return result;
        }
        public void EnsureWritable(IEnumerable<int> scopes)
        {
            foreach (var scope in scopes)
            {
                if (scratch && scope != 0) throw new Exception("Isolated tests cannot access the machine environment.");
                if (scope == 1 && !IsAdministrator) throw new Exception("本次修改含系统变量。请通过左下角的“以管理员身份重启”打开程序，再启用方案。");
                using (var key = (scope == 0 ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(scope == 0 ? userPath : MachinePath, true))
                { if (key == null && scope == 1) throw new Exception("找不到系统环境变量注册表项。"); }
            }
        }
        public void Write(Variable value)
        {
            if (scratch && value.Scope != 0) throw new Exception("Isolated tests cannot access the machine environment.");
            using (var key = (value.Scope == 0 ? Registry.CurrentUser : Registry.LocalMachine).CreateSubKey(value.Scope == 0 ? userPath : MachinePath))
            {
                if (key == null) throw new Exception("无法写入环境变量。");
                if (value.Exists) key.SetValue(value.Name, value.Value, (RegistryValueKind)value.Kind); else key.DeleteValue(value.Name, false);
                key.Flush();
            }
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wparam, string lparam, uint flags, uint timeout, out IntPtr result);
        public void Notify() { if (scratch) return; IntPtr result; SendMessageTimeout(new IntPtr(0xffff), 0x1a, IntPtr.Zero, "Environment", 2, 1500, out result); }

        public static void OpenFreshTerminal()
        {
            // Use the Windows API to build the current user's block, including volatile variables,
            // then overlay raw registry values. Existing terminals keep their own inherited blocks.
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IntPtr token; IntPtr block;
            if (!OpenProcessToken(GetCurrentProcess(), 8, out token)) throw new Exception("无法获取当前用户身份。");
            try
            {
                if (!CreateEnvironmentBlock(out block, token, false)) throw new Exception("无法创建新的终端环境。");
                try
                {
                    int offset = 0;
                    while (true)
                    {
                        string item = Marshal.PtrToStringUni(IntPtr.Add(block, offset)); if (String.IsNullOrEmpty(item)) break;
                        offset += (item.Length + 1) * 2; int equal = item.IndexOf('=');
                        if (equal > 0) values[item.Substring(0, equal)] = item.Substring(equal + 1);
                    }
                }
                finally { DestroyEnvironmentBlock(block); }
            }
            finally { CloseHandle(token); }
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe"), "-NoLogo -NoExit") { UseShellExecute = false };
            start.EnvironmentVariables.Clear(); foreach (var item in values) start.EnvironmentVariables[item.Key] = item.Value;
            Process.Start(start);
        }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr block);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    }

    public class DemoEnvironment : IEnvironment
    {
        public Snapshot Data = new Snapshot();
        public DemoEnvironment()
        {
            Add("Path", "C:\\Windows\\System32;C:\\Windows;C:\\Program Files\\Git\\cmd", 1, 2);
            Add("Path", "%USERPROFILE%\\AppData\\Local\\Microsoft\\WindowsApps;D:\\Tools\\bin", 0, 2);
            Add("JAVA_HOME", "D:\\SDKs\\jdk-17", 0, 1); Add("NODE_ENV", "development", 0, 1);
            Add("TEMP", "%USERPROFILE%\\AppData\\Local\\Temp", 0, 2); Add("HTTP_PROXY", "http://127.0.0.1:7890", 0, 1);
        }
        void Add(string n, string v, int scope, int kind) { Data.Put(new Variable { Name = n, Value = v, Scope = scope, Kind = kind, Exists = true }); }
        public Snapshot Read() { return Data.Copy(); }
        public void EnsureWritable(IEnumerable<int> scopes) { }
        public void Write(Variable v) { Data.Put(v); }
        public void Notify() { }
    }
}
