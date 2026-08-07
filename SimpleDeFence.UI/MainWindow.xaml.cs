using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using SimpleDeFence.UI.Pages;

namespace SimpleDeFence.UI
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Title = "SimpleDeFence";
            ContentFrame.Navigate(typeof(StatusPage));
        }

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item)
                return;

            var page = item.Tag switch
            {
                "applications" => typeof(ApplicationsPage),
                _ => typeof(StatusPage),
            };

            if (ContentFrame.CurrentSourcePageType != page)
                ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
        }
    }
}
