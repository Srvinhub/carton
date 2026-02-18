using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace carton.GUI.Resources.Localization;

public partial class Strings_zh_Hans : ResourceDictionary
{
    public Strings_zh_Hans()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
