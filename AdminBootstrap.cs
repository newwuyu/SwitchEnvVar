using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SwitchEnv
{
    public static class AdminBootstrap
    {
        // Escape individual arguments using the Windows command-line parsing rules.
        public static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new Exception("启动参数含有无效字符。");
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', slashes * (c == '"' ? 2 : 1)); slashes = 0;
                if (c == '"') result.Append('\\');
                result.Append(c);
            }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }

        public static void CheckOwner(string expected, string actual)
        {
            if (!String.IsNullOrEmpty(expected) && !String.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new Exception("提权后 Windows 账号发生了变化。请使用当前账号确认权限，避免修改另一账号的用户变量和自启设置。");
        }

        public static string ChildArguments(string[] args, string sid)
        {
            var kept = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--expected-user-sid") { if (i + 1 < args.Length) i++; continue; }
                kept.Add(args[i]);
            }
            kept.Add("--expected-user-sid"); kept.Add(sid);
            return String.Join(" ", kept.Select(Quote));
        }

        public static bool Request(string executable, string[] args, string sid)
        {
            try
            {
                Process.Start(new ProcessStartInfo(executable, ChildArguments(args, sid)) {
                    UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(executable)
                });
                return true;
            }
            catch (Win32Exception ex) { if (ex.NativeErrorCode == 1223) return false; throw; }
        }
    }
}
