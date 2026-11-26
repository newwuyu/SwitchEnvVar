using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SwitchEnv
{
    public static class Ui
    {
        public static Brush Color(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        public static TextBlock Text(string text, double size, string color)
        { return new TextBlock { Text = text, FontSize = size, Foreground = Color(color), TextWrapping = TextWrapping.Wrap }; }
        public static Button Button(string label, bool primary)
        { return new Button { Content = label, Style = (Style)Application.Current.FindResource(primary ? "PrimaryButton" : "ButtonBaseStyle"), MinWidth = 86 }; }
        public static Window Dialog(Window owner, string title, double width, double height)
        { return new Window { Owner = owner, Title = title + " — SwitchEnv", Width = width, Height = height, MinWidth = width, MinHeight = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize }; }
        public static bool Secret(string name, string value)
        { return Regex.IsMatch(name ?? "", "KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|PASSWD", RegexOptions.IgnoreCase) || Regex.IsMatch(value ?? "", "^(sk-|ghp_|github_pat_|xox[baprs]-|glpat-)"); }
        public static string ValueText(Variable v, bool reveal)
        { return !v.Exists ? "（不存在）" : (!reveal && Secret(v.Name, v.Value) ? "••••••••••••" : v.Value == "" ? "（空字符串）" : v.Value); }
        public static string Scope(int scope) { return scope == 1 ? "系统" : "用户"; }
        public static string Mode(string mode) { return mode == "Delete" ? "删除变量" : mode == "Prepend" ? "前置列表" : mode == "Append" ? "追加列表" : "设置值"; }
    }

    public class Row
    {
        public Entry Entry { get; set; }
        public Variable Variable { get; set; }
        public string Name { get; set; }
        public string DisplayValue { get; set; }
        public string Description { get; set; }
        public string ScopeText { get; set; }
        public string ModeText { get; set; }
        public bool IsManaged { get; set; }
        public List<string> GroupIds { get; set; }
        public string OwnerText { get; set; }
        public string OwnerExplanation { get; set; }
        public string KindText { get; set; }
    }
    public class ProfileView
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Subtitle { get; set; }
        public bool IsActive { get; set; }
    }
    public class HistoryView
    {
        public History Item { get; set; }
        public string Time { get; set; }
        public string Label { get; set; }
        public string Count { get; set; }
    }

    public static class Dialogs
    {
        public static void Error(Window owner, Exception ex) { MessageBox.Show(owner, ex.Message, "SwitchEnv", MessageBoxButton.OK, MessageBoxImage.Warning); }
        public static bool Confirm(Window owner, string title, string text, string affirmative)
        {
            var w = Ui.Dialog(owner, title, 480, 260); w.SizeToContent = SizeToContent.Height; w.MaxHeight = 700; var panel = new StackPanel { Margin = new Thickness(25) };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
            var body = Ui.Text(text, 13, "#6E7E73"); body.Margin = new Thickness(0, 15, 0, 24); panel.Children.Add(body);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = Ui.Button("取消", false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0); buttons.Children.Add(cancel);
            var yes = Ui.Button(affirmative, true); yes.Click += delegate { w.DialogResult = true; }; buttons.Children.Add(yes); panel.Children.Add(buttons); w.Content = panel;
            return w.ShowDialog() == true;
        }

        public static Profile ProfileEditor(Window owner, Profile existing)
        {
            var p = existing == null ? new Profile { Name = "新配置组" } : Json.Copy(existing);
            var w = Ui.Dialog(owner, existing == null ? "新建配置组" : "配置组设置", 500, 400);
            var panel = new StackPanel { Margin = new Thickness(27) };
            panel.Children.Add(new TextBlock { Text = existing == null ? "创建一组环境" : "配置组设置", FontSize = 21, FontWeight = FontWeights.SemiBold });
            var subtitle = Ui.Text("给不同的开发环境、代理或工具链起一个名字。", 12, "#859388"); subtitle.Margin = new Thickness(0, 9, 0, 20); panel.Children.Add(subtitle);
            panel.Children.Add(Ui.Text("配置组名称", 12, "#748578")); var name = new TextBox { Text = p.Name, Margin = new Thickness(0, 7, 0, 16), MaxLength = 60 }; panel.Children.Add(name);
            panel.Children.Add(Ui.Text("说明（可选）", 12, "#748578")); var note = new TextBox { Text = p.Note, Margin = new Thickness(0, 7, 0, 22), MaxLength = 200 }; panel.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = Ui.Button("取消", false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0); buttons.Children.Add(cancel);
            var save = Ui.Button(existing == null ? "创建配置组" : "保存", true); save.IsDefault = true; save.Click += delegate { if (String.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; } p.Name = name.Text.Trim(); p.Note = note.Text.Trim(); w.DialogResult = true; }; buttons.Children.Add(save); panel.Children.Add(buttons);
            w.Content = panel; w.Loaded += delegate { name.Focus(); name.SelectAll(); }; return w.ShowDialog() == true ? p : null;
        }

        class MoveTarget
        {
            public string Label { get; set; }
            public string ProfileId;
            public int Scope;
        }
        public static Window BuildMoveWindow(Window owner, State state, Profile sourceProfile, Entry entry, Action<MoveRequest> accepted)
        {
            return BuildMoveWindow(owner, state, sourceProfile, new[] { entry }, requests => accepted(requests.Single()));
        }

        public static Window BuildMoveWindow(Window owner, State state, Profile sourceProfile, IList<Entry> entries, Action<List<MoveRequest>> accepted)
        {
            if (entries == null || entries.Count == 0) throw new Exception("请先选择要移动的变量。");
            bool batch = entries.Count > 1; var entry = entries[0];
            var w = Ui.Dialog(owner, batch ? "批量移动变量" : "移动变量", batch ? 720 : 640, batch ? 640 : 460); var root = new Grid { Margin = new Thickness(28) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new StackPanel(); header.Children.Add(new TextBlock { Text = batch ? "批量移动 · " + entries.Count + " 个变量" : "移动 " + entry.Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
            string sourceScope = entries.Select(e => e.Scope).Distinct().Count() > 1 ? "用户 + 系统变量" : Ui.Scope(entry.Scope) + "变量";
            var source = Ui.Text("来源：" + (sourceProfile == null ? "本机环境" : "配置组 · " + sourceProfile.Name) + " / " + sourceScope, 12, "#829388"); source.Margin = new Thickness(0, 10, 0, 23); header.Children.Add(source); root.Children.Add(header);
            var form = new StackPanel(); var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            if (batch)
            {
                form.Children.Add(Ui.Text("已选择的变量", 12, "#748578"));
                var list = new ListBox { ItemsSource = entries.Select(e => e.Name + "   ·   " + Ui.Scope(e.Scope) + "变量   ·   " + Ui.Mode(e.Mode)).ToList(), Height = 102, Background = Brushes.White, BorderBrush = Ui.Color("#DFE6E3"), BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 7, 0, 18), FontSize = 12 };
                form.Children.Add(list);
            }
            var targets = new List<MoveTarget> { new MoveTarget { Label = "本机环境 · 用户变量", Scope = 0 }, new MoveTarget { Label = "本机环境 · 系统变量", Scope = 1 } };
            targets.AddRange(state.Profiles.Select(p => new MoveTarget { Label = "配置组 · " + p.Name + (GroupState.Enabled(state, p.Id) ? "（已启用）" : ""), ProfileId = p.Id, Scope = entry.Scope }));
            form.Children.Add(Ui.Text("移动到", 12, "#748578"));
            var destination = new ComboBox { Name = "MoveDestination", ItemsSource = targets, DisplayMemberPath = "Label", Margin = new Thickness(0, 7, 0, 17) }; form.Children.Add(destination);
            var scopeLabel = Ui.Text("配置组中的作用范围", 12, "#748578"); form.Children.Add(scopeLabel);
            var scope = new ComboBox { Name = "MoveScope", ItemsSource = batch ? new[] { "保留各变量原范围", "用户变量 · 当前账号", "系统变量 · 所有账号" } : new[] { "用户变量 · 当前账号", "系统变量 · 所有账号" }, SelectedIndex = batch ? 0 : entry.Scope, Margin = new Thickness(0, 7, 0, 18) }; form.Children.Add(scope);
            int profileScope = batch ? 0 : entry.Scope; bool changingDestination = false;
            scope.SelectionChanged += delegate { var selected = destination.SelectedItem as MoveTarget; if (!changingDestination && selected != null && selected.ProfileId != null) profileScope = scope.SelectedIndex; };
            var hint = Ui.Text("", 12, "#6F8D7A"); form.Children.Add(hint);
            Action update = () =>
            {
                var target = destination.SelectedItem as MoveTarget; if (target == null) return;
                changingDestination = true;
                scope.IsEnabled = target.ProfileId != null; scope.SelectedIndex = target.ProfileId == null ? target.Scope + (batch ? 1 : 0) : profileScope;
                changingDestination = false;
                string description = "确认后从来源移除；下一步会显示全部修改预览。";
                if (target.ProfileId != null && GroupState.Enabled(state, target.ProfileId)) description += "\n目标配置组已启用，相关变量会立即应用。";
                else if (target.ProfileId != null) description += batch ? "\n保留每项的应用方式，启用目标配置组后才会写入本机。" : "\n保留“" + Ui.Mode(entry.Mode) + "”方式，启用目标配置组后才会写入本机。";
                else description += batch ? "\n按各项的应用方式写入本机；已在目标位置的项会保留。" : "\n按“" + Ui.Mode(entry.Mode) + "”方式写入本机环境。系统变量需要管理员权限。";
                if (sourceProfile != null && GroupState.Enabled(state, sourceProfile.Id)) description += "\n移出后，来源配置组不再管理" + (batch ? "这些变量。" : "这个变量。");
                if (batch) description += "\n整批只生成一条记录，可一起恢复。";
                hint.Text = description;
            };
            destination.SelectionChanged += delegate { update(); };
            destination.SelectedIndex = sourceProfile == null ? 1 - entry.Scope : targets.FindIndex(t => t.ProfileId != null && t.ProfileId != sourceProfile.Id);
            if (destination.SelectedIndex < 0) destination.SelectedIndex = entry.Scope;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetRow(buttons, 2); root.Children.Add(buttons);
            var cancel = Ui.Button("取消", false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0); buttons.Children.Add(cancel);
            var move = Ui.Button("预览移动", true); move.Name = "MovePreview"; move.IsDefault = true; move.Click += delegate
            {
                try
                {
                    var target = destination.SelectedItem as MoveTarget; if (target == null) return;
                    accepted(entries.Select(item => new MoveRequest { Name = item.Name, SourceScope = item.Scope, SourceProfileId = sourceProfile == null ? null : sourceProfile.Id, TargetProfileId = target.ProfileId,
                        TargetScope = target.ProfileId == null ? target.Scope : batch ? scope.SelectedIndex == 0 ? item.Scope : scope.SelectedIndex - 1 : scope.SelectedIndex }).ToList());
                    w.DialogResult = true;
                }
                catch (Exception ex) { Error(w, ex); }
            };
            buttons.Children.Add(move); w.Content = root; return w;
        }

        public static Window BuildEntryWindow(Window owner, Entry source, bool local, Action<Entry> accepted)
        {
            var e = source == null ? new Entry { Name = "", Value = "", Scope = 0, Kind = 1, Mode = "Set" } : source.Copy();
            var w = Ui.Dialog(owner, source == null ? "添加变量" : "编辑变量", 670, 660);
            var root = new Grid { Margin = new Thickness(28) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = source == null ? "添加环境变量" : "编辑环境变量", FontSize = 22, FontWeight = FontWeights.SemiBold });
            var hint = Ui.Text(local ? "修改变量值时需要确认预览。" : "保存到当前配置组，启用或更新后应用变量值。", 12, "#839187"); hint.Margin = new Thickness(0, 10, 0, 22); heading.Children.Add(hint); root.Children.Add(heading);
            var form = new StackPanel();
            var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            form.Children.Add(Ui.Text("变量名称", 12, "#77877B")); var name = new TextBox { Text = e.Name, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 7, 0, 18), IsReadOnly = local && source != null }; form.Children.Add(name);
            var pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition()); form.Children.Add(pair);
            var scopePanel = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; scopePanel.Children.Add(Ui.Text("作用范围", 12, "#77877B"));
            var scope = new ComboBox { ItemsSource = new[] { "用户变量 · 当前账号", "系统变量 · 所有账号" }, SelectedIndex = e.Scope, IsEnabled = !(local && source != null), Margin = new Thickness(0, 7, 0, 0) }; scopePanel.Children.Add(scope); pair.Children.Add(scopePanel);
            var modePanel = new StackPanel { Margin = new Thickness(10, 0, 0, 0) }; Grid.SetColumn(modePanel, 1); modePanel.Children.Add(Ui.Text("应用方式", 12, "#77877B"));
            var modes = local ? new[] { "Set", "Delete" } : new[] { "Set", "Prepend", "Append", "Delete" };
            var mode = new ComboBox { ItemsSource = modes.Select(Ui.Mode).ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(modes, e.Mode)), Margin = new Thickness(0, 7, 0, 0) }; modePanel.Children.Add(mode); pair.Children.Add(modePanel);
            var valueHead = new Grid { Margin = new Thickness(0, 20, 0, 7) }; valueHead.Children.Add(Ui.Text("变量值", 12, "#77877B"));
            var path = Ui.Button("逐项编辑列表 ↗", false); path.Style = (Style)Application.Current.FindResource("QuietButton"); path.Padding = new Thickness(0); path.FontSize = 11; path.HorizontalAlignment = HorizontalAlignment.Right; path.ToolTip = "PATH 的前置和追加会按顺序合并目录并去重。"; valueHead.Children.Add(path); form.Children.Add(valueHead);
            var value = new TextBox { Text = e.Value, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), Height = 100, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top }; form.Children.Add(value);
            var expand = new CheckBox { Content = "展开 %变量名% 引用（REG_EXPAND_SZ）", IsChecked = e.Kind == 2, Margin = new Thickness(0, 15, 0, 0), Foreground = Ui.Color("#718378"), FontSize = 12 }; form.Children.Add(expand);
            var description = AddDescriptionField(form, e.Description, new Thickness(0, 16, 0, 0));
            mode.SelectionChanged += delegate { bool enabled = modes[mode.SelectedIndex] != "Delete"; value.IsEnabled = enabled; path.IsEnabled = enabled; expand.IsEnabled = enabled; };
            path.Click += delegate { var result = PathEditor(w, value.Text); if (result != null) value.Text = result; };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) }; Grid.SetRow(buttons, 2); root.Children.Add(buttons);
            var cancel = Ui.Button("取消", false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0); buttons.Children.Add(cancel);
            var save = Ui.Button("保存变量", true); save.Name = "SaveVariable"; save.Click += delegate
            {
                try
                {
                    e.Name = local && source != null ? source.Name : name.Text.Trim();
                    e.Value = value.Text;
                    e.Scope = scope.SelectedIndex;
                    e.Kind = expand.IsChecked == true ? 2 : 1;
                    e.Mode = modes[mode.SelectedIndex];
                    e.Description = ReadDescription(description);
                    if (!local || !e.SameOperation(source)) Planner.Validate(e);
                    accepted(e);
                    w.DialogResult = true;
                }
                catch (Exception ex) { Error(w, ex); }
            }; buttons.Children.Add(save); w.Content = root;
            w.Loaded += delegate { if (source == null) name.Focus(); else value.Focus(); };
            if (e.Mode == "Delete") { value.IsEnabled = false; path.IsEnabled = false; expand.IsEnabled = false; }
            return w;
        }
        public static Entry EntryEditor(Window owner, Entry source, bool local)
        { Entry result = null; var window = BuildEntryWindow(owner, source, local, e => result = e); window.ShowDialog(); return result; }

        static TextBox AddDescriptionField(Panel form, string initial, Thickness margin)
        {
            var label = Ui.Text("描述（选填，仅 SwitchEnv 内可见）", 12, "#77877B");
            label.Margin = margin;
            form.Children.Add(label);
            var input = new TextBox { Name = "VariableDescription", Text = initial ?? "", Height = 64, Margin = new Thickness(0, 7, 0, 0),
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(input, "变量描述");
            form.Children.Add(input);
            return input;
        }

        static string ReadDescription(TextBox input)
        {
            return String.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
        }

        public static Window BuildDescriptionWindow(Window owner, Variable variable, string initial, Action<string> accepted)
        {
            var w = Ui.Dialog(owner, "编辑本机变量描述", 530, 290);
            var form = new StackPanel { Margin = new Thickness(26) };
            form.Children.Add(Ui.Text(variable.Name + " · " + Ui.Scope(variable.Scope), 18, "#334B3E"));
            var description = AddDescriptionField(form, initial, new Thickness(0, 17, 0, 0));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 17, 0, 0) };
            var cancel = Ui.Button("取消", false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 10, 0);
            buttons.Children.Add(cancel);
            var save = Ui.Button("保存描述", true);
            save.Name = "SaveVariableDescription";
            save.Click += delegate { accepted(ReadDescription(description)); w.DialogResult = true; };
            buttons.Children.Add(save);
            form.Children.Add(buttons);
            w.Content = form;
            w.Loaded += delegate { description.Focus(); };
            return w;
        }

        public static string PathEditor(Window owner, string initial)
        {
            var window = BuildPathWindow(owner, initial); return window.ShowDialog() == true ? (string)window.Tag : null;
        }
        public static Window BuildPathWindow(Window owner, string initial)
        {
            var w = Ui.Dialog(owner, "列表编辑器", 760, 650); var root = new Grid { Margin = new Thickness(27) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = "逐项整理 PATH", FontSize = 22, FontWeight = FontWeights.SemiBold }); var sub = Ui.Text("每项一个目录，靠前的目录优先查找。%变量名% 会原样保留。", 12, "#829087"); sub.Margin = new Thickness(0, 10, 0, 20); heading.Children.Add(sub); root.Children.Add(heading);
            var paths = new ObservableCollection<string>(initial.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0));
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); Grid.SetRow(grid, 1); root.Children.Add(grid);
            var list = new ListBox { ItemsSource = paths, Background = Brushes.White, BorderBrush = Ui.Color("#DFE6E3"), BorderThickness = new Thickness(1), FontFamily = new FontFamily("Consolas"), Padding = new Thickness(10), FontSize = 13 };
            var factory = new FrameworkElementFactory(typeof(TextBlock)); factory.SetBinding(TextBlock.TextProperty, new Binding(".")); factory.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 8, 10, 8)); factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); list.ItemTemplate = new DataTemplate { VisualTree = factory }; grid.Children.Add(list);
            var tools = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; Grid.SetColumn(tools, 1); grid.Children.Add(tools);
            var up = Ui.Button("↑  上移", false); up.Margin = new Thickness(0, 0, 0, 10); tools.Children.Add(up); var down = Ui.Button("↓  下移", false); down.Margin = new Thickness(0, 0, 0, 10); tools.Children.Add(down);
            var remove = Ui.Button("移除", false); remove.Margin = new Thickness(0, 0, 0, 20); tools.Children.Add(remove); var dedupe = Ui.Button("保序去重", false); dedupe.FontSize = 11; dedupe.Padding = new Thickness(8, 9, 8, 9); tools.Children.Add(dedupe);
            Action<int> move = delta => { int index = list.SelectedIndex; if (index >= 0 && index + delta >= 0 && index + delta < paths.Count) { var p = paths[index]; paths.RemoveAt(index); paths.Insert(index + delta, p); list.SelectedIndex = index + delta; } };
            up.Click += delegate { move(-1); }; down.Click += delegate { move(1); }; remove.Click += delegate { if (list.SelectedIndex >= 0) paths.RemoveAt(list.SelectedIndex); };
            dedupe.Click += delegate { var unique = Planner.MergePath("", String.Join(";", paths), false).Split(';').Where(p => p.Length > 0).ToList(); paths.Clear(); foreach (var p in unique) paths.Add(p); };
            var editor = new Grid { Margin = new Thickness(0, 18, 0, 0) }; editor.ColumnDefinitions.Add(new ColumnDefinition()); editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetRow(editor, 2); root.Children.Add(editor);
            var text = new TextBox { FontFamily = new FontFamily("Consolas"), ToolTip = "输入一个目录或 %变量名% 引用" }; editor.Children.Add(text);
            var add = Ui.Button("添加", false); add.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(add, 1); editor.Children.Add(add);
            var update = Ui.Button("更新", false); update.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(update, 2); editor.Children.Add(update);
            list.SelectionChanged += delegate { text.Text = list.SelectedItem as string ?? ""; };
            add.Click += delegate { if (!String.IsNullOrWhiteSpace(text.Text)) { foreach (var p in text.Text.Split(';').Select(v => v.Trim()).Where(v => v.Length > 0)) paths.Add(p); list.SelectedIndex = paths.Count - 1; } };
            update.Click += delegate { int i = list.SelectedIndex; if (i >= 0 && !String.IsNullOrWhiteSpace(text.Text) && !text.Text.Contains(";")) { var edited = text.Text.Trim(); paths[i] = edited; list.SelectedIndex = i; } };
            var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetRow(footer, 3); root.Children.Add(footer);
            var hint = Ui.Text("调整仅保存到编辑器，确认配置组后才会影响系统。", 11, "#8E9E93"); hint.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(hint);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(buttons, 1); footer.Children.Add(buttons); var cancel = Ui.Button("取消", false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 10, 0); buttons.Children.Add(cancel); var done = Ui.Button("完成", true); done.Click += delegate { w.Tag = String.Join(";", paths); w.DialogResult = true; }; buttons.Children.Add(done); w.Content = root; return w;
        }

        class PreviewRow
        {
            public string Location { get; set; }
            public string Name { get; set; }
            public string Scope { get; set; }
            public string Before { get; set; }
            public string After { get; set; }
        }
        static string EntryText(Entry entry, bool reveal)
        {
            if (entry == null) return "（不在配置组中）";
            string value = entry.Mode == "Delete" ? "（启用时删除变量）" : !reveal && Ui.Secret(entry.Name, entry.Value) ? "••••••••••••" : entry.Value == "" ? "（空字符串）" : entry.Value;
            string details = Ui.Mode(entry.Mode) + " · " + (entry.Kind == 2 ? "可展开" : "字符串") + " · " + value;
            return String.IsNullOrEmpty(entry.Description) ? details : details + "\n描述：" + entry.Description;
        }
        public static Window BuildPreviewWindow(Window owner, Plan plan, bool readOnly, bool demo)
        {
            var w = Ui.Dialog(owner, readOnly ? "修改记录" : "应用预览", 960, 660); var root = new Grid { Margin = new Thickness(27) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(137) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new StackPanel(); header.Children.Add(new TextBlock { Text = plan.Label, FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            string summary = plan.Changes.Count + " 项本机变量值修改";
            if (plan.ProfileChanges.Count > 0) summary += " · " + plan.ProfileChanges.Count + " 项配置组修改";
            if (plan.DescriptionChanges.Count > 0) summary += " · " + plan.DescriptionChanges.Count + " 项本机描述修改";
            var hint = Ui.Text(summary + " · 自动备份修改前的状态" + (demo ? " · 演示模式" : ""), 12, "#829388"); hint.Margin = new Thickness(0, 10, 0, 20); header.Children.Add(hint); if (!String.IsNullOrEmpty(plan.CompositionNote)) { var composition = Ui.Text(plan.CompositionNote, 12, "#567862"); composition.TextWrapping = TextWrapping.Wrap; composition.MaxHeight = 105; composition.Margin = new Thickness(0, 0, 0, 14); composition.ToolTip = plan.CompositionNote; header.Children.Add(composition); } root.Children.Add(header);
            var table = new DataGrid(); table.Columns.Add(new DataGridTextColumn { Header = "位置", Binding = new Binding("Location"), Width = 155 }); table.Columns.Add(new DataGridTextColumn { Header = "范围", Binding = new Binding("Scope"), Width = 65 }); table.Columns.Add(new DataGridTextColumn { Header = "变量", Binding = new Binding("Name"), Width = 150 }); table.Columns.Add(new DataGridTextColumn { Header = "修改前", Binding = new Binding("Before"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); table.Columns.Add(new DataGridTextColumn { Header = "修改后", Binding = new Binding("After"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); Grid.SetRow(table, 1); root.Children.Add(table);
            var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12, Background = Ui.Color("#F0F5F1"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 16, 0, 0), Text = "选择一个变量查看完整值。" }; Grid.SetRow(details, 2); root.Children.Add(details);
            var footer = new Grid { Margin = new Thickness(0, 22, 0, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetRow(footer, 3); root.Children.Add(footer);
            var reveal = new CheckBox { Content = "显示敏感值", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Color("#7A8F7F"), FontSize = 12 }; footer.Children.Add(reveal);
            Action detail = () => {
                var row = table.SelectedItem as PreviewRow;
                if (row != null) details.Text = row.Location + " · " + row.Name + " · " + row.Scope + "\n\n修改前：" + row.Before + "\n\n修改后：" + row.After;
            };
            Action load = () =>
            {
                var rows = plan.Changes.Select(c => new PreviewRow { Location = "本机环境", Name = c.After.Name, Scope = Ui.Scope(c.After.Scope), Before = Ui.ValueText(c.Before, reveal.IsChecked == true), After = Ui.ValueText(c.After, reveal.IsChecked == true) }).ToList();
                rows.AddRange(plan.ProfileChanges.Select(c => new PreviewRow { Location = "配置组 · " + c.ProfileName, Name = (c.After ?? c.Before).Name, Scope = Ui.Scope((c.After ?? c.Before).Scope), Before = EntryText(c.Before, reveal.IsChecked == true), After = EntryText(c.After, reveal.IsChecked == true) }));
                rows.AddRange(plan.DescriptionChanges.Select(c => new PreviewRow {
                    Location = "本机描述", Name = c.Name, Scope = Ui.Scope(c.Scope),
                    Before = c.Before ?? "（未填写）", After = c.After ?? "（未填写）"
                }));
                table.ItemsSource = rows; table.SelectedIndex = rows.Count > 0 ? 0 : -1;
                if (rows.Count == 0) details.Text = "变量值无需修改，将更新当前配置组的启用状态。"; detail();
            };
            table.SelectionChanged += delegate { detail(); }; reveal.Checked += delegate { load(); }; reveal.Unchecked += delegate { load(); }; load();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(buttons, 1); footer.Children.Add(buttons);
            var cancel = Ui.Button(readOnly ? "关闭" : "取消", false); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, readOnly ? 0 : 10, 0); buttons.Children.Add(cancel);
            if (!readOnly) { var apply = Ui.Button(demo ? "确认（演示）" : "确认应用", true); apply.Click += delegate { w.DialogResult = true; }; buttons.Children.Add(apply); }
            w.Content = root; return w;
        }
        public static bool Preview(Window owner, Plan plan, bool demo) { return BuildPreviewWindow(owner, plan, false, demo).ShowDialog() == true; }
    }
}
