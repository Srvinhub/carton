using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace carton.GUI.Resources.Localization;

public partial class Strings_en : ResourceDictionary
{
    public Strings_en()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
