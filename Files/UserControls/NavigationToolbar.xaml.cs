using Files.DataModels;
using Files.Helpers;
using Files.Helpers.ContextFlyouts;
using Files.Helpers.XamlHelpers;
using Files.ViewModels;
using Files.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;

namespace Files.UserControls
{
    public sealed partial class NavigationToolbar : UserControl
    {
        public NavToolbarViewModel ViewModel
        {
            get => (NavToolbarViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        // Using a DependencyProperty as the backing store for ViewModel.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register(nameof(ViewModel), typeof(NavToolbarViewModel), typeof(NavigationToolbar), new PropertyMetadata(null));

        public ISearchBox SearchBox => ViewModel.SearchBox;

        public MainViewModel MainViewModel => App.MainViewModel;


        public static readonly DependencyProperty CanPasteInPageProperty = DependencyProperty.Register(
          "CanPasteInPage",
          typeof(bool),
          typeof(NavigationToolbar),
          new PropertyMetadata(null)
        );

        public bool CanPasteInPage
        {
            get
            {
                return (bool)GetValue(CanPasteInPageProperty);
            }
            set
            {
                SetValue(CanPasteInPageProperty, value);
            }
        }



        public bool ShowPreviewPaneButton
        {
            get { return (bool)GetValue(ShowPreviewPaneButtonProperty); }
            set { SetValue(ShowPreviewPaneButtonProperty, value); }
        }

        // Using a DependencyProperty as the backing store for ShowPreviewPaneButton.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ShowPreviewPaneButtonProperty =
            DependencyProperty.Register("ShowPreviewPaneButton", typeof(bool), typeof(NavigationToolbar), new PropertyMetadata(null));

        public SettingsViewModel AppSettings => App.AppSettings;

        private List<ShellNewEntry> cachedNewContextMenuEntries { get; set; }


        public NavigationToolbar()
        {
            InitializeComponent();
            Loading += NavigationToolbar_Loading;
        }

        private async void NavigationToolbar_Loading(FrameworkElement sender, object args)
        {
            cachedNewContextMenuEntries = await RegistryHelper.GetNewContextMenuEntries();
        }


        public bool ShowMultiPaneControls
        {
            get => (bool)GetValue(ShowMultiPaneControlsProperty);
            set => SetValue(ShowMultiPaneControlsProperty, value);
        }

        // Using a DependencyProperty as the backing store for ShowMultiPaneControls.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty ShowMultiPaneControlsProperty =
            DependencyProperty.Register(nameof(ShowMultiPaneControls), typeof(bool), typeof(NavigationToolbar), new PropertyMetadata(null));


        public bool IsMultiPaneActive
        {
            get { return (bool)GetValue(IsMultiPaneActiveProperty); }
            set { SetValue(IsMultiPaneActiveProperty, value); }
        }

        // Using a DependencyProperty as the backing store for IsMultiPaneActive.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty IsMultiPaneActiveProperty =
            DependencyProperty.Register("IsMultiPaneActive", typeof(bool), typeof(NavigationToolbar), new PropertyMetadata(false));


        private void VisiblePath_Loaded(object sender, RoutedEventArgs e)
        {
            // AutoSuggestBox won't receive focus unless it's fully loaded
            VisiblePath.Focus(FocusState.Programmatic);
            DependencyObjectHelpers.FindChild<TextBox>(VisiblePath)?.SelectAll();
        }

        private void ManualPathEntryItem_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsEditModeEnabled = true;
        }

        private void VisiblePath_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                ViewModel.IsEditModeEnabled = false;
            }
        }

