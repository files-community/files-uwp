using Files.Filesystem;
using Files.Helpers;
using Files.Helpers.XamlHelpers;
using Files.UserControls;
using Files.Views;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp;
using Microsoft.Toolkit.Uwp.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.System;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Files.Common;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using static Files.UserControls.INavigationToolbar;
using SearchBox = Files.UserControls.SearchBox;

namespace Files.ViewModels
{
    public class NavToolbarViewModel : ObservableObject, INavigationToolbar, IDisposable
    {
        public delegate void ToolbarPathItemInvokedEventHandler(object sender, PathNavigationEventArgs e);

        public delegate void ToolbarFlyoutOpenedEventHandler(object sender, ToolbarFlyoutOpenedEventArgs e);

        public delegate void ToolbarPathItemLoadedEventHandler(object sender, ToolbarPathItemLoadedEventArgs e);

        public delegate void AddressBarTextEnteredEventHandler(object sender, AddressBarTextEnteredEventArgs e);

        public delegate void PathBoxItemDroppedEventHandler(object sender, PathBoxItemDroppedEventArgs e);

        public event ToolbarPathItemInvokedEventHandler ToolbarPathItemInvoked;

        public event ToolbarFlyoutOpenedEventHandler ToolbarFlyoutOpened;

        public event ToolbarPathItemLoadedEventHandler ToolbarPathItemLoaded;

        public event ItemDraggedOverPathItemEventHandler ItemDraggedOverPathItem;

        public event EventHandler EditModeEnabled;

        public event ToolbarQuerySubmittedEventHandler PathBoxQuerySubmitted;

        public event AddressBarTextEnteredEventHandler AddressBarTextEntered;

        public event PathBoxItemDroppedEventHandler PathBoxItemDropped;

        public event EventHandler BackRequested;

        public event EventHandler ForwardRequested;

        public event EventHandler UpRequested;

        public event EventHandler RefreshRequested;

        public event EventHandler RefreshWidgetsRequested;

        public ObservableCollection<PathBoxItem> PathComponents { get; } = new ObservableCollection<PathBoxItem>();


        private bool canCopyPathInPage;
        public bool CanCopyPathInPage
        {
            get => canCopyPathInPage;
            set => SetProperty(ref canCopyPathInPage, value);
        }

        private bool canGoBack;
        public bool CanGoBack
        {
            get => canGoBack;
            set => SetProperty(ref canGoBack, value);
        }


        private bool canGoForward;
        public bool CanGoForward
        {
            get => canGoForward;
            set => SetProperty(ref canGoForward, value);
        }


        private bool canNavigateToParent;
        public bool CanNavigateToParent
        {
            get => canNavigateToParent;
            set => SetProperty(ref canNavigateToParent, value);
        }


        private bool previewPaneEnabled;
        public bool PreviewPaneEnabled
        {
            get => previewPaneEnabled;
            set => SetProperty(ref previewPaneEnabled, value);
        }

        private bool canRefresh;
        public bool CanRefresh
        {
            get => canRefresh;
            set => SetProperty(ref canRefresh, value);
        }


        private string searchButtonGlyph = "\uE721";
        public string SearchButtonGlyph
        {
            get => searchButtonGlyph;
            set => SetProperty(ref searchButtonGlyph, value);
        }


        private bool isSearchBoxVisible;
        public bool IsSearchBoxVisible
        {
            get => isSearchBoxVisible;
            set
            {
                if (SetProperty(ref isSearchBoxVisible, value))
                {
                    SearchButtonGlyph = value ? "\uE711" : "\uE721";
                }
            }
        }


        private string pathText;
        public string PathText
        {
            get => pathText;
            set => SetProperty(ref pathText, value);
        }

        public ObservableCollection<ListedItem> NavigationBarSuggestions = new ObservableCollection<ListedItem>();


        private CurrentInstanceViewModel instanceViewModel;
        public CurrentInstanceViewModel InstanceViewModel
        {
            get => instanceViewModel;
            set => SetProperty(ref instanceViewModel, value);
        }

