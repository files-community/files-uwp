using Files.DataModels.NavigationControlItems;
using Files.Filesystem;
using Files.Filesystem.Permissions;
using Files.Helpers;
using Files.ViewModels.Properties;
using Microsoft.Toolkit.Uwp;
using System;
using System.Linq;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.Resources.Core;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Navigation;

// Il modello di elemento Pagina vuota è documentato all'indirizzo https://go.microsoft.com/fwlink/?LinkId=234238

namespace Files.Views
{
    /// <summary>
    /// Pagina vuota che può essere usata autonomamente oppure per l'esplorazione all'interno di un frame.
    /// </summary>
    public sealed partial class PropertiesSecurityAdvanced : Page
    {
        private static ApplicationViewTitleBar TitleBar;

        private object navParameterItem;

        public string DialogTitle => string.Format("SecurityAdvancedPermissionsTitle".GetLocalized(), ViewModel.Item.ItemName);

        public SecurityProperties ViewModel { get; set; }

        public PropertiesSecurityAdvanced()
        {
            this.InitializeComponent();

            var flowDirectionSetting = ResourceContext.GetForCurrentView().QualifierValues["LayoutDirection"];

            if (flowDirectionSetting == "RTL")
            {
                FlowDirection = FlowDirection.RightToLeft;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            var args = e.Parameter as PropertiesPageNavigationArguments;
            navParameterItem = args.Item;

            if (args.Item is ListedItem listedItem)
            {
                ViewModel = new SecurityProperties(listedItem);
            }
            else if (args.Item is DriveItem driveitem)
            {
                ViewModel = new SecurityProperties(driveitem);
            }

            base.OnNavigatedTo(e);
        }

        private async void Properties_Loaded(object sender, RoutedEventArgs e)
        {
            App.AppSettings.ThemeModeChanged += AppSettings_ThemeModeChanged;
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
            {
                // Set window size in the loaded event to prevent flickering
                ApplicationView.GetForCurrentView().Consolidated += Properties_Consolidated;
                TitleBar = ApplicationView.GetForCurrentView().TitleBar;
                TitleBar.ButtonBackgroundColor = Colors.Transparent;
                TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                await CoreApplication.MainView.DispatcherQueue.EnqueueAsync(() => App.AppSettings.UpdateThemeElements.Execute(null));
            }
            else
            {
            }

            ViewModel.GetFilePermissions();
        }

        private void Properties_Consolidated(ApplicationView sender, ApplicationViewConsolidatedEventArgs args)
        {
            App.AppSettings.ThemeModeChanged -= AppSettings_ThemeModeChanged;
            ApplicationView.GetForCurrentView().Consolidated -= Properties_Consolidated;
        }

        private void Properties_Unloaded(object sender, RoutedEventArgs e)
        {
            // Why is this not called? Are we cleaning up properly?
        }

        private async void AppSettings_ThemeModeChanged(object sender, EventArgs e)
        {
            var selectedTheme = ThemeHelper.RootTheme;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RequestedTheme = selectedTheme;
                if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
                {
                    switch (RequestedTheme)
                    {
                        case ElementTheme.Default:
                            TitleBar.ButtonHoverBackgroundColor = (Color)Application.Current.Resources["SystemBaseLowColor"];
                            TitleBar.ButtonForegroundColor = (Color)Application.Current.Resources["SystemBaseHighColor"];
                            break;

                        case ElementTheme.Light:
                            TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(51, 0, 0, 0);
                            TitleBar.ButtonForegroundColor = Colors.Black;
                            break;

                        case ElementTheme.Dark:
                            TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(51, 255, 255, 255);
                            TitleBar.ButtonForegroundColor = Colors.White;
                            break;
                    }
                }
            });
        }

        private async void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
            {
                if (await ViewModel.SetFilePermissions())
                {
                    await ApplicationView.GetForCurrentView().TryConsolidateAsync();
                }
            }
            else
            {
            }
        }

        private async void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
            {
                await ApplicationView.GetForCurrentView().TryConsolidateAsync();
            }
            else
            {
            }
        }

        private async void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key.Equals(VirtualKey.Escape))
            {
                if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
                {
                    await ApplicationView.GetForCurrentView().TryConsolidateAsync();
                }
                else
                {
                }
            }
        }

        private void Page_Loading(FrameworkElement sender, object args)
        {
            // This manually adds the user's theme resources to the page
            // I was unable to get this to work any other way
            try
            {
                var xaml = XamlReader.Load(App.ExternalResourcesHelper.CurrentThemeResources) as ResourceDictionary;
                App.Current.Resources.MergedDictionaries.Add(xaml);
            }
            catch (Exception)
            {
            }
        }

        public class PropertiesPageNavigationArguments
        {
            public object Item { get; set; }
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedAccessRules = (sender as ListView).SelectedItems.Cast<FileSystemAccessRuleForUI>().ToList();

            if (e.AddedItems != null)
            {
                foreach (var item in e.AddedItems)
                {
                    (item as FileSystemAccessRuleForUI).IsSelected = true;
                }
            }
            if (e.RemovedItems != null)
            {
                foreach (var item in e.RemovedItems)
                {
                    (item as FileSystemAccessRuleForUI).IsSelected = false;
                }
            }
        }
    }
}
