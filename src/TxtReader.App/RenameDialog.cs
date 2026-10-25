using System.Windows;
using System.Windows.Controls;

namespace TxtReader.App;

internal sealed class RenameDialog : Window
{
    private readonly TextBox _name;
    public string BookName => _name.Text.Trim();

    public RenameDialog(Window owner, string title)
    {
        Owner = owner;
        Title = "修改书名";
        Width = 440; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "书架中显示的书名", Margin = new Thickness(0, 0, 0, 12) });
        _name = new TextBox { Text = title, MaxLength = 120, MinWidth = 340 };
        panel.Children.Add(_name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var save = new Button { Content = "保存", IsDefault = true, Margin = new Thickness(12, 0, 0, 0) };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(BookName)) DialogResult = true; };
        _name.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(BookName);
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = panel;
        ContentRendered += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}