        public NavToolbarViewModel()
        {
            dragOverTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            SearchBox.SuggestionChosen += SearchRegion_SuggestionChosen;
            SearchBox.Escaped += SearchRegion_Escaped;
        }

        private DispatcherQueueTimer dragOverTimer;

        private ISearchBox searchBox = new SearchBoxViewModel();
        public ISearchBox SearchBox
        {
            get => searchBox;
            set => SetProperty(ref searchBox, value);
        }
        public SearchBoxViewModel SearchBoxViewModel => SearchBox as SearchBoxViewModel;

        public bool IsSingleItemOverride { get; set; } = false;


        private string dragOverPath = null;

        public void PathBoxItem_DragLeave(object sender, DragEventArgs e)
        {
            if (!((sender as Grid).DataContext is PathBoxItem pathBoxItem) ||
                pathBoxItem.Path == "Home" || pathBoxItem.Path == "NewTab".GetLocalized())
            {
                return;
            }

            if (pathBoxItem.Path == dragOverPath)
            {
                // Reset dragged over pathbox item
                dragOverPath = null;
            }
        }

        public void PathBoxItem_Drop(object sender, DragEventArgs e)
        {
            dragOverPath = null; // Reset dragged over pathbox item

            if (!((sender as Grid).DataContext is PathBoxItem pathBoxItem) ||
                pathBoxItem.Path == "Home" || pathBoxItem.Path == "NewTab".GetLocalized())
            {
                return;
            }

            var deferral = e.GetDeferral();
            PathBoxItemDropped?.Invoke(this, new PathBoxItemDroppedEventArgs()
            {
                AcceptedOperation = e.AcceptedOperation,
                Package = e.DataView,
                Path = pathBoxItem.Path
            });
            deferral.Complete();
        }


        public async void PathBoxItem_DragOver(object sender, DragEventArgs e)
        {
            if (IsSingleItemOverride || !((sender as Grid).DataContext is PathBoxItem pathBoxItem) ||
                pathBoxItem.Path == "Home" || pathBoxItem.Path == "NewTab".GetLocalized())
            {
                return;
            }

            if (dragOverPath != pathBoxItem.Path)
            {
                dragOverPath = pathBoxItem.Path;
                dragOverTimer.Stop();
                if (dragOverPath != (this as INavigationToolbar).PathComponents.LastOrDefault()?.Path)
                {
                    dragOverTimer.Debounce(() =>
                    {
                        if (dragOverPath != null)
                        {
                            dragOverTimer.Stop();
                            ItemDraggedOverPathItem?.Invoke(this, new PathNavigationEventArgs()
                            {
                                ItemPath = dragOverPath
                            });
                            dragOverPath = null;
                        }
                    }, TimeSpan.FromMilliseconds(1000), false);
                }
            }

            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            e.Handled = true;
            var deferral = e.GetDeferral();

            IReadOnlyList<IStorageItem> storageItems;
            try
            {
                storageItems = await e.DataView.GetStorageItemsAsync();
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80040064 || (uint)ex.HResult == 0x8004006A)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                deferral.Complete();
                return;
            }
            catch (Exception ex)
            {
                App.Logger.Warn(ex, ex.Message);
                e.AcceptedOperation = DataPackageOperation.None;
                deferral.Complete();
                return;
            }

            if (!storageItems.Any(storageItem =>
                storageItem.Path.Replace(pathBoxItem.Path, string.Empty).
                Trim(Path.DirectorySeparatorChar).
                Contains(Path.DirectorySeparatorChar)))
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            else
            {
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = string.Format("MoveToFolderCaptionText".GetLocalized(), pathBoxItem.Title);
                e.AcceptedOperation = DataPackageOperation.Move;
            }

            deferral.Complete();
        }

        public bool IsEditModeEnabled
        {
            get
            {
                return ManualEntryBoxLoaded;
            }
            set
            {
                if (value)
                {
                    EditModeEnabled?.Invoke(this, new EventArgs());

                    var visiblePath = NavToolbar.FindDescendant("VisiblePath") as Control;
                    visiblePath?.Focus(FocusState.Programmatic);
                    visiblePath?.FindDescendant<TextBox>()?.SelectAll();
                }
                else
                {
                    ManualEntryBoxLoaded = false;
                    ClickablePathLoaded = true;
                }
            }
        }


