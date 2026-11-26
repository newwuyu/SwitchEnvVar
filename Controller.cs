using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SwitchEnv
{
    public class Controller : IDisposable
    {
        public readonly Window Window;
        public readonly IEnvironment EnvironmentBackend;
        public State State;
        readonly FileStore store;
        readonly Engine engine;
        readonly bool demo;
        readonly bool capture;
        readonly WindowsStartup startup;
        string startupMigrationError;
        string route = "profile";
        string selectedId;
        bool rendering;
        bool quitting;
        bool updatingRows;
        int visibleRowCount;
        int totalRowCount;
        string rowsContext;
        Forms.NotifyIcon tray;
        CheckBox SelectionHeader { get { return Find<DataGrid>("VariablesGrid").Columns[0].Header as CheckBox; } }
        public Profile SelectedProfile { get { return State.Profiles.FirstOrDefault(p => p.Id == selectedId); } }
        T Find<T>(string name) where T : class { return Window.FindName(name) as T; }
        void Text(string name, string value) { Find<TextBlock>(name).Text = value; }
        void Show(string name, bool visible) { Find<UIElement>(name).Visibility = visible ? Visibility.Visible : Visibility.Collapsed; }
        void Click(string name, Action action) { Find<Button>(name).Click += delegate { Safe(action); }; }
        void Safe(Action action) { try { action(); } catch (Exception ex) { if (capture) throw; Dialogs.Error(Window, ex); } }

        public Controller(Window window, IEnvironment backend, FileStore storage, State state, bool demo, bool capture)
        {
            Window = window; EnvironmentBackend = backend; store = storage; State = state; this.demo = demo; this.capture = capture; engine = new Engine(backend, storage);
            if (!demo && !capture) startup = new WindowsStartup(Process.GetCurrentProcess().MainModule.FileName, store.DirectoryPath);
            if (startup != null) try { startup.MigrateLegacyIfNeeded(); } catch (Exception ex) { startupMigrationError = ex.Message; }
            selectedId = State.Profiles.Count > 0 ? State.Profiles[0].Id : null;
            route = selectedId == null ? "environment" : "profile";
            var pending = store.LoadPending();
            if (pending != null && State.History.Any(h => h.Id == pending.Transaction.Id)) store.ClearPending();
            Wire(); Render(); if (!capture) CreateTray();
            if (startupMigrationError != null) Status("旧版自启升级失败：" + startupMigrationError);
        }

        void Wire()
        {
            Click("EnvironmentNav", () => Navigate("environment", null)); Click("HistoryNav", () => Navigate("history", null));
            Click("AddProfile", NewProfile);
            Click("RefreshButton", () => { Render(); Status("已重新读取 Windows 环境变量"); });
            Click("AddVariable", () => Edit(null)); Click("EditVariable", () => Edit(Find<DataGrid>("VariablesGrid").SelectedItem as Row));
            Click("DeleteVariable", DeleteVariable); Click("ApplyButton", Apply); Click("StopButton", () => SetGroup(SelectedProfile, false));
            Click("MoveVariable", MoveVariable);
            Click("TerminalButton", () => { if (demo) { Status("演示模式使用模拟变量，不启动系统终端"); return; } WindowsEnvironment.OpenFreshTerminal(); });
            Click("ProfileMenuButton", ProfileMenu); Click("ImportButton", Import); Click("ExportButton", Export);
            Click("RecoverButton", () =>
            {
                var pending = store.LoadPending(); if (pending == null) return;
                var snapshot = EnvironmentBackend.Read(); var recovery = new Plan { Label = "恢复未完成的修改" };
                foreach (var c in pending.Transaction.Changes)
                {
                    var actual = snapshot.Get(c.After.Name, c.After.Scope);
                    if (Variable.Equal(actual, c.After)) recovery.Changes.Add(new Change { Before = actual, After = c.Before });
                    else if (!Variable.Equal(actual, c.Before)) throw new ConflictException(c.Before.Name + " 已被其他软件修改，请保留 data/pending.json 并手动核对。");
                }
                if (Dialogs.Preview(Window, recovery, demo)) { State = engine.Recover(State); Render(); Status("未完成的修改已恢复"); }
            });
            Click("AdminButton", Elevate); Click("HelpButton", Help);
            Find<ToggleButton>("StartupToggle").Click += delegate { if (!rendering) Safe(UpdateStartup); };
            Find<TextBox>("ProfileSearch").TextChanged += delegate { if (!rendering) RenderProfiles(); };
            Find<TextBox>("VariableSearch").TextChanged += delegate { if (!rendering) RenderRows(); };
            Find<ComboBox>("ScopeFilter").SelectionChanged += delegate { if (!rendering) RenderRows(); };
            Find<ComboBox>("ApplicationFilter").SelectionChanged += delegate { if (!rendering) RenderRows(); };
            Find<CheckBox>("RevealValues").Checked += delegate { RenderRows(); }; Find<CheckBox>("RevealValues").Unchecked += delegate { RenderRows(); };
            Find<ListBox>("ProfilesBox").SelectionChanged += delegate
            { if (rendering) return; var p = Find<ListBox>("ProfilesBox").SelectedItem as ProfileView; if (p != null) Navigate("profile", p.Id); };
            Window.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((sender, e) =>
            {
                var toggle = e.OriginalSource as ToggleButton;
                if (toggle != null && toggle.Tag is string) Safe(() => { var p = State.Profiles.First(v => v.Id == (string)toggle.Tag); SetGroup(p, !GroupState.Enabled(State, p.Id)); RenderProfiles(); });
            }));
            var variables = Find<DataGrid>("VariablesGrid");
            variables.MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            { if (variables.SelectedItems.Count == 1 && Ancestor<DataGridRow>(e.OriginalSource as DependencyObject) != null && Ancestor<CheckBox>(e.OriginalSource as DependencyObject) == null) Safe(() => Edit(variables.SelectedItem as Row)); };
            variables.SelectionChanged += delegate { if (!updatingRows) UpdateSelection(); };
            SelectionHeader.Click += delegate { if (variables.Items.Count > 0 && variables.SelectedItems.Count == variables.Items.Count) variables.UnselectAll(); else variables.SelectAll(); UpdateSelection(); };
            variables.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject);
                if (row == null || Ancestor<CheckBox>(e.OriginalSource as DependencyObject) == null) return;
                if (variables.SelectedItems.Contains(row.Item)) variables.SelectedItems.Remove(row.Item); else variables.SelectedItems.Add(row.Item);
                variables.Focus(); e.Handled = true;
            };
            var moveMenu = new ContextMenu(); var moveItem = new MenuItem { Header = "移动变量…" }; moveItem.Click += delegate { Safe(MoveVariable); }; moveMenu.Items.Add(moveItem);
            moveMenu.Opened += delegate { moveItem.IsEnabled = variables.SelectedItems.Count > 0; moveItem.Header = variables.SelectedItems.Count > 1 ? "批量移动 " + variables.SelectedItems.Count + " 个变量…" : "移动变量…"; }; variables.ContextMenu = moveMenu;
            variables.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                var row = Ancestor<DataGridRow>(e.OriginalSource as DependencyObject); if (row == null) return;
                if (!variables.SelectedItems.Contains(row.Item)) { variables.UnselectAll(); variables.SelectedItems.Add(row.Item); }
                e.Handled = true;
            };
            Find<DataGrid>("HistoryGrid").MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            {
                var h = Find<DataGrid>("HistoryGrid").SelectedItem as HistoryView;
                if (h != null && Ancestor<DataGridRow>(e.OriginalSource as DependencyObject) != null)
                    Dialogs.BuildPreviewWindow(Window, new Plan { Label = h.Item.Label, Changes = h.Item.Changes, CompositionNote = h.Item.CompositionNote,
                        ProfileChanges = h.Item.ProfileChanges ?? new List<ProfileEntryChange>(),
                        DescriptionChanges = h.Item.DescriptionChanges ?? new List<DescriptionChange>() }, true, demo).ShowDialog();
            };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!quitting && !capture) { e.Cancel = true; Window.Hide(); } };
            Window.StateChanged += delegate { if (Window.WindowState == WindowState.Minimized && !capture) Window.Hide(); };
            Window.Activated += delegate { if (!rendering) Safe(Render); };
            Window.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.F5) { Safe(Render); e.Handled = true; }
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { Safe(NewProfile); e.Handled = true; }
            };
        }

        static T Ancestor<T>(DependencyObject item) where T : DependencyObject
        {
            while (item != null) { var found = item as T; if (found != null) return found; item = System.Windows.Media.VisualTreeHelper.GetParent(item); } return null;
        }
        public void Navigate(string page, string id) { route = page; if (id != null) selectedId = id; Render(); }
        public void Render()
        {
            rendering = true;
            try
            {
                var p = SelectedProfile; bool profile = route == "profile" && p != null; bool history = route == "history";
                bool active = profile && GroupState.Enabled(State, p.Id);
                Text("Breadcrumb", history ? "工作区  /  切换记录" : profile ? "工作区  /  配置组" : "工作区  /  本机环境");
                Text("PageTitle", history ? "切换记录" : profile ? p.Name : "本机环境");
                Text("PageSubtitle", history ? "每次写入前自动备份，双击记录查看详细变化。" : profile ? String.IsNullOrWhiteSpace(p.Note) ? "为你的工具链准备一组环境变量。" : p.Note : "查看并管理 Windows 用户变量和系统变量。双击变量进行编辑。");
                string global = State.ActiveGroups.Count == 0 ? "当前未启用配置组" : "已启用 " + State.ActiveGroups.Count + " 个配置组";
                if (State.ActiveGroups.Count > 0)
                {
                    var snapshot = EnvironmentBackend.Read(); int changed = GroupState.Effective(State.ActiveGroups).Values.Count(v => !Variable.Equal(snapshot.Get(v.Name, v.Scope), v));
                    if (changed > 0) global += " · " + changed + " 项外部修改";
                }
                Text("GlobalStatus", demo ? "演示模式 · 不写入系统" : global);
                Show("RecoveryBanner", store.LoadPending() != null);
                Show("SummaryPanel", profile); Show("ProfileMenuButton", profile); Show("ApplyButton", profile || history); Show("StopButton", active); Show("VariableToolbar", !history);
                Show("VariablesGrid", !history); Show("HistoryGrid", history);
                Show("ApplicationFilter", !profile && !history);
                RenderApplicationFilter(); Find<TextBlock>("GlobalStatus").ToolTip = String.Join(" → ", State.ActiveGroups.Select(g => g.ProfileName)) + "（后一组优先）";
                Find<Button>("ApplyButton").Content = history ? "恢复上次状态" : active ? "应用配置组更新" : "启用配置组";
                Find<Button>("ApplyButton").IsEnabled = history ? State.History.Count > 0 : p != null && (active || p.Entries.Count > 0);
                Find<Button>("AdminButton").Content = demo ? "◈  演示环境" : WindowsEnvironment.IsAdministrator ? "◈  已使用管理员权限" : "◈  以管理员身份重启";
                RenderStartup();
                if (profile)
                {
                    Text("VariableCount", p.Entries.Count.ToString()); Text("ScopeSummary", p.Entries.Any(v => v.Scope == 1) ? p.Entries.Any(v => v.Scope == 0) ? "用户 + 系统变量" : "系统变量" : "用户变量"); Text("ActiveSummary", active ? "已启用 · 优先级 " + (State.ActiveGroups.FindIndex(g => g.ProfileId == p.Id) + 1) : "尚未启用");
                }
                Text("InfoText", (history ? "关闭软件会保留当前环境。使用“停用”恢复启用前的值，或恢复最近一次修改。" : profile ? active ? "可同时启用多个配置组。更新保持优先级；停用此组后重新计算其余组，全部停用后恢复原值。" : "可同时启用多个配置组；同名同范围由后启用的组优先，PATH 前置/追加依次合并。" : "已有终端保留旧环境。修改后可点击“新建终端”，让新进程使用当前环境。") + (history ? "" : " 勾选多项可批量移动。"));
                Text("StatusText", demo ? "模拟环境，所有修改仅发生在内存中" : State.ActiveGroups.Count == 0 ? "就绪 · 本地保存，无网络请求" : "已启用：" + String.Join("、", State.ActiveGroups.Select(g => g.ProfileName)) + " · 原值已备份");
                var nav = Find<Button>("EnvironmentNav"); nav.Background = Ui.Color(route == "environment" ? "#DEEADF" : "Transparent"); nav.Foreground = Ui.Color(route == "environment" ? "#296C47" : "#667570");
                Find<Button>("HistoryNav").Background = Ui.Color(history ? "#DEEADF" : "Transparent");
                RenderProfiles(); RenderRows(); RenderTray();
            }
            finally { rendering = false; }
        }

        void RenderProfiles()
        {
            bool old = rendering; rendering = true;
            var search = Find<TextBox>("ProfileSearch").Text;
            var views = State.Profiles.Where(p => String.IsNullOrWhiteSpace(search) || p.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(p => new ProfileView { Id = p.Id, Name = p.Name, IsActive = GroupState.Enabled(State, p.Id), Subtitle = p.Entries.Count + " 个变量" + (GroupState.Enabled(State, p.Id) ? " · 已启用 · 优先级 " + (State.ActiveGroups.FindIndex(g => g.ProfileId == p.Id) + 1) : "") }).ToList();
            Find<ListBox>("ProfilesBox").ItemsSource = views; Find<ListBox>("ProfilesBox").SelectedItem = route == "profile" ? views.FirstOrDefault(p => p.Id == selectedId) : null;
            rendering = old;
        }

        void RenderRows()
        {
            if (route == "history")
            {
                Find<DataGrid>("HistoryGrid").ItemsSource = State.History.Select(h => new HistoryView { Item = h, Time = h.Time, Label = h.Label, Count = (h.Changes.Count + (h.ProfileChanges == null ? 0 : h.ProfileChanges.Count) + (h.DescriptionChanges == null ? 0 : h.DescriptionChanges.Count)) + " 项" }).ToList();
                Show("EmptyPanel", State.History.Count == 0); Text("EmptyTitle", "还没有切换记录"); Text("EmptySubtitle", "启用配置组或编辑本机变量后，会自动记录变化。"); Text("TableCount", State.History.Count + " 条记录"); return;
            }
            bool reveal = Find<CheckBox>("RevealValues").IsChecked == true; var search = Find<TextBox>("VariableSearch").Text; int filter = Find<ComboBox>("ScopeFilter").SelectedIndex;
            bool local = route != "profile" || SelectedProfile == null; int application = Find<ComboBox>("ApplicationFilter").SelectedIndex;
            List<Row> rows;
            if (route == "profile" && SelectedProfile != null)
            {
                rows = SelectedProfile.Entries.Select(e => new Row { Entry = e, Name = e.Name, Description = e.Description ?? "", DisplayValue = e.Mode == "Delete" ? "启用时删除此变量" : !reveal && Ui.Secret(e.Name, e.Value) ? "••••••••••••" : e.Value == "" ? "（空字符串）" : e.Value, ScopeText = Ui.Scope(e.Scope), KindText = e.Kind == 2 ? "可展开" : "字符串", ModeText = Ui.Mode(e.Mode) }).ToList();
            }
            else
            {
                var groups = State.ActiveGroups;
                rows = EnvironmentBackend.Read().Values.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Scope).Select(v => {
                    var owners = GroupState.Owners(groups, v.Key);
                    return new Row { Variable = v, Name = v.Name, Description = LocalDescriptions.Get(State, v.Key) ?? "", DisplayValue = Ui.ValueText(v, reveal), ScopeText = Ui.Scope(v.Scope), IsManaged = owners.Count > 0,
                        GroupIds = owners.Select(g => g.ProfileId).ToList(), OwnerText = String.Join(" → ", owners.Select(g => g.ProfileName)), OwnerExplanation = "配置组叠加顺序（后一组优先）：" + String.Join(" → ", owners.Select(g => g.ProfileName)),
                        ModeText = owners.Count > 0 ? "配置组管理" : "本机值", KindText = v.Kind == 2 ? "可展开" : v.Kind == 1 ? "字符串" : "只读类型" }; }).ToList();
            }
            int allCount = rows.Count;
            rows = rows.Where(r => (filter == 0 || (r.Entry != null ? r.Entry.Scope : r.Variable.Scope) == filter - 1) &&
                (!local || application <= 0 || (application == 1 && !r.IsManaged) || (application == 2 && r.IsManaged) || (application >= 3 && r.GroupIds.Contains((string)((ComboBoxItem)Find<ComboBox>("ApplicationFilter").SelectedItem).Tag))) &&
                (String.IsNullOrWhiteSpace(search) || r.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    r.DisplayValue.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || r.Description.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            var grid = Find<DataGrid>("VariablesGrid"); string context = route == "profile" ? "profile:" + selectedId : route;
            var selected = rowsContext == context ? new HashSet<string>(grid.SelectedItems.Cast<Row>().Select(r => r.Entry == null ? r.Variable.Key : r.Entry.Key)) : new HashSet<string>();
            updatingRows = true;
            try { grid.ItemsSource = rows; foreach (var row in rows) if (selected.Contains(row.Entry == null ? row.Variable.Key : row.Entry.Key)) grid.SelectedItems.Add(row); }
            finally { updatingRows = false; }
            rowsContext = context; visibleRowCount = rows.Count; totalRowCount = allCount; UpdateSelection();
            bool noActiveScheme = local && application >= 2 && State.ActiveGroups.Count == 0;
            Show("EmptyPanel", rows.Count == 0); Text("EmptyTitle", noActiveScheme ? "还没有配置组管理的变量" : allCount == 0 ? "从一个变量开始" : "没有匹配的变量");
            Text("EmptySubtitle", noActiveScheme ? "启用一个配置组后，可在这里查看它管理的本机变量。" : allCount == 0 ? "添加 JAVA_HOME、代理地址或一组 PATH 目录" : "试试其他关键词，或选择全部范围和全部方式。");
        }

        void UpdateSelection()
        {
            int count = Find<DataGrid>("VariablesGrid").SelectedItems.Count;
            Find<Button>("EditVariable").IsEnabled = count == 1; Find<Button>("DeleteVariable").IsEnabled = count == 1;
            var move = Find<Button>("MoveVariable"); move.IsEnabled = count > 0; move.Content = count > 1 ? "批量移动" : "移动";
            move.ToolTip = count > 1 ? "将选中的 " + count + " 个变量一起移动" : "移动到本机用户/系统变量，或其他配置组";
            SelectionHeader.IsEnabled = visibleRowCount > 0; SelectionHeader.IsChecked = count == 0 ? (bool?)false : count == visibleRowCount ? (bool?)true : null;
            Text("TableCount", visibleRowCount + " / " + totalRowCount + " 个变量" + (count > 0 ? " · 已选 " + count + " 项" : ""));
        }

        void Mutate(Action<State> change)
        { var next = Json.Copy(State); change(next); GroupState.Normalize(next); store.Save(next); State = next; Render(); }
        void NewProfile()
        {
            var p = Dialogs.ProfileEditor(Window, null); if (p == null) return;
            selectedId = p.Id; route = "profile"; Mutate(s => s.Profiles.Add(p)); Status("已创建配置组 · " + p.Name);
        }
        void Edit(Row row)
        {
            bool local = route != "profile"; Entry entry = null;
            if (local && row != null && (row.IsManaged || Planner.IsProtected(row.Name) || (row.Variable.Kind != 1 && row.Variable.Kind != 2)))
            {
                string description = null;
                var dialog = Dialogs.BuildDescriptionWindow(Window, row.Variable, row.Description, text => description = text);
                if (dialog.ShowDialog() == true) SaveLocalDescription(row.Variable.Key, description);
                return;
            }
            if (row != null)
            {
                if (row.Entry != null) entry = row.Entry.Copy();
                else { var v = row.Variable; entry = new Entry { Name = v.Name, Value = v.Value, Scope = v.Scope, Kind = v.Kind, Mode = "Set", Description = row.Description }; }
            }
            Entry edited = null;
            var editWindow = Dialogs.BuildEntryWindow(Window, entry, local, value =>
            {
                if (!local && SelectedProfile != null && SelectedProfile.Entries.Any(e => e.Key == value.Key && (entry == null || e.Key != entry.Key))) throw new Exception("当前配置组中已存在同范围变量：" + value.Name);
                edited = value;
            });
            editWindow.ShowDialog(); if (edited == null) return;
            if (local)
            {
                if (edited.SameOperation(entry)) SaveLocalDescription(edited.Key, edited.Description);
                else ApplyPlan(GroupPlanner.Edit(EnvironmentBackend.Read(), State, edited));
                return;
            }
            if (SelectedProfile == null) return;
            string oldKey = entry == null ? null : entry.Key;
            if (SelectedProfile.Entries.Any(e => e.Key == edited.Key && e.Key != oldKey)) throw new Exception("当前配置组中已存在同范围变量：" + edited.Name);
            Mutate(s => { var profile = s.Profiles.First(p => p.Id == selectedId); int index = profile.Entries.FindIndex(e => e.Key == oldKey); if (index < 0) profile.Entries.Add(edited); else profile.Entries[index] = edited; }); Status("已保存变量 · " + edited.Name);
        }
        void SaveLocalDescription(string key, string description)
        {
            if ((description ?? "") == (LocalDescriptions.Get(State, key) ?? "")) return;
            Mutate(s => LocalDescriptions.Set(s, key, description));
            Status("已保存本机变量描述");
        }
        void DeleteVariable()
        {
            var row = Find<DataGrid>("VariablesGrid").SelectedItem as Row; if (row == null) return;
            if (route == "profile")
            { if (Dialogs.Confirm(Window, "移除配置组变量", "将 " + row.Name + " 从当前配置组移除。更新已启用配置组后会恢复该变量原值。", "移除")) Mutate(s => s.Profiles.First(p => p.Id == selectedId).Entries.RemoveAll(e => e.Key == row.Entry.Key)); }
            else { var v = row.Variable; ApplyPlan(GroupPlanner.Edit(EnvironmentBackend.Read(), State, new Entry { Name = v.Name, Value = "", Scope = v.Scope, Kind = v.Kind, Mode = "Delete" })); }
        }
        void MoveVariable()
        {
            var rows = Find<DataGrid>("VariablesGrid").SelectedItems.Cast<Row>().ToList(); if (rows.Count == 0) return;
            var sourceProfile = rows[0].Entry == null ? null : SelectedProfile;
            var entries = rows.Select(row => row.Entry == null ? new Entry { Name = row.Variable.Name, Value = row.Variable.Value, Scope = row.Variable.Scope, Kind = row.Variable.Kind, Mode = "Set" } : row.Entry.Copy()).ToList();
            Plan plan = null;
            Dialogs.BuildMoveWindow(Window, State, sourceProfile, entries, requests => { plan = MovePlanner.MoveBatch(EnvironmentBackend.Read(), State, requests); }).ShowDialog();
            if (plan != null) ApplyPlan(plan);
        }
        void Apply()
        {
            if (route == "history") { if (State.History.Count > 0) ApplyPlan(Planner.Undo(EnvironmentBackend.Read(), State.History[0], State)); }
            else SetGroup(SelectedProfile, true);
        }
        void SetGroup(Profile target, bool enabled)
        {
            if (target == null) return;
            try { ApplyPlan(GroupPlanner.SetEnabled(EnvironmentBackend.Read(), State, target, enabled)); }
            catch (ConflictException ex)
            {
                if (enabled) throw;
                if (Dialogs.Confirm(Window, "保留外部修改", ex.Message, "保留外部修改并停用此组")) ApplyPlan(GroupPlanner.SetEnabled(EnvironmentBackend.Read(), State, target, false, true));
            }
            finally { Render(); }
        }
        void StopAll()
        {
            try { ApplyPlan(GroupPlanner.DisableAll(EnvironmentBackend.Read(), State)); }
            catch (ConflictException ex) { if (Dialogs.Confirm(Window, "保留外部修改", ex.Message, "保留外部修改并全部停用")) ApplyPlan(GroupPlanner.DisableAll(EnvironmentBackend.Read(), State, true)); }
        }
        void RenderApplicationFilter()
        {
            var filter = Find<ComboBox>("ApplicationFilter"); var selected = filter.SelectedItem as ComboBoxItem;
            string key = selected == null ? "all" : (string)selected.Tag;
            filter.Items.Clear();
            filter.Items.Add(new ComboBoxItem { Content = "全部方式", Tag = "all" }); filter.Items.Add(new ComboBoxItem { Content = "本机值", Tag = "local" }); filter.Items.Add(new ComboBoxItem { Content = "配置组管理", Tag = "managed" });
            foreach (var g in State.ActiveGroups) filter.Items.Add(new ComboBoxItem { Content = "组 · " + g.ProfileName, Tag = g.ProfileId });
            filter.SelectedItem = filter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == key) ?? filter.Items[2];
            filter.ToolTip = "筛选应用方式或某个已启用配置组。叠加顺序：" + String.Join(" → ", State.ActiveGroups.Select(g => g.ProfileName)) + "（后一组优先）";
            var column = Find<DataGrid>("VariablesGrid").Columns[4]; bool local = route == "environment";
            column.Header = local ? "应用方式 / 来源" : "应用方式"; column.Width = new DataGridLength(local ? 126 : 96);
        }
        void ApplyPlan(Plan plan)
        {
            if (!Dialogs.Preview(Window, plan, demo)) return;
            State = engine.Execute(plan, State); Render(); Status(plan.Label + " · " + (plan.Changes.Count + plan.ProfileChanges.Count + plan.DescriptionChanges.Count) + " 项修改已保存");
        }
        void Status(string message) { Text("StatusText", message); }
        void ProfileMenu()
        {
            var profile = SelectedProfile; if (profile == null) return; var menu = new ContextMenu();
            Action<string, Action> add = (label, action) => { var item = new MenuItem { Header = label }; item.Click += delegate { Safe(action); }; menu.Items.Add(item); };
            add("名称与说明", () => { var edited = Dialogs.ProfileEditor(Window, profile); if (edited != null) Mutate(s => { var p = s.Profiles.First(v => v.Id == profile.Id); p.Name = edited.Name; p.Note = edited.Note; foreach (var g in s.ActiveGroups.Where(g => g.ProfileId == p.Id)) g.ProfileName = p.Name; }); });
            if (GroupState.Enabled(State, profile.Id) && State.ActiveGroups.Last().ProfileId != profile.Id) add("提高到最高优先级", () => ApplyPlan(GroupPlanner.Prioritize(EnvironmentBackend.Read(), State, profile)));
            add("复制配置组", () => { var copy = Json.Copy(profile); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += "（副本）"; selectedId = copy.Id; Mutate(s => s.Profiles.Add(copy)); });
            menu.Items.Add(new Separator());
            add("删除配置组", () => { if (GroupState.Enabled(State, profile.Id)) throw new Exception("请先停用此配置组，再删除它。"); if (Dialogs.Confirm(Window, "删除配置组", "删除“" + profile.Name + "”及其保存的变量。", "删除")) { Mutate(s => s.Profiles.RemoveAll(p => p.Id == profile.Id)); selectedId = State.Profiles.Count > 0 ? State.Profiles[0].Id : null; route = selectedId == null ? "environment" : "profile"; Render(); } });
            menu.PlacementTarget = Find<Button>("ProfileMenuButton"); menu.IsOpen = true;
        }
        void Import()
        {
            var dialog = new OpenFileDialog { Title = "导入 SwitchEnvVar 配置组", Filter = "SwitchEnvVar JSON|*.json" }; if (dialog.ShowDialog(Window) != true) return;
            var imported = Json.Read<State>(dialog.FileName); FileStore.Check(imported);
            foreach (var p in imported.Profiles) { var keys = new HashSet<string>(); foreach (var e in p.Entries) { Planner.Validate(e); if (!keys.Add(e.Key)) throw new Exception(p.Name + " 包含重复变量：" + e.Name); } }
            if (imported.Profiles.Count > 200) throw new Exception("单次最多导入 200 个配置组。");
            Mutate(s => { foreach (var original in imported.Profiles) { var p = Json.Copy(original); p.Id = Guid.NewGuid().ToString("N"); if (s.Profiles.Any(v => v.Name == p.Name)) p.Name += "（导入）"; s.Profiles.Add(p); } }); Status("已导入 " + imported.Profiles.Count + " 个配置组，尚未启用");
        }
        void Export()
        {
            if (State.Profiles.Any(p => p.Entries.Any(e => Ui.Secret(e.Name, e.Value))) && !Dialogs.Confirm(Window, "导出包含敏感值", "配置组包含密钥或密码。导出的 JSON 会保存完整值，请妥善保管。", "继续导出")) return;
            var dialog = new SaveFileDialog { Title = "导出全部配置组", Filter = "SwitchEnvVar JSON|*.json", FileName = "switchenv-profiles.json" }; if (dialog.ShowDialog(Window) != true) return;
            var exported = new State { Profiles = Json.Copy(State.Profiles) }; Json.Write(dialog.FileName, exported); Status("配置组已导出（不含本机备份与历史）");
        }
        void Elevate()
        {
            if (demo || WindowsEnvironment.IsAdministrator) { Status(demo ? "演示模式不需要提权" : "已经使用管理员权限"); return; }
            // The new process waits until this process releases the data-directory mutex.
            string sid; using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            if (AdminBootstrap.Request(Process.GetCurrentProcess().MainModule.FileName, new[] { "--data-dir", store.DirectoryPath, "--wait-instance" }, sid)) Quit();
        }
        void RenderStartup()
        {
            var toggle = Find<ToggleButton>("StartupToggle");
            toggle.IsEnabled = startup != null;
            if (startup == null)
            {
                toggle.IsChecked = false; toggle.ToolTip = "演示模式不能修改开机自启设置。"; return;
            }
            try
            {
                toggle.IsChecked = startup.IsEnabled;
                toggle.ToolTip = startupMigrationError == null ? "开启后，当前账号登录 Windows 时以管理员权限启动到托盘。移动程序后请重新开启。" : "旧版自启升级失败。请重新开启以重试：" + startupMigrationError;
            }
            catch (Exception ex)
            {
                toggle.IsEnabled = false; toggle.IsChecked = false;
                toggle.ToolTip = "无法读取开机自启设置：" + ex.Message;
            }
        }
        void UpdateStartup()
        {
            if (startup == null) { RenderStartup(); return; }
            bool enabled = Find<ToggleButton>("StartupToggle").IsChecked == true;
            try
            {
                startup.SetEnabled(enabled); startupMigrationError = null; Render();
                Status(enabled ? "已开启管理员自启 · 登录 Windows 后驻留托盘" : "已关闭开机自启");
            }
            catch { Render(); throw; }
        }
        void Help()
        {
            MessageBox.Show(Window, "1. 左侧新建配置组，右侧添加并编辑变量。\n2. 点击启用，检查预览后应用；可同时启用多个配置组，后启用的组优先。\n3. PATH 可选择前置或追加，并逐项调整。\n4. 停用某组会保留其他组；全部停用后恢复原值，外部修改会提示冲突。\n5. 正常打开时默认申请管理员权限，请使用当前账号确认 Windows 权限提示。演示模式不需要提权。\n6. 关闭窗口会进入托盘。退出程序后仍保留已启用的环境。\n7. 数据保存在程序旁的 data 文件夹，可随程序一起迁移；备份中可能含敏感值。\n8. 新建终端会读取最新环境，已经打开的应用可能需要重启。\n9. 左下角可开启“开机自启”，当前账号登录后以管理员权限启动到托盘。移动程序后请重新开启。\n10. 勾选变量或用 Ctrl/Shift 多选，再点击“批量移动”；表头或 Ctrl+A 可全选当前列表。支持用户/系统、本机/配置组及配置组之间移动，统一预览、整体恢复。\n11. 本机环境可按“全部方式 / 本机值 / 配置组管理”或某个已启用组筛选，与范围和搜索组合；全选只选当前结果。\n\n快捷键：Ctrl+N 新建配置组，F5 刷新。", "SwitchEnvVar 使用帮助", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        void CreateTray()
        {
            tray = new Forms.NotifyIcon { Text = "SwitchEnvVar · 环境变量管理" };
            using (var resource = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchEnv.app.ico")) tray.Icon = new System.Drawing.Icon(resource);
            tray.DoubleClick += delegate { Window.Dispatcher.BeginInvoke(new Action(ShowWindow)); }; RenderTray();
            tray.Visible = true;
        }
        void RenderTray()
        {
            if (tray == null) return; var menu = new Forms.ContextMenuStrip();
            Action<string, Action> add = (label, action) => { var item = menu.Items.Add(label); item.Click += delegate { Window.Dispatcher.BeginInvoke(new Action(() => { ShowWindow(); Safe(action); })); }; };
            add("打开 SwitchEnvVar", ShowWindow); menu.Items.Add(new Forms.ToolStripSeparator());
            foreach (var p in State.Profiles) { var current = p; add((GroupState.Enabled(State, p.Id) ? "✓  " : "    ") + p.Name, () => SetGroup(current, !GroupState.Enabled(State, current.Id))); }
            if (State.ActiveGroups.Count > 0) add("停用全部配置组", StopAll);
            menu.Items.Add(new Forms.ToolStripSeparator()); add("退出（保留当前环境）", Quit);
            var old = tray.ContextMenuStrip; tray.ContextMenuStrip = menu; if (old != null) old.Dispose();
        }
        void ShowWindow()
        {
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            if (!Window.IsVisible) Window.Show();
            Window.Activate();
        }
        public void Reopen()
        {
            ShowWindow();
            // Ask Windows to register the icon again in case the taskbar lost it.
            if (tray != null)
            {
                tray.Visible = false;
                tray.Visible = true;
            }
        }
        public void Quit() { quitting = true; Dispose(); Application.Current.Shutdown(); }
        public void Dispose() { if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } }
    }
}