        private void VisiblePath_LostFocus(object sender, RoutedEventArgs e)
        {
            if (FocusManager.GetFocusedElement() is FlyoutBase ||
                FocusManager.GetFocusedElement() is AppBarButton ||
                FocusManager.GetFocusedElement() is Popup)
            {
                return;
            }

            var element = FocusManager.GetFocusedElement();
            var elementAsControl = element as Control;
            if (elementAsControl == null)
            {
                return;
            }
            else if (elementAsControl.FocusState != FocusState.Programmatic && elementAsControl.FocusState != FocusState.Keyboard)
            {
                ViewModel.IsEditModeEnabled = false;
            }
            else
            {
                if (ViewModel.IsEditModeEnabled)
                {
                    this.VisiblePath.Focus(FocusState.Programmatic);
                }
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e) => ViewModel.SwitchSearchBoxVisibility();

        private void SearchBox_Escaped(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => ViewModel.CloseSearchBox();

        private void SearchRegion_LostFocus(object sender, RoutedEventArgs e) => ViewModel.SearchRegion_LostFocus(sender, e);

        private void NavMoreButtonFlyout_Opening(object sender, object e)
        {
            if (cachedNewContextMenuEntries == null)
            {
                return;
            }
            if (!NewEmptySpace.Items.Any(x => (x.Tag as string) == "CreateNewFile"))
            {
                var separatorIndex = NewEmptySpace.Items.IndexOf(NewEmptySpace.Items.Single(x => x.Name == "NewMenuFileFolderSeparator"));
                foreach (var newEntry in Enumerable.Reverse(cachedNewContextMenuEntries))
                {
                    MenuFlyoutItem menuLayoutItem;
                    if (newEntry.Icon != null)
                    {
                        BitmapImage image = null;
                        image = new BitmapImage();
#pragma warning disable CS4014
                        image.SetSourceAsync(newEntry.Icon);
#pragma warning restore CS4014
                        menuLayoutItem = new MenuFlyoutItemWithImage()
                        {
                            Text = newEntry.Name,
                            BitmapIcon = image,
                            Tag = "CreateNewFile"
                        };
                    }
                    else
                    {
                        menuLayoutItem = new MenuFlyoutItem()
                        {
                            Text = newEntry.Name,
                            Icon = new FontIcon()
                            {
                                Glyph = "\xE7C3"
                            },
                            Tag = "CreateNewFile"
                        };
                    }
                    menuLayoutItem.Command = ViewModel.CreateNewFileCommand;
                    menuLayoutItem.CommandParameter = newEntry;
                    NewEmptySpace.Items.Insert(separatorIndex + 1, menuLayoutItem);
                }
            }
        }

        private void VisiblePath_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => ViewModel.VisiblePath_QuerySubmitted(sender, args);



        public bool IsCompactOverlay
        {
            get { return (bool)GetValue(IsCompactOverlayProperty); }
            set { SetValue(IsCompactOverlayProperty, value); }
        }

        // Using a DependencyProperty as the backing store for IsCompactOverlay.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty IsCompactOverlayProperty =
            DependencyProperty.Register("IsCompactOverlay", typeof(bool), typeof(NavigationToolbar), new PropertyMetadata(null));



        public ICommand SetCompactOverlayCommand
        {
            get { return (ICommand)GetValue(SetCompactOverlayCommandProperty); }
            set { SetValue(SetCompactOverlayCommandProperty, value); }
        }

        // Using a DependencyProperty as the backing store for ToggleCompactOverlayCommand.  This enables animation, styling, binding, etc...
        public static readonly DependencyProperty SetCompactOverlayCommandProperty =
            DependencyProperty.Register("ToggleCompactOverlayCommand", typeof(ICommand), typeof(NavigationToolbar), new PropertyMetadata(null));



        private async void NavToolbarEnterCompactOverlay_Click(object sender, RoutedEventArgs e)
        {
            var view = ApplicationView.GetForCurrentView();
            if (view.ViewMode == ApplicationViewMode.CompactOverlay)
            {
                await view.TryEnterViewModeAsync(ApplicationViewMode.Default);
                NavToolbarExitCompactOverlay.Visibility = Visibility.Collapsed;
                NavToolbarEnterCompactOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                await view.TryEnterViewModeAsync(ApplicationViewMode.CompactOverlay);
                NavToolbarExitCompactOverlay.Visibility = Visibility.Visible;
                NavToolbarEnterCompactOverlay.Visibility = Visibility.Collapsed;
            }
        }

        public void SetCommandBarContextItems(List<ContextMenuFlyoutItemViewModel> items)
        {
            // Clear out all old context items
            /*for (int i = 0; i < ; i++)
            {
                
            }*/

            var i = ContextCommandBar.PrimaryCommands.IndexOf(CurrentItemOptionSeparator);
            while (i > 0)
            {
                ContextCommandBar.PrimaryCommands.RemoveAt(i-1);
                i = ContextCommandBar.PrimaryCommands.IndexOf(CurrentItemOptionSeparator);
            }

            if(items is null || !items.Any())
            {
                return;
            }

            (var primaryAppBarItems, var secondaryAppBarItems) = ItemModelListToContextFlyoutHelper.GetAppBarItemsFromModel(items);

            //foreach (var item in primaryAppBarItems)
            //{
            //    SetAppBarButtonProps(item);
            //    var index = ContextCommandBar.PrimaryCommands.IndexOf(CurrentItemOptionSeparator) - 1;
            //    if(index < 0)
            //    {
            //        index = 0;
            //    }
            //    ContextCommandBar.PrimaryCommands.Insert(index, item);
            //}

            foreach (var item in secondaryAppBarItems)
            {
                SetAppBarButtonProps(item);
                var index = ContextCommandBar.PrimaryCommands.IndexOf(CurrentItemOptionSeparator);
                //if (index < 0)
                //{
                //    index = 0;
                //}
                ContextCommandBar.PrimaryCommands.Insert(index, item);
            }
        }

        private void SetAppBarButtonProps(ICommandBarElement e)
        {
            if (e is AppBarButton appBarButton)
            {
                if (appBarButton.Icon is FontIcon bFontIcon)
                {
                    bFontIcon.Style = Resources["AccentColorFontIconStyle"] as Style;
                }
                if(appBarButton.LabelPosition == CommandBarLabelPosition.Collapsed)
                {
                    appBarButton.Width = 48;
                }
            }
            else if (e is AppBarToggleButton appBarToggleButton)
            {
                if (appBarToggleButton.Icon is FontIcon tFontIcon)
                {
                    tFontIcon.Style = Resources["AccentColorFontIconStyle"] as Style;
                }

                if (appBarToggleButton.LabelPosition == CommandBarLabelPosition.Collapsed)
                {
                    appBarToggleButton.Width = 48;
                }
            }
        }
        
        public void SetShellCommandBarContextItems()
        {

        }
    }
}