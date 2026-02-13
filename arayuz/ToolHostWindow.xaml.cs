using System.Windows;
using Dragablz;

namespace arayuz_deneme_1
{
    public partial class ToolHostWindow : Window
    {
        public ToolHostWindow()
        {
            InitializeComponent();
        }

        public TabablzControl Tabs => ToolTabs;
    }
}