        private bool manualEntryBoxLoaded;
        public bool ManualEntryBoxLoaded
        {
            get => manualEntryBoxLoaded;
            set => SetProperty(ref manualEntryBoxLoaded, value);
        }

        private bool clickablePathLoaded = true;
        public bool ClickablePathLoaded
        {
            get => clickablePathLoaded;
            set => SetProperty(ref clickablePathLoaded, value);
        }


        private string pathControlDisplayText;
        public string PathControlDisplayText
        {
            get => pathControlDisplayText;
            set => SetProperty(ref pathControlDisplayText, value);
        }

        public ICommand BackClickCommand => new RelayCommand<RoutedEventArgs>(e => BackRequested?.Invoke(this, EventArgs.Empty));
        public ICommand ForwardClickCommand => new RelayCommand<RoutedEventArgs>(e => ForwardRequested?.Invoke(this, EventArgs.Empty));
        public ICommand UpClickCommand => new RelayCommand<RoutedEventArgs>(e => UpRequested?.Invoke(this, EventArgs.Empty));
        public ICommand RefreshClickCommand => new RelayCommand<RoutedEventArgs>(e => RefreshRequested?.Invoke(this, EventArgs.Empty));

        public void PathItemSeparator_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var pathSeparatorIcon = sender as FontIcon;
            if (pathSeparatorIcon.DataContext == null)
            {
                return;
            }
            ToolbarPathItemLoaded?.Invoke(pathSeparatorIcon, new ToolbarPathItemLoadedEventArgs()
            {
                Item = pathSeparatorIcon.DataContext as PathBoxItem,
                OpenedFlyout = pathSeparatorIcon.ContextFlyout as MenuFlyout
            });
        }

        public void PathboxItemFlyout_Opened(object sender, object e)
        {
            ToolbarFlyoutOpened?.Invoke(this, new ToolbarFlyoutOpenedEventArgs() { OpenedFlyout = sender as MenuFlyout });
        }

        public void VisiblePath_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                AddressBarTextEntered?.Invoke(this, new AddressBarTextEnteredEventArgs() { AddressBarTextField = sender });
            }
        }

        public void VisiblePath_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            PathBoxQuerySubmitted?.Invoke(this, new ToolbarQuerySubmittedEventArgs() { QueryText = args.QueryText });

            (this as INavigationToolbar).IsEditModeEnabled = false;
        }

        public void PathBoxItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var itemTappedPath = ((sender as Border).DataContext as PathBoxItem).Path;
            ToolbarPathItemInvoked?.Invoke(this, new PathNavigationEventArgs()
            {
                ItemPath = itemTappedPath
            });
        }

        public void SwitchSearchBoxVisibility()
        {
            if (IsSearchBoxVisible)
            {
                SearchBox.Query = string.Empty;
                IsSearchBoxVisible = false;
            }
            else
            {
                IsSearchBoxVisible = true;

                // Given that binding and layouting might take a few cycles, when calling UpdateLayout
                // we can guarantee that the focus call will be able to find an open ASB
                var searchbox = NavToolbar.FindDescendant("SearchRegion") as SearchBox;
                searchbox?.UpdateLayout();
                searchbox?.Focus(FocusState.Programmatic);
            }
        }

        NavigationToolbar NavToolbar => (Window.Current.Content as Frame).FindDescendant<NavigationToolbar>();

        #region YourHome Widgets

        public bool ShowFolderWidgetWidget
        {
            get => App.AppSettings.ShowFolderWidgetWidget;
            set
            {
                if (App.AppSettings.ShowFolderWidgetWidget != value)
                {
                    App.AppSettings.ShowFolderWidgetWidget = value;

                    RefreshWidgetsRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool ShowDrivesWidget
        {
            get => App.AppSettings.ShowDrivesWidget;
            set
            {
                if (App.AppSettings.ShowDrivesWidget != value)
                {
                    App.AppSettings.ShowDrivesWidget = value;

                    RefreshWidgetsRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool ShowBundlesWidget
        {
            get => App.AppSettings.ShowBundlesWidget;
            set
            {
                if (App.AppSettings.ShowBundlesWidget != value)
                {
                    App.AppSettings.ShowBundlesWidget = value;

                    RefreshWidgetsRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool ShowRecentFilesWidget
        {
            get => App.AppSettings.ShowRecentFilesWidget;
            set
            {
                if (App.AppSettings.ShowRecentFilesWidget != value)
                {
                    App.AppSettings.ShowRecentFilesWidget = value;

                    RefreshWidgetsRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        #endregion YourHome Widgets

        public void CloseSearchBox()
        {
            SearchBox.Query = string.Empty;
            IsSearchBoxVisible = false;
        }

        public void SearchRegion_LostFocus(object sender, RoutedEventArgs e)
        {
            var focusedElement = FocusManager.GetFocusedElement();
            if ((focusedElement is Button bttn && bttn.Name == "SearchButton") || focusedElement is FlyoutBase || focusedElement is AppBarButton)
            {
                return;
            }

            CloseSearchBox();
        }

        private void SearchRegion_SuggestionChosen(ISearchBox sender, SearchBoxSuggestionChosenEventArgs args) => IsSearchBoxVisible = false;
        private void SearchRegion_Escaped(object sender, ISearchBox searchBox) => IsSearchBoxVisible = false;

        public void Dispose()
        {
            SearchBox.SuggestionChosen -= SearchRegion_SuggestionChosen;
            SearchBox.Escaped -= SearchRegion_Escaped;
        }

        public ICommand SelectAllContentPageItemsCommand { get; set; }

        public ICommand InvertContentPageSelctionCommand { get; set; }

        public ICommand ClearContentPageSelectionCommand { get; set; }

        public ICommand PasteItemsFromClipboardCommand { get; set; }

        public ICommand CopyPathOfWorkingDirectoryCommand { get; set; }

        public ICommand OpenNewWindowCommand { get; set; }

        public ICommand OpenNewPaneCommand { get; set; }

        public ICommand ClosePaneCommand { get; set; }

        public ICommand OpenDirectoryInDefaultTerminalCommand { get; set; }

        public ICommand CreateNewFileCommand { get; set; }

        public ICommand CreateNewFolderCommand { get; set; }

        public async Task SetPathBoxDropDownFlyoutAsync(MenuFlyout flyout, PathBoxItem pathItem, IShellPage shellPage)
        {
            var nextPathItemTitle = PathComponents[PathComponents.IndexOf(pathItem) + 1].Title;
            IList<StorageFolderWithPath> childFolders = null;

            StorageFolderWithPath folder = await shellPage.FilesystemViewModel.GetFolderWithPathFromPathAsync(pathItem.Path);
            if (folder != null)
            {
                childFolders = (await FilesystemTasks.Wrap(() => folder.GetFoldersWithPathAsync(string.Empty))).Result;
            }
            flyout.Items?.Clear();

            if (childFolders == null || childFolders.Count == 0)
            {
                var flyoutItem = new MenuFlyoutItem
                {
                    Icon = new FontIcon { Glyph = "\uE7BA" },
                    Text = "SubDirectoryAccessDenied".GetLocalized(),
                    //Foreground = (SolidColorBrush)Application.Current.Resources["SystemControlErrorTextForegroundBrush"],
                    FontSize = 12
                };
                flyout.Items.Add(flyoutItem);
                return;
            }

            var boldFontWeight = new FontWeight { Weight = 800 };
            var normalFontWeight = new FontWeight { Weight = 400 };

            var workingPath = PathComponents
                    [PathComponents.Count - 1].
                    Path?.TrimEnd(Path.DirectorySeparatorChar);

            foreach (var childFolder in childFolders)
            {
                var isPathItemFocused = childFolder.Item.Name == nextPathItemTitle;

                var flyoutItem = new MenuFlyoutItem
                {
                    Icon = new FontIcon
                    {
                        Glyph = "\uED25",
                        FontWeight = isPathItemFocused ? boldFontWeight : normalFontWeight
                    },
                    Text = childFolder.Item.Name,
                    FontSize = 12,
                    FontWeight = isPathItemFocused ? boldFontWeight : normalFontWeight
                };

                if (workingPath != childFolder.Path)
                {
                    flyoutItem.Click += (sender, args) =>
                    {
                        // Navigate to the directory
                        shellPage.NavigateToPath(childFolder.Path);
                    };
                }

                flyout.Items.Add(flyoutItem);
            }
        }

        public async void CheckPathInput(string currentInput, string currentSelectedPath, IShellPage shellPage)
        {
            currentInput = currentInput.Replace("\\\\", "\\");

            if (currentInput.StartsWith("\\") && !currentInput.StartsWith("\\\\"))
            {
                currentInput = currentInput.Insert(0, "\\");
            }

            if (currentSelectedPath == currentInput || string.IsNullOrWhiteSpace(currentInput))
            {
                return;
            }

            if (currentInput != shellPage.FilesystemViewModel.WorkingDirectory || shellPage.CurrentPageType == typeof(WidgetsPage))
            {
                if (currentInput.Equals("Home", StringComparison.OrdinalIgnoreCase)
                    || currentInput.Equals("NewTab".GetLocalized(), StringComparison.OrdinalIgnoreCase))
                {
                    shellPage.NavigateHome();
                }
                else
                {
                    currentInput = StorageFileExtensions.GetPathWithoutEnvironmentVariable(currentInput);
                    if (currentSelectedPath == currentInput)
                    {
                        return;
                    }

                    var item = await FilesystemTasks.Wrap(() => DrivesManager.GetRootFromPathAsync(currentInput));

                    var resFolder = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderWithPathFromPathAsync(currentInput, item));
                    if (resFolder || FolderHelpers.CheckFolderAccessWithWin32(currentInput))
                    {
                        var pathToNavigate = resFolder.Result?.Path ?? currentInput;
                        shellPage.NavigateToPath(pathToNavigate);
                    }
                    else // Not a folder or inaccessible
                    {
                        var resFile = await FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileWithPathFromPathAsync(currentInput, item));
                        if (resFile)
                        {
                            var pathToInvoke = resFile.Result.Path;
                            await Win32Helpers.InvokeWin32ComponentAsync(pathToInvoke, shellPage);
                        }
                        else // Not a file or not accessible
                        {
                            var workingDir = string.IsNullOrEmpty(shellPage.FilesystemViewModel.WorkingDirectory)
                                    || shellPage.CurrentPageType == typeof(WidgetsPage)
                                ? App.AppSettings.HomePath
                                : shellPage.FilesystemViewModel.WorkingDirectory;

                            // Launch terminal application if possible
                            foreach (var terminal in App.AppSettings.TerminalController.Model.Terminals)
                            {
                                if (terminal.Path.Equals(currentInput, StringComparison.OrdinalIgnoreCase)
                                    || terminal.Path.Equals(currentInput + ".exe", StringComparison.OrdinalIgnoreCase) || terminal.Name.Equals(currentInput, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (shellPage.ServiceConnection != null)
                                    {
                                        var value = new ValueSet
                                        {
                                            { "Arguments", "LaunchApp" },
                                            { "WorkingDirectory", workingDir },
                                            { "Application", terminal.Path },
                                            { "Parameters", string.Format(terminal.Arguments, workingDir) }
                                        };
                                        await shellPage.ServiceConnection.SendMessageAsync(value);
                                    }
                                    return;
                                }
                            }

                            try
                            {
                                if (!await Launcher.LaunchUriAsync(new Uri(currentInput)))
                                {
                                    await DialogDisplayHelper.ShowDialogAsync("InvalidItemDialogTitle".GetLocalized(),
                                        string.Format("InvalidItemDialogContent".GetLocalized(), Environment.NewLine, resFolder.ErrorCode.ToString()));
                                }
                            }
                            catch (Exception ex) when (ex is UriFormatException || ex is ArgumentException)
                            {
                                await DialogDisplayHelper.ShowDialogAsync("InvalidItemDialogTitle".GetLocalized(),
                                    string.Format("InvalidItemDialogContent".GetLocalized(), Environment.NewLine, resFolder.ErrorCode.ToString()));
                            }
                        }
                    }
                }

                PathControlDisplayText = shellPage.FilesystemViewModel.WorkingDirectory;
            }
        }

        public async void SetAddressBarSuggestions(AutoSuggestBox sender, IShellPage shellpage, int maxSuggestions = 7)
        {
            if (!string.IsNullOrWhiteSpace(sender.Text))
            {
                try
                {
                    IList<ListedItem> suggestions = null;
                    var expandedPath = StorageFileExtensions.GetPathWithoutEnvironmentVariable(sender.Text);
                    var folderPath = Path.GetDirectoryName(expandedPath) ?? expandedPath;
                    var folder = await shellpage.FilesystemViewModel.GetFolderWithPathFromPathAsync(folderPath);
                    var currPath = await folder.Result.GetFoldersWithPathAsync(Path.GetFileName(expandedPath), (uint)maxSuggestions);
                    if (currPath.Count() >= maxSuggestions)
                    {
                        suggestions = currPath.Select(x => new ListedItem(null)
                        {
                            ItemPath = x.Path,
                            ItemName = x.Folder.DisplayName
                        }).ToList();
                    }
                    else if (currPath.Any())
                    {
                        var subPath = await currPath.First().GetFoldersWithPathAsync((uint)(maxSuggestions - currPath.Count()));
                        suggestions = currPath.Select(x => new ListedItem(null)
                        {
                            ItemPath = x.Path,
                            ItemName = x.Folder.DisplayName
                        }).Concat(
                            subPath.Select(x => new ListedItem(null)
                            {
                                ItemPath = x.Path,
                                ItemName = Path.Combine(currPath.First().Folder.DisplayName, x.Folder.DisplayName)
                            })).ToList();
                    }
                    else
                    {
                        suggestions = new List<ListedItem>() { new ListedItem(null) {
                        ItemPath = shellpage.FilesystemViewModel.WorkingDirectory,
                        ItemName = "NavigationToolbarVisiblePathNoResults".GetLocalized() } };
                    }

                    // NavigationBarSuggestions becoming empty causes flickering of the suggestion box
                    // Here we check whether at least an element is in common between old and new list
                    if (!NavigationBarSuggestions.IntersectBy(suggestions, x => x.ItemName).Any())
                    {
                        // No elements in common, update the list in-place
                        for (int si = 0; si < suggestions.Count; si++)
                        {
                            if (si < NavigationBarSuggestions.Count)
                            {
                                NavigationBarSuggestions[si].ItemName = suggestions[si].ItemName;
                                NavigationBarSuggestions[si].ItemPath = suggestions[si].ItemPath;
                            }
                            else
                            {
                                NavigationBarSuggestions.Add(suggestions[si]);
                            }
                        }
                        while (NavigationBarSuggestions.Count > suggestions.Count)
                        {
                            NavigationBarSuggestions.RemoveAt(NavigationBarSuggestions.Count - 1);
                        }
                    }
                    else
                    {
                        // At least an element in common, show animation
                        foreach (var s in NavigationBarSuggestions.ExceptBy(suggestions, x => x.ItemName).ToList())
                        {
                            NavigationBarSuggestions.Remove(s);
                        }
                        foreach (var s in suggestions.ExceptBy(NavigationBarSuggestions, x => x.ItemName).ToList())
                        {
                            NavigationBarSuggestions.Insert(suggestions.IndexOf(s), s);
                        }
                    }
                }
                catch
                {
                    NavigationBarSuggestions.Clear();
                    NavigationBarSuggestions.Add(new ListedItem(null)
                    {
                        ItemPath = shellpage.FilesystemViewModel.WorkingDirectory,
                        ItemName = "NavigationToolbarVisiblePathNoResults".GetLocalized()
                    });
                }
            }
        }

    }
}
