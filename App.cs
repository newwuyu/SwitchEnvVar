using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SwitchEnv
{
    public class App
    {
        static string Option(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        [STAThread]
        public static int Main(string[] args)
        {
            Controller controller = null;
            Mutex mutex = null;
            bool ownsMutex = false;
            InstanceActivation activation = null;
            string errorPath = Option(args, "--error-log");
            try
            {
                string screenshot = Option(args, "--screenshot"); string captureAll = Option(args, "--capture-all");
                bool capture = screenshot != null || captureAll != null; bool demo = args.Contains("--demo") || capture;
                bool startup = args.Contains("--startup");
                string data = Option(args, "--data-dir") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, demo ? "demo-data" : "data");
                string sid; using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
                AdminBootstrap.CheckOwner(Option(args, "--expected-user-sid"), sid);
                if (!demo && !WindowsEnvironment.IsAdministrator)
                {
                    AdminBootstrap.Request(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, args, sid);
                    return 0;
                }
                string instanceName = null;
                if (!capture)
                {
                    string suffix; using (var sha = SHA256.Create()) suffix = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToUpperInvariant() + sid))).Replace("-", "").Substring(0, 24);
                    instanceName = "Local\\SwitchEnv-" + suffix;
                    mutex = new Mutex(false, instanceName);
                    try { ownsMutex = mutex.WaitOne(args.Contains("--wait-instance") ? 10000 : 0); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    if (!ownsMutex)
                    {
                        if (startup || InstanceActivation.TryShow(instanceName)) return 0;
                        const string message = "SwitchEnv 已在运行，但主窗口未响应打开请求。请查看任务栏右下角的隐藏图标。如果仍打不开，请在任务管理器结束 SwitchEnv.exe，再重新打开。";
                        if (errorPath == null) MessageBox.Show(message, "SwitchEnv");
                        else File.WriteAllText(errorPath, message);
                        return 1;
                    }
                }
                var store = new FileStore(data); var state = store.Load();
                if (!demo && !String.IsNullOrEmpty(state.UserSid) && state.UserSid != sid) throw new Exception("此数据目录属于另一 Windows 账号。请使用原账号运行，或选择新的 data 目录。");
                if (state.Profiles.Count == 0 && !File.Exists(Path.Combine(data, "state.json")))
                {
                    state.UserSid = sid;
                    if (demo) SeedDemo(state); else state.Profiles.Add(new Profile { Name = "我的开发环境", Note = "从 JAVA_HOME、代理或 PATH 开始，添加你的第一组变量。" });
                    store.Save(state);
                }
                if (capture) { state = new State { UserSid = sid }; SeedDemo(state); store.Save(state); }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchEnv.Theme.xaml")) app.Resources = (ResourceDictionary)XamlReader.Load(stream);
                Window window; using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchEnv.MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
                using (var icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchEnv.app.ico")) window.Icon = BitmapFrame.Create(icon);
                if (!capture) { window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width - 30); window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height - 30); }
                var backend = demo ? (IEnvironment)new DemoEnvironment() : new WindowsEnvironment();
                if (demo) foreach (var v in GroupState.Effective(state.ActiveGroups).Values) backend.Write(v);
                controller = new Controller(window, backend, store, state, demo, capture);
                if (!capture) activation = new InstanceActivation(instanceName, sid, window.Dispatcher, controller.Reopen);
                if (capture)
                {
                    window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -16000; window.Top = -16000;
                    window.Loaded += delegate
                    {
                        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                        {
                            try
                            {
                                if (captureAll != null) CaptureAll(controller, store, captureAll); else Capture(window, screenshot);
                                app.Shutdown(0);
                            }
                            catch (Exception ex) { if (errorPath != null) File.WriteAllText(errorPath, ex.ToString()); app.Shutdown(2); }
                        }));
                    };
                }
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { if (errorPath != null) File.WriteAllText(errorPath, e.Exception.ToString()); Dialogs.Error(window, e.Exception); e.Handled = true; };
                if (startup && !capture)
                {
                    app.MainWindow = window; app.ShutdownMode = ShutdownMode.OnExplicitShutdown; return app.Run();
                }
                return app.Run(window);
            }
            catch (Exception ex)
            {
                if (errorPath != null) File.WriteAllText(errorPath, ex.ToString()); else MessageBox.Show(ex.Message, "SwitchEnv 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return 2;
            }
            finally
            {
                if (activation != null) activation.Dispose();
                if (controller != null) controller.Dispose();
                if (mutex != null)
                {
                    if (ownsMutex) mutex.ReleaseMutex();
                    mutex.Dispose();
                }
            }
        }

        public static void SeedDemo(State state)
        {
            state.Profiles.Add(new Profile { Name = "日常开发", Note = "Java 21 与本地工具链，让每个终端使用一致的开发环境。", Entries = new System.Collections.Generic.List<Entry> {
                new Entry { Name = "JAVA_HOME", Value = "D:\\SDKs\\jdk-21", Scope = 0, Kind = 1, Mode = "Set" },
                new Entry { Name = "Path", Value = "%JAVA_HOME%\\bin;D:\\Tools\\maven\\bin", Scope = 0, Kind = 2, Mode = "Prepend" },
                new Entry { Name = "NODE_ENV", Value = "development", Scope = 0, Kind = 1, Mode = "Set" },
                new Entry { Name = "MAVEN_HOME", Value = "D:\\Tools\\maven", Scope = 0, Kind = 1, Mode = "Set" } } });
            state.Profiles.Add(new Profile { Name = "本地代理", Note = "为命令行工具配置本地代理。", Entries = new System.Collections.Generic.List<Entry> {
                new Entry { Name = "HTTP_PROXY", Value = "http://127.0.0.1:7890", Scope = 0, Kind = 1, Mode = "Set" },
                new Entry { Name = "HTTPS_PROXY", Value = "http://127.0.0.1:7890", Scope = 0, Kind = 1, Mode = "Set" } } });
            state.Profiles.Add(new Profile { Name = "Java 17 · 兼容项目", Note = "旧项目的兼容工具链。", Entries = new System.Collections.Generic.List<Entry> {
                new Entry { Name = "JAVA_HOME", Value = "D:\\SDKs\\jdk-17", Scope = 0, Kind = 1, Mode = "Set" },
                new Entry { Name = "Path", Value = "%JAVA_HOME%\\bin", Scope = 0, Kind = 2, Mode = "Prepend" } } });
        }
        public static void Capture(Window w, string path)
        {
            w.UpdateLayout(); w.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            var content = w.Content as FrameworkElement; content.UpdateLayout();
            var margin = content.Margin; double width = content.ActualWidth + margin.Left + margin.Right; double height = content.ActualHeight + margin.Top + margin.Bottom;
            var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
                var offset = VisualTreeHelper.GetOffset(content);
                dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, content.ActualWidth, content.ActualHeight) }, null, new Rect(margin.Left, margin.Top, content.ActualWidth, content.ActualHeight));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(path)) encoder.Save(file);
        }
        static void CaptureDialog(Window w, string path)
        {
            w.ShowActivated = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -16000; w.Top = -16000; w.Show(); w.UpdateLayout(); Capture(w, path); w.Close();
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static T VisualChild<T>(DependencyObject item) where T : DependencyObject
        {
            if (item == null) return null; var found = item as T; if (found != null) return found;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) { found = VisualChild<T>(VisualTreeHelper.GetChild(item, i)); if (found != null) return found; }
            return null;
        }
        static void ClickRowCheck(DataGrid grid, string name)
        {
            grid.UpdateLayout(); var item = grid.Items.Cast<Row>().First(r => r.Name == name);
            var row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow; var check = VisualChild<CheckBox>(row);
            Require(check != null, "Checkbox was not rendered for " + name);
            check.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        }
        static List<MoveRequest> SmokeMoveDialog(Controller c, Profile source, Profile target, IList<Entry> entries, int scopeIndex, string screenshot)
        {
            List<MoveRequest> requests = null; Exception failure = null;
            var w = Dialogs.BuildMoveWindow(c.Window, c.State, source, entries, selected => requests = selected);
            w.ShowActivated = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -16000; w.Top = -16000;
            w.Loaded += delegate
            {
                w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        var destination = LogicalTreeHelper.FindLogicalNode(w, "MoveDestination") as ComboBox; var scope = LogicalTreeHelper.FindLogicalNode(w, "MoveScope") as ComboBox;
                        Require(destination != null && scope != null, "Move target controls missing");
                        destination.SelectedIndex = c.State.Profiles.FindIndex(p => p.Id == target.Id) + 2; scope.SelectedIndex = scopeIndex;
                        if (screenshot != null) Capture(w, screenshot);
                        var preview = LogicalTreeHelper.FindLogicalNode(w, "MovePreview") as Button; Require(preview != null, "Move preview button missing"); preview.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    catch (Exception ex) { failure = ex; w.Close(); }
                }));
            };
            w.ShowDialog(); if (failure != null) throw failure; Require(requests != null, "Batch dialog did not submit requests"); return requests;
        }
        static void CaptureBatch(Controller c, FileStore store, string folder, string sourceId, string targetId)
        {
            c.Navigate("profile", sourceId); var grid = c.Window.FindName("VariablesGrid") as DataGrid; var header = grid.Columns[0].Header as CheckBox;
            Require(grid.SelectionMode == DataGridSelectionMode.Extended, "Table did not allow multiple selection"); grid.UnselectAll();
            ClickRowCheck(grid, "JAVA_HOME"); ClickRowCheck(grid, "Path"); Require(grid.SelectedItems.Count == 2, "Checkbox clicks replaced the earlier selection");
            ClickRowCheck(grid, "JAVA_HOME"); Require(grid.SelectedItems.Count == 1 && ((Row)grid.SelectedItems[0]).Name == "Path", "Unchecking a row cleared another selection");
            ClickRowCheck(grid, "JAVA_HOME"); Require(grid.SelectedItems.Count == 2 && header.IsChecked == null, "Partial-selection checkbox state was incorrect");
            var selectedRow = grid.ItemContainerGenerator.ContainerFromItem(grid.Items.Cast<Row>().First(r => r.Name == "Path")) as DataGridRow;
            selectedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Require(grid.SelectedItems.Count == 2, "Right click discarded the batch selection"); grid.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Require(((MenuItem)grid.ContextMenu.Items[0]).Header.ToString().Contains("批量移动 2"), "Context menu did not use the batch selection");
            header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Require(grid.SelectedItems.Count == grid.Items.Count && header.IsChecked == true, "Select-all header failed");
            header.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Require(grid.SelectedItems.Count == 0 && header.IsChecked == false, "Clear-all header failed");
            ClickRowCheck(grid, "JAVA_HOME"); ClickRowCheck(grid, "Path"); c.Render(); Require(grid.SelectedItems.Count == 2, "Refresh lost multiple selection");
            var search = c.Window.FindName("VariableSearch") as TextBox; search.Text = "Path"; Require(grid.SelectedItems.Count == 1 && grid.Items.Count == 1, "Filtering kept a hidden selected variable");
            grid.UnselectAll(); DataGrid.SelectAllCommand.Execute(null, grid); Require(grid.SelectedItems.Count == 1, "Select-all command failed on filtered rows");
            search.Text = ""; Require(grid.SelectedItems.Count == 1, "Clearing a filter reselected a hidden variable"); ClickRowCheck(grid, "JAVA_HOME");
            Require(((Button)c.Window.FindName("MoveVariable")).Content.ToString() == "批量移动" && !((Button)c.Window.FindName("EditVariable")).IsEnabled && !((Button)c.Window.FindName("DeleteVariable")).IsEnabled, "Batch actions were not correctly enabled");
            Capture(c.Window, Path.Combine(folder, "11-batch-selection.png"));
            var source = c.State.Profiles.First(p => p.Id == sourceId); var target = c.State.Profiles.First(p => p.Id == targetId); var entries = grid.SelectedItems.Cast<Row>().Select(r => r.Entry.Copy()).ToList();
            var requests = SmokeMoveDialog(c, source, target, entries, 0, Path.Combine(folder, "12-batch-dialog.png"));
            var mixed = entries.Select(e => e.Copy()).ToList(); mixed[0].Scope = 0; mixed[1].Scope = 1;
            var preserve = SmokeMoveDialog(c, source, target, mixed, 0, null); Require(preserve[0].TargetScope == 0 && preserve[1].TargetScope == 1, "Dialog collapsed original scopes");
            Require(SmokeMoveDialog(c, source, target, mixed, 1, null).All(r => r.TargetScope == 0), "Dialog failed to set user scope");
            Require(SmokeMoveDialog(c, source, target, mixed, 2, null).All(r => r.TargetScope == 1), "Dialog failed to set system scope");
            var before = c.EnvironmentBackend.Read(); int records = c.State.History.Count; var plan = MovePlanner.MoveBatch(before, c.State, requests);
            CaptureDialog(Dialogs.BuildPreviewWindow(c.Window, plan, false, true), Path.Combine(folder, "13-batch-preview.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(plan, c.State); Require(c.State.History.Count == records + 1, "Batch created more than one history record"); c.Navigate("profile", targetId);
            Capture(c.Window, Path.Combine(folder, "14-after-batch.png")); Require(grid.SelectedItems.Count == 0, "Selection leaked to a different profile");
            c.State = new Engine(c.EnvironmentBackend, store).Execute(Planner.Undo(c.EnvironmentBackend.Read(), c.State.History[0], c.State), c.State);
            Require(before.Values.Count == c.EnvironmentBackend.Read().Values.Count && before.Values.Values.All(v => Variable.Equal(v, c.EnvironmentBackend.Read().Get(v.Name, v.Scope))), "Batch undo changed the starting environment");
            c.Navigate("profile", sourceId); grid.SelectAll(); c.Window.Width = c.Window.MinWidth; c.Window.UpdateLayout(); Capture(c.Window, Path.Combine(folder, "15-minimum-width.png"));
        }
        static void CaptureAll(Controller c, FileStore store, string folder)
        {
            Directory.CreateDirectory(folder); Capture(c.Window, Path.Combine(folder, "01-profile.png"));
            var p = c.SelectedProfile; var plan = GroupPlanner.SetEnabled(c.EnvironmentBackend.Read(), c.State, p, true);
            CaptureDialog(Dialogs.BuildPreviewWindow(c.Window, plan, false, true), Path.Combine(folder, "02-preview.png"));
            CaptureDialog(Dialogs.BuildEntryWindow(c.Window, p.Entries[1], false, e => { }), Path.Combine(folder, "03-editor.png"));
            CaptureDialog(Dialogs.BuildPathWindow(c.Window, "%JAVA_HOME%\\bin;D:\\Tools\\maven\\bin;D:\\Tools\\nodejs;%USERPROFILE%\\AppData\\Local\\Microsoft\\WindowsApps"), Path.Combine(folder, "04-path.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(plan, c.State); c.Render(); Capture(c.Window, Path.Combine(folder, "05-active.png"));
            c.Navigate("environment", null); Capture(c.Window, Path.Combine(folder, "06-environment.png"));
            c.Navigate("history", null); Capture(c.Window, Path.Combine(folder, "07-history.png"));
            c.Navigate("profile", p.Id); var source = c.SelectedProfile; var target = c.State.Profiles[2];
            CaptureDialog(Dialogs.BuildMoveWindow(c.Window, c.State, source, source.Entries[1], r => { }), Path.Combine(folder, "08-move-dialog.png"));
            var move = MovePlanner.Move(c.EnvironmentBackend.Read(), c.State, new MoveRequest { Name = "Path", SourceProfileId = source.Id, SourceScope = 0, TargetProfileId = target.Id, TargetScope = 0 });
            CaptureDialog(Dialogs.BuildPreviewWindow(c.Window, move, false, true), Path.Combine(folder, "09-move-preview.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(move, c.State); c.Navigate("profile", target.Id); Capture(c.Window, Path.Combine(folder, "10-after-move.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(Planner.Undo(c.EnvironmentBackend.Read(), c.State.History[0], c.State), c.State);
            CaptureBatch(c, store, folder, source.Id, target.Id);
            CaptureApplicationFilter(c, store, folder, source.Id);
            CaptureMultipleGroups(c, store, folder);
            CaptureDescriptions(c, store, folder);
            File.WriteAllText(Path.Combine(folder, "ui-smoke.txt"), "Thirty WPF views rendered; variable description edit, search, scope separation and persistence verified; one sidebar create button verified; multiple checked toggles, shared-variable provenance, per-group filters, single-group stop, full-state undo, priority preview and final baseline restoration verified; application-mode, scope and search combinations, filtered batch selection, no-active-profile state, checkbox and select-all commands, refresh, context-menu selection, batch scope choices, one-record batch move and undo verified; no real registry writes.\n");
        }
        static void CaptureApplicationFilter(Controller c, FileStore store, string folder, string sourceId)
        {
            c.Window.Width = 1240; c.Navigate("environment", null);
            var grid = c.Window.FindName("VariablesGrid") as DataGrid; var application = c.Window.FindName("ApplicationFilter") as ComboBox;
            var scope = c.Window.FindName("ScopeFilter") as ComboBox; var search = c.Window.FindName("VariableSearch") as TextBox;
            Require(application.Visibility == Visibility.Visible, "Application filter missing on local environment"); search.Text = ""; scope.SelectedIndex = 0; application.SelectedIndex = 0;
            int all = grid.Items.Count; application.SelectedIndex = 2;
            Require(grid.Items.Count == 4 && grid.Items.Cast<Row>().All(r => r.ModeText == "配置组管理"), "Managed application filter returned unrelated rows");
            grid.SelectAll(); Require(grid.SelectedItems.Count == 4, "Batch selection did not respect the application filter");
            Capture(c.Window, Path.Combine(folder, "16-managed-filter.png"));
            var original = c.EnvironmentBackend.Read().Get("JAVA_HOME", 0); var external = original.Copy(); external.Value = "external edit in demo"; c.EnvironmentBackend.Write(external); c.Render();
            Require(grid.Items.Cast<Row>().Any(r => r.Name == "JAVA_HOME" && r.DisplayValue == external.Value), "External edits incorrectly changed profile ownership");
            c.EnvironmentBackend.Write(original); c.Render(); application.SelectedIndex = 1;
            Require(grid.Items.Count == all - 4 && grid.Items.Cast<Row>().All(r => r.ModeText == "本机值") && grid.SelectedItems.Count == 0, "Local filter kept managed rows or hidden selections");
            Capture(c.Window, Path.Combine(folder, "17-local-filter.png"));
            scope.SelectedIndex = 2; search.Text = "Path";
            Require(grid.Items.Count == 1 && ((Row)grid.Items[0]).Variable.Scope == 1 && ((Row)grid.Items[0]).Name == "Path", "Application, scope and search did not combine");
            Capture(c.Window, Path.Combine(folder, "18-combined-filter.png"));
            search.Text = ""; application.SelectedIndex = 2; Require(grid.Items.Count == 0, "Managed filter included an unmanaged system Path");
            scope.SelectedIndex = 0; search.Text = "MAVEN_HOME"; Require(grid.Items.Count == 1 && ((Row)grid.Items[0]).Name == "MAVEN_HOME" && ((Row)grid.Items[0]).Variable.Scope == 0, "Managed name search failed");
            search.Text = ""; c.Window.Width = c.Window.MinWidth; grid.SelectAll(); c.Window.UpdateLayout(); Capture(c.Window, Path.Combine(folder, "19-filter-minimum-width.png"));
            c.Navigate("profile", sourceId); Require(application.Visibility == Visibility.Collapsed && grid.Items.Count == c.SelectedProfile.Entries.Count, "Local application filter hid scheme definitions");
            c.Navigate("environment", null); Require(application.SelectedIndex == 2 && grid.Items.Count == 4, "Returning to local environment lost the filter choice");
            c.State = new Engine(c.EnvironmentBackend, store).Execute(GroupPlanner.DisableAll(c.EnvironmentBackend.Read(), c.State), c.State); c.Render();
            Require(grid.Items.Count == 0 && !((CheckBox)grid.Columns[0].Header).IsEnabled && ((TextBlock)c.Window.FindName("EmptyTitle")).Text.Contains("还没有配置组管理"), "No-active-profile filtering or empty selection state failed");
            c.Window.Width = 1240; Capture(c.Window, Path.Combine(folder, "20-no-active-profile.png"));
        }
        static void ClickGroupToggle(Controller c, string id)
        {
            var list = c.Window.FindName("ProfilesBox") as ListBox; c.Window.UpdateLayout();
            var view = list.Items.Cast<ProfileView>().First(g => g.Id == id); var item = list.ItemContainerGenerator.ContainerFromItem(view) as ListBoxItem;
            var toggle = VisualChild<ToggleButton>(item); Require(toggle != null, "Group toggle not rendered");
            bool accepted = false; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            int ticks = 0;
            timer.Tick += delegate { var preview = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "应用预览 — SwitchEnv" && w.IsVisible); if (preview != null) { accepted = true; timer.Stop(); preview.DialogResult = true; } else if (++ticks >= 250) { timer.Stop(); foreach (var dialog in Application.Current.Windows.Cast<Window>().Where(w => w.Owner == c.Window).ToList()) dialog.Close(); } };
            timer.Start(); try { toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); } finally { timer.Stop(); }
            Require(accepted, "Toggle did not show a transaction preview");
        }
        static void CaptureMultipleGroups(Controller c, FileStore store, string folder)
        {
            var baseline = c.EnvironmentBackend.Read(); var first = c.State.Profiles[0]; var proxy = c.State.Profiles[1]; var compatible = c.State.Profiles[2];
            c.Window.Width = 1240; c.Navigate("profile", first.Id);
            var search = c.Window.FindName("VariableSearch") as TextBox; var scope = c.Window.FindName("ScopeFilter") as ComboBox; var filter = c.Window.FindName("ApplicationFilter") as ComboBox; var grid = c.Window.FindName("VariablesGrid") as DataGrid;
            search.Text = ""; scope.SelectedIndex = 0; filter.SelectedIndex = 0;
            ClickGroupToggle(c, first.Id); ClickGroupToggle(c, proxy.Id); ClickGroupToggle(c, compatible.Id);
            Require(c.State.ActiveGroups.Count == 3 && ((ListBox)c.Window.FindName("ProfilesBox")).Items.Cast<ProfileView>().Count(g => g.IsActive) == 3, "Toggles remained mutually exclusive");
            Require(c.EnvironmentBackend.Read().Get("JAVA_HOME", 0).Value == compatible.Entries.Single(e => e.Name == "JAVA_HOME").Value && c.EnvironmentBackend.Read().Get("HTTP_PROXY", 0).Exists, "Last-wins or independent groups failed");
            Capture(c.Window, Path.Combine(folder, "21-multiple-enabled.png")); c.Navigate("environment", null); filter.SelectedIndex = 2;
            Require(grid.Items.Cast<Row>().First(r => r.Name == "JAVA_HOME").GroupIds.Count == 2 && grid.Items.Cast<Row>().First(r => r.Name == "Path" && r.Variable.Scope == 0).OwnerText.Contains("→"), "Shared-variable provenance missing");
            Capture(c.Window, Path.Combine(folder, "22-multiple-local.png"));
            filter.SelectedItem = filter.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == proxy.Id); Require(grid.Items.Count == 2 && grid.Items.Cast<Row>().All(r => r.GroupIds.Contains(proxy.Id)), "Group filter mixed dimensions");
            search.Text = "HTTP_PROXY"; Require(grid.Items.Count == 1, "Group filter did not combine with search"); search.Text = ""; grid.SelectAll(); Require(grid.SelectedItems.Count == 2, "Group-filtered select-all included unrelated variables");
            Capture(c.Window, Path.Combine(folder, "23-group-filter.png"));
            var priority = GroupPlanner.Prioritize(c.EnvironmentBackend.Read(), c.State, first);
            Require(priority.CompositionNote.Contains("重叠变量"), "Priority preview did not explain overlap"); CaptureDialog(Dialogs.BuildPreviewWindow(c.Window, priority, false, true), Path.Combine(folder, "24-priority-preview.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(priority, c.State); c.Render(); Require(c.EnvironmentBackend.Read().Get("JAVA_HOME", 0).Value == first.Entries.Single(e => e.Name == "JAVA_HOME").Value, "Raising priority failed");
            filter.SelectedItem = filter.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == first.Id); ClickGroupToggle(c, first.Id);
            Require(c.State.ActiveGroups.Count == 2 && filter.SelectedIndex == 2 && c.EnvironmentBackend.Read().Get("HTTP_PROXY", 0).Exists && c.EnvironmentBackend.Read().Get("JAVA_HOME", 0).Value == compatible.Entries.Single(e => e.Name == "JAVA_HOME").Value, "Stopping one group stopped others or left a stale filter");
            Capture(c.Window, Path.Combine(folder, "25-stop-one-group.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(Planner.Undo(c.EnvironmentBackend.Read(), c.State.History[0], c.State), c.State); c.Render(); Require(c.State.ActiveGroups.Count == 3 && c.State.ActiveGroups.Last().ProfileId == first.Id, "Single-group stop undo lost the order");
            c.Window.Width = c.Window.MinWidth; c.Window.UpdateLayout(); Capture(c.Window, Path.Combine(folder, "26-multiple-minimum-width.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(GroupPlanner.DisableAll(c.EnvironmentBackend.Read(), c.State), c.State); c.Render(); var restored = c.EnvironmentBackend.Read();
            Require(restored.Values.Count == baseline.Values.Count && baseline.Values.Values.All(v => Variable.Equal(v, restored.Get(v.Name, v.Scope))), "Stopping all groups did not restore the baseline");
            c.Window.Width = 1240; Capture(c.Window, Path.Combine(folder, "27-all-groups-stopped.png"));
        }

        static void EditDescription(Controller c, Row row, string description, string screenshot = null)
        {
            var grid = (DataGrid)c.Window.FindName("VariablesGrid");
            grid.SelectedItem = row;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            Exception failure = null;
            bool edited = false;
            int ticks = 0;
            timer.Tick += delegate
            {
                var dialog = c.Window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsVisible);
                if (dialog == null) return;
                var input = LogicalTreeHelper.FindLogicalNode(dialog, "VariableDescription") as TextBox;
                try
                {
                    if (!edited && input != null)
                    {
                        input.Text = description;
                        if (screenshot != null) Capture(dialog, screenshot);
                        var save = (Button)(LogicalTreeHelper.FindLogicalNode(dialog, "SaveVariable") ?? LogicalTreeHelper.FindLogicalNode(dialog, "SaveVariableDescription"));
                        Require(save != null, "Description save button missing");
                        edited = true;
                        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    else if (++ticks > 50)
                    {
                        failure = new Exception("Description edit did not close or opened an environment preview.");
                        dialog.Close();
                    }
                }
                catch (Exception ex) { failure = ex; dialog.Close(); }
            };
            timer.Start();
            try { ((Button)c.Window.FindName("EditVariable")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
            finally { timer.Stop(); }
            if (failure != null) throw failure;
            Require(edited, "Variable editing did not open a description field");
        }

        static void CaptureDescriptions(Controller c, FileStore store, string folder)
        {
            var grid = (DataGrid)c.Window.FindName("VariablesGrid");
            var filter = (ComboBox)c.Window.FindName("ApplicationFilter");
            var search = (TextBox)c.Window.FindName("VariableSearch");
            Require(c.Window.FindName("AddProfileSmall") == null && c.Window.FindName("AddProfile") is Button, "Duplicate create button still exists");
            filter.SelectedIndex = 0;
            var environment = c.EnvironmentBackend.Read();
            int historyCount = c.State.History.Count;
            var profile = c.State.Profiles.First();
            c.Navigate("profile", profile.Id);
            EditDescription(c, grid.Items.Cast<Row>().First(r => r.Name == "JAVA_HOME"), "项目构建使用的 JDK", Path.Combine(folder, "28-description-editor.png"));
            search.Text = "项目构建";
            Require(grid.Items.Count == 1 && ((Row)grid.Items[0]).Name == "JAVA_HOME", "Configuration description was not searchable");
            search.Text = "";
            c.Navigate("environment", null);
            EditDescription(c, grid.Items.Cast<Row>().First(r => r.Variable.Key == "0:PATH"), "当前用户的工具目录");
            EditDescription(c, grid.Items.Cast<Row>().First(r => r.Variable.Key == "1:PATH"), "所有账号共用的工具目录");
            search.Text = "当前用户的工具";
            Require(grid.Items.Count == 1 && ((Row)grid.Items[0]).Variable.Scope == 0, "Local descriptions are not searchable per scope");
            search.Text = "";
            c.Window.Width = c.Window.MinWidth;
            Capture(c.Window, Path.Combine(folder, "29-descriptions-local.png"));
            c.State = new Engine(c.EnvironmentBackend, store).Execute(GroupPlanner.SetEnabled(environment, c.State, c.State.Profiles.First(), true), c.State);
            c.Render();
            var activeEnvironment = c.EnvironmentBackend.Read();
            EditDescription(c, grid.Items.Cast<Row>().First(r => r.Name == "JAVA_HOME"), "此机 Java 环境记录", Path.Combine(folder, "30-description-managed.png"));
            var reloaded = store.Load();
            Require(reloaded.Profiles.First().Entries.First(e => e.Name == "JAVA_HOME").Description == "项目构建使用的 JDK", "Group description was not saved");
            Require(LocalDescriptions.Get(reloaded, "0:PATH") == "当前用户的工具目录" &&
                LocalDescriptions.Get(reloaded, "1:PATH") == "所有账号共用的工具目录" &&
                LocalDescriptions.Get(reloaded, "0:JAVA_HOME") == "此机 Java 环境记录", "Local descriptions did not persist separately");
            Require(c.State.History.Count == historyCount + 1 &&
                activeEnvironment.Values.Values.All(v => Variable.Equal(v, c.EnvironmentBackend.Read().Get(v.Name, v.Scope))),
                "Saving descriptions modified the environment or created an environment history entry");
        }
    }
}
