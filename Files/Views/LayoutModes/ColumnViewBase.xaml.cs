using Files.EventArguments;
using Files.Filesystem;
using Files.Helpers;
using Files.Helpers.XamlHelpers;
using Files.Interacts;
using Files.UserControls.Selection;
using Microsoft.Toolkit.Uwp.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Files.Views.LayoutModes
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class ColumnViewBase : BaseLayout
    {
        private DispatcherQueueTimer tapDebounceTimer;
        private ListedItem renamingItem;
        private string oldItemName;
        private TextBlock textBlock;

        public ColumnViewBase() : base()
        {
            this.InitializeComponent();
            CurrentColumn = this;
            var selectionRectangle = RectangleSelection.Create(FileList, SelectionRectangle, FileList_SelectionChanged);
            tapDebounceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        }

        protected override void HookEvents()
        {
            UnhookEvents();
            ItemManipulationModel.FocusFileListInvoked += ItemManipulationModel_FocusFileListInvoked;
            ItemManipulationModel.SelectAllItemsInvoked += ItemManipulationModel_SelectAllItemsInvoked;
            ItemManipulationModel.ClearSelectionInvoked += ItemManipulationModel_ClearSelectionInvoked;
            ItemManipulationModel.InvertSelectionInvoked += ItemManipulationModel_InvertSelectionInvoked;
            ItemManipulationModel.AddSelectedItemInvoked += ItemManipulationModel_AddSelectedItemInvoked;
            ItemManipulationModel.RemoveSelectedItemInvoked += ItemManipulationModel_RemoveSelectedItemInvoked;
            ItemManipulationModel.FocusSelectedItemsInvoked += ItemManipulationModel_FocusSelectedItemsInvoked;
            ItemManipulationModel.StartRenameItemInvoked += ItemManipulationModel_StartRenameItemInvoked;
            ItemManipulationModel.ScrollIntoViewInvoked += ItemManipulationModel_ScrollIntoViewInvoked;
        }

        private void ItemManipulationModel_ScrollIntoViewInvoked(object sender, ListedItem e)
        {
            try
            {
                FileList.ScrollIntoView(e, ScrollIntoViewAlignment.Default);
            }
            catch (Exception)
            {
                // Catch error where row index could not be found
            }
        }

        private void ItemManipulationModel_StartRenameItemInvoked(object sender, EventArgs e)
        {
            StartRenameItem();
        }

        private void ItemManipulationModel_FocusSelectedItemsInvoked(object sender, EventArgs e)
        {
            FileList.ScrollIntoView(FileList.Items.Last());
        }

        private void ItemManipulationModel_AddSelectedItemInvoked(object sender, ListedItem e)
        {
            FileList?.SelectedItems.Add(e);
        }

        private void ItemManipulationModel_RemoveSelectedItemInvoked(object sender, ListedItem e)
        {
            FileList?.SelectedItems.Remove(e);
        }

        private void ItemManipulationModel_InvertSelectionInvoked(object sender, EventArgs e)
        {
            if (SelectedItems.Count < GetAllItems().Cast<ListedItem>().Count() / 2)
            {
                var oldSelectedItems = SelectedItems.ToList();
                ItemManipulationModel.SelectAllItems();
                ItemManipulationModel.RemoveSelectedItems(oldSelectedItems);
            }
            else
            {
                List<ListedItem> newSelectedItems = GetAllItems()
                    .Cast<ListedItem>()
                    .Except(SelectedItems)
                    .ToList();

                ItemManipulationModel.SetSelectedItems(newSelectedItems);
            }
        }

        private void ItemManipulationModel_ClearSelectionInvoked(object sender, EventArgs e)
        {
            FileList.SelectedItems.Clear();
        }

        private void ItemManipulationModel_SelectAllItemsInvoked(object sender, EventArgs e)
        {
            FileList.SelectAll();
        }

        private void ItemManipulationModel_FocusFileListInvoked(object sender, EventArgs e)
        {
            FileList.Focus(FocusState.Programmatic);
        }

        protected override void UnhookEvents()
        {
            if (ItemManipulationModel != null)
            {
                ItemManipulationModel.FocusFileListInvoked -= ItemManipulationModel_FocusFileListInvoked;
                ItemManipulationModel.SelectAllItemsInvoked -= ItemManipulationModel_SelectAllItemsInvoked;
                ItemManipulationModel.ClearSelectionInvoked -= ItemManipulationModel_ClearSelectionInvoked;
                ItemManipulationModel.InvertSelectionInvoked -= ItemManipulationModel_InvertSelectionInvoked;
                ItemManipulationModel.AddSelectedItemInvoked -= ItemManipulationModel_AddSelectedItemInvoked;
                ItemManipulationModel.RemoveSelectedItemInvoked -= ItemManipulationModel_RemoveSelectedItemInvoked;
                ItemManipulationModel.FocusSelectedItemsInvoked -= ItemManipulationModel_FocusSelectedItemsInvoked;
                ItemManipulationModel.StartRenameItemInvoked -= ItemManipulationModel_StartRenameItemInvoked;
                ItemManipulationModel.ScrollIntoViewInvoked -= ItemManipulationModel_ScrollIntoViewInvoked;
            }
        }

        private void ListViewTextBoxItemName_TextChanged(object sender, TextChangedEventArgs e)
        {
            var textBox = sender as TextBox;

            if (FilesystemHelpers.ContainsRestrictedCharacters(textBox.Text))
            {
                FileNameTeachingTip.Visibility = Visibility.Visible;
                FileNameTeachingTip.IsOpen = true;
            }
            else
            {
                if (FileNameTeachingTip.IsOpen == true)
                {
                    FileNameTeachingTip.IsOpen = false;
                    FileNameTeachingTip.Visibility = Visibility.Collapsed;
                }
            }
        }

        public static event EventHandler ItemInvoked;

        public static event EventHandler DismissColumn;

        public static event EventHandler UnFocusPreviousListView;

        protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
        {
            base.OnNavigatedTo(eventArgs);
            var param = (eventArgs.Parameter as NavigationArguments);
            //NavParam = param.NavPathParam;
            //var viewmodel = new ItemViewModel(FolderSettings);
            //await ParentShellPageInstance.FilesystemViewModel.SetWorkingDirectoryAsync(NavParam);
            //await viewmodel.SetWorkingDirectoryAsync(NavParam);
            FolderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;
            FolderSettings.LayoutModeChangeRequested += FolderSettings_LayoutModeChangeRequested;
            ParentShellPageInstance.IsCurrentInstance = true;
            ColumnViewBrowser.columnparent.UpdatePathUIToWorkingDirectory(param.NavPathParam);
            var parameters = (NavigationArguments)eventArgs.Parameter;
            if (parameters.IsLayoutSwitch)
            {
                //ReloadItemIcons();
            }
        }

        protected override void InitializeCommandsViewModel()
        {
            CommandsViewModel = new BaseLayoutCommandsViewModel(new BaseLayoutCommandImplementationModel(ParentShellPageInstance, ItemManipulationModel));
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);
            FolderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;
        }

        private async void ReloadItemIcons()
        {
            ParentShellPageInstance.FilesystemViewModel.CancelExtendedPropertiesLoading();
            foreach (ListedItem listedItem in ParentShellPageInstance.FilesystemViewModel.FilesAndFolders.ToList())
            {
                listedItem.ItemPropertiesInitialized = false;
                if (FileList.ContainerFromItem(listedItem) != null)
                {
                    listedItem.ItemPropertiesInitialized = true;
                    await ParentShellPageInstance.FilesystemViewModel.LoadExtendedItemProperties(listedItem, 24);
                }
            }
        }

        private void FolderSettings_LayoutModeChangeRequested(object sender, LayoutModeEventArgs e)
        {
        }

        private void StartRenameItem()
        {
            renamingItem = FileList.SelectedItem as ListedItem;
            int extensionLength = renamingItem.FileExtension?.Length ?? 0;
            ListViewItem listViewItem = FileList.ContainerFromItem(renamingItem) as ListViewItem;
            TextBox textBox = null;
            if (listViewItem == null)
            {
                return;
            }
            textBlock = listViewItem.FindDescendant("ItemName") as TextBlock;
            textBox = listViewItem.FindDescendant("ListViewTextBoxItemName") as TextBox;
            //textBlock = (listViewItem.ContentTemplateRoot as Border).FindDescendant("ItemName") as TextBlock;
            //textBox = (listViewItem.ContentTemplateRoot as Border).FindDescendant("ListViewTextBoxItemName") as TextBox;
            textBox.Text = textBlock.Text;
            oldItemName = textBlock.Text;
            textBlock.Visibility = Visibility.Collapsed;
            textBox.Visibility = Visibility.Visible;

            textBox.Focus(FocusState.Pointer);
            textBox.LostFocus += RenameTextBox_LostFocus;
            textBox.KeyDown += RenameTextBox_KeyDown;

            int selectedTextLength = SelectedItem.ItemName.Length;
            if (!SelectedItem.IsShortcutItem && App.AppSettings.ShowFileExtensions)
            {
                selectedTextLength -= extensionLength;
            }
            textBox.Select(0, selectedTextLength);
            IsRenamingItem = true;
        }

        private void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                TextBox textBox = sender as TextBox;
                textBox.LostFocus -= RenameTextBox_LostFocus;
                textBox.Text = oldItemName;
                EndRename(textBox);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter)
            {
                TextBox textBox = sender as TextBox;
                textBox.LostFocus -= RenameTextBox_LostFocus;
                CommitRename(textBox);
                e.Handled = true;
            }
        }

        private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // This check allows the user to use the text box context menu without ending the rename
            if (!(FocusManager.GetFocusedElement() is AppBarButton))
            {
                TextBox textBox = e.OriginalSource as TextBox;
                CommitRename(textBox);
            }
        }

        private async void CommitRename(TextBox textBox)
        {
            EndRename(textBox);
            string newItemName = textBox.Text.Trim().TrimEnd('.');

            bool successful = await UIFilesystemHelpers.RenameFileItemAsync(renamingItem, oldItemName, newItemName, ParentShellPageInstance);
            if (!successful)
            {
                renamingItem.ItemName = oldItemName;
            }
        }

        private void EndRename(TextBox textBox)
        {
            if (textBox.Parent == null)
            {
                // Navigating away, do nothing
            }
            else
            {
                textBox.Visibility = Visibility.Collapsed;
                textBlock.Visibility = Visibility.Visible;
            }

            textBox.LostFocus -= RenameTextBox_LostFocus;
            textBox.KeyDown -= RenameTextBox_KeyDown;
            FileNameTeachingTip.IsOpen = false;
            IsRenamingItem = false;
        }

        public override void ResetItemOpacity()
        {
            // throw new NotImplementedException();
        }

        protected override ListedItem GetItemFromElement(object element)
        {
            return (element as ListViewItem).DataContext as ListedItem ?? (element as ListViewItem).Content as ListedItem;
        }

        #region IDisposable

        public override void Dispose()
        {
            UnhookEvents();
            CommandsViewModel?.Dispose();
        }

        #endregion IDisposable

        public static ColumnViewBase CurrentColumn;
        private ListViewItem listViewItem;

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e != null)
            {
                // Do not commit rename if SelectionChanged is due to selction rectangle (#3660)
                //FileList.CommitEdit();
            }
            UnFocusPreviousListView?.Invoke(FileList, EventArgs.Empty);
            tapDebounceTimer.Stop();
            SelectedItems = FileList.SelectedItems.Cast<ListedItem>().Where(x => x != null).ToList();
        }

        private void FileList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (!IsRenamingItem)
            {
                HandleRightClick(sender, e);
            }
        }

        private void HandleRightClick(object sender, RightTappedRoutedEventArgs e)
        {
            var objectPressed = ((FrameworkElement)e.OriginalSource).DataContext as ListedItem;
            if (objectPressed != null)
            {
                {
                    return;
                }
            }
            // Check if RightTapped row is currently selected
            if (IsItemSelected)
            {
                if (SelectedItems.Contains(objectPressed))
                {
                    return;
                }
            }

            // The following code is only reachable when a user RightTapped an unselected row
            ItemManipulationModel.SetSelectedItem(objectPressed);
        }

        private async void FileList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrlPressed = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            var shiftPressed = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

            if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
            {
                if (!IsRenamingItem)
                {
                    NavigationHelpers.OpenSelectedItems(ParentShellPageInstance, false);
                    e.Handled = true;
                }
            }
            else if (e.Key == VirtualKey.Enter && e.KeyStatus.IsMenuKeyDown)
            {
                FilePropertiesHelpers.ShowProperties(ParentShellPageInstance);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Space)
            {
                if (!IsRenamingItem && !ParentShellPageInstance.NavToolbarViewModel.IsEditModeEnabled)
                {
                    if (App.MainViewModel.IsQuickLookEnabled)
                    {
                        await QuickLookHelpers.ToggleQuickLook(ParentShellPageInstance);
                    }
                    e.Handled = true;
                }
            }
            else if (e.KeyStatus.IsMenuKeyDown && (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right || e.Key == VirtualKey.Up))
            {
                // Unfocus the GridView so keyboard shortcut can be handled
                NavToolbar?.Focus(FocusState.Pointer);
            }
            else if (ctrlPressed && shiftPressed && (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right || e.Key == VirtualKey.W))
            {
                // Unfocus the ListView so keyboard shortcut can be handled (ctrl + shift + W/"->"/"<-")
                NavToolbar?.Focus(FocusState.Pointer);
            }
            else if (e.KeyStatus.IsMenuKeyDown && shiftPressed && e.Key == VirtualKey.Add)
            {
                // Unfocus the ListView so keyboard shortcut can be handled (alt + shift + "+")
                NavToolbar?.Focus(FocusState.Pointer);
            }
        }

        private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            DismissColumn?.Invoke(sender as ListView, EventArgs.Empty);
            await Task.Delay(200);
            if (listViewItem != null)
            {
                listViewItem.Style = (Style)this.Resources["NormalStyle"];
            }
            if ((e.OriginalSource as FrameworkElement)?.DataContext is ListedItem && !AppSettings.OpenItemsWithOneclick)
            {
                if (listViewItem != null)
                {
                    listViewItem.Style = (Style)this.Resources["NormalStyle"];
                }
                var item = (e.OriginalSource as FrameworkElement).DataContext as ListedItem;
                if (item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder)
                {
                    if (item.ContainsFilesOrFolders)
                    {
                        listViewItem = (FileList.ContainerFromItem(item) as ListViewItem);

                        ItemInvoked?.Invoke(new ColumnParam { Path = item.ItemPath, ListView = FileList }, EventArgs.Empty);
                    }
                }
                // The delay gives time for the item to be selected
                else
                {
                    NavigationHelpers.OpenSelectedItems(ParentShellPageInstance, false);
                }
            }
        }

        private void FileList_Holding(object sender, HoldingRoutedEventArgs e)
        {
            HandleRightClick(sender, e);
        }

        private void HandleRightClick(object sender, HoldingRoutedEventArgs e)
        {
            var objectPressed = ((FrameworkElement)e.OriginalSource).DataContext as ListedItem;
            if (objectPressed != null)
            {
                {
                    return;
                }
            }
            // Check if RightTapped row is currently selected
            if (IsItemSelected)
            {
                if (SelectedItems.Contains(objectPressed))
                {
                    return;
                }
            }

            // The following code is only reachable when a user RightTapped an unselected row
            ItemManipulationModel.SetSelectedItem(objectPressed);
        }

        private async void FileList_ItemClick(object sender, ItemClickEventArgs e)
        {
            DismissColumn?.Invoke(sender as ListView, EventArgs.Empty);
            await Task.Delay(200);
            if (listViewItem != null)
            {
                listViewItem.Style = (Style)this.Resources["NormalStyle"];
            }
            var ctrlPressed = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            var shiftPressed = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

            if (ctrlPressed || shiftPressed) // Allow for Ctrl+Shift selection
            {
                return;
            }
            if (IsRenamingItem)
            {
                return;
            }
            var item = (e.ClickedItem as ListedItem);
            // Check if the setting to open items with a single click is turned on
            if (AppSettings.OpenItemsWithOneclick)
            {
                tapDebounceTimer.Stop();
                await Task.Delay(200);
                if (item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder)
                {
                    if (item.ContainsFilesOrFolders)
                    {
                        listViewItem = (FileList.ContainerFromItem(item) as ListViewItem);
                        ItemInvoked?.Invoke(new ColumnParam { Path = item.ItemPath, ListView = FileList }, EventArgs.Empty);
                    }
                }
                // The delay gives time for the item to be selected
                else
                {
                    NavigationHelpers.OpenSelectedItems(ParentShellPageInstance, false);
                }
            }
        }

        private void StackPanel_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var parentContainer = DependencyObjectHelpers.FindParent<ListViewItem>(e.OriginalSource as DependencyObject);
            if (parentContainer.IsSelected)
            {
                return;
            }
            // The following code is only reachable when a user RightTapped an unselected row
            ItemManipulationModel.SetSelectedItem(FileList.ItemFromContainer(parentContainer) as ListedItem);
        }

        private void FileListListItem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.KeyModifiers == VirtualKeyModifiers.Control)
            {
                if ((sender as SelectorItem).IsSelected)
                {
                    (sender as SelectorItem).IsSelected = false;
                    // Prevent issues arising caused by the default handlers attempting to select the item that has just been deselected by ctrl + click
                    e.Handled = true;
                }
            }
            else if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
            {
                if (!(sender as SelectorItem).IsSelected)
                {
                    (sender as SelectorItem).IsSelected = true;
                }
            }
        }

        private void Grid_Loaded(object sender, RoutedEventArgs e)
        {
            // This is the best way I could find to set the context flyout, as doing it in the styles isn't possible
            // because you can't use bindings in the setters
            DependencyObject item = VisualTreeHelper.GetParent(sender as Grid);
            while (!(item is ListViewItem))
                item = VisualTreeHelper.GetParent(item);
            var itemContainer = item as ListViewItem;
            itemContainer.ContextFlyout = ItemContextMenuFlyout;
        }

        private void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue)
            {
                InitializeDrag(args.ItemContainer);
                if (args.Item is ListedItem item && !item.ItemPropertiesInitialized)
                {
                    args.ItemContainer.PointerPressed += FileListListItem_PointerPressed;

                    args.RegisterUpdateCallback(3, async (s, c) =>
                    {
                        item.ItemPropertiesInitialized = true;
                        await ParentShellPageInstance.FilesystemViewModel.LoadExtendedItemProperties(item, 24);
                    });
                }
                else if (args.Item is ListedItem item1 && item1.ItemPropertiesInitialized && !item1.LoadFileIcon)
                {
                    args.RegisterUpdateCallback(3, async (s, c) =>
                    {
                        await ParentShellPageInstance.FilesystemViewModel.LoadItemThumbnail(item1, 24);
                    });
                }
            }
            else
            {
                UninitializeDrag(args.ItemContainer);
                args.ItemContainer.PointerPressed -= FileListListItem_PointerPressed;

                if (args.Item is ListedItem item && item.ItemPropertiesInitialized && item.LoadFileIcon)
                {
                    ParentShellPageInstance.FilesystemViewModel.UnloadItemThumbnail(item);
                }
            }
        }
    }
}