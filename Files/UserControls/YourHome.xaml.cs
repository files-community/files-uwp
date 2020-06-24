﻿#nullable enable
using Files.Filesystem;
using Files.Helpers;
using Files.Interacts;
using Files.View_Models;
using Files.Views.Pages;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Files
{
    public sealed partial class YourHome : Page
    {
        private ObservableCollection<RecentItem> recentItemsCollection = new ObservableCollection<RecentItem>();
        private EmptyRecentsText Empty { get; set; } = new EmptyRecentsText();
        public SettingsViewModel AppSettings => App.AppSettings;

        public YourHome()
        {
            InitializeComponent();
        }

        private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            var flyoutItem = sender as MenuFlyoutItem;
            var clickedOnItem = flyoutItem.DataContext as RecentItem;
            if (clickedOnItem.IsFile)
            {
                var filePath = clickedOnItem.RecentPath;
                var folderPath = filePath.Substring(0, filePath.Length - clickedOnItem.Name.Length);
                App.CurrentInstance.ContentFrame.Navigate(AppSettings.GetLayoutType(), folderPath);
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
        {
            base.OnNavigatedTo(eventArgs);
            App.CurrentInstance.InstanceViewModel.IsPageTypeNotHome = false;
            App.CurrentInstance.InstanceViewModel.IsPageTypeNotRecycleBin = true;
            var parameters = eventArgs.Parameter.ToString();
            Locations.ItemLoader.itemsAdded.Clear();
            Locations.ItemLoader.DisplayItems();
            recentItemsCollection.Clear();
            PopulateRecentsList();
            Frame rootFrame = Window.Current.Content as Frame;
            var instanceTabsView = rootFrame.Content as InstanceTabsView;
            instanceTabsView.SetSelectedTabInfo(parameters, null);
            instanceTabsView.TabStrip_SelectionChanged(null, null);
            App.CurrentInstance.NavigationToolbar.CanRefresh = false;
            App.PS.IsEnabled = false;
            App.CurrentInstance.NavigationToolbar.CanGoBack = App.CurrentInstance.ContentFrame.CanGoBack;
            App.CurrentInstance.NavigationToolbar.CanGoForward = App.CurrentInstance.ContentFrame.CanGoForward;
            App.CurrentInstance.NavigationToolbar.CanNavigateToParent = false;

            // Clear the path UI and replace with Favorites
            App.CurrentInstance.NavigationToolbar.PathComponents.Clear();
            string componentLabel = parameters;
            string tag = parameters;
            PathBoxItem item = new PathBoxItem()
            {
                Title = componentLabel,
                Path = tag,
            };
            App.CurrentInstance.NavigationToolbar.PathComponents.Add(item);
        }

        public async void PopulateRecentsList()
        {
            var mostRecentlyUsed = Windows.Storage.AccessCache.StorageApplicationPermissions.MostRecentlyUsedList;
            BitmapImage ItemImage;
            string ItemPath;
            string ItemName;
            StorageItemTypes ItemType;
            Visibility ItemFolderImgVis;
            Visibility ItemEmptyImgVis;
            Visibility ItemFileIconVis;
            bool IsRecentsListEmpty = true;
            foreach (var entry in mostRecentlyUsed.Entries)
            {
                try
                {
                    var item = await mostRecentlyUsed.GetItemAsync(entry.Token);
                    if (item.IsOfType(StorageItemTypes.File))
                    {
                        IsRecentsListEmpty = false;
                    }
                }
                catch (Exception) { }
            }

            if (IsRecentsListEmpty)
            {
                Empty.Visibility = Visibility.Visible;
            }
            else
            {
                Empty.Visibility = Visibility.Collapsed;
            }

            foreach (Windows.Storage.AccessCache.AccessListEntry entry in mostRecentlyUsed.Entries)
            {
                string mruToken = entry.Token;
                try
                {
                    IStorageItem item = await mostRecentlyUsed.GetItemAsync(mruToken);
                    if (item.IsOfType(StorageItemTypes.File))
                    {
                        ItemName = item.Name;
                        ItemPath = item.Path;
                        ItemType = StorageItemTypes.File;
                        ItemImage = new BitmapImage();
                        StorageFile file = await StorageFile.GetFileFromPathAsync(ItemPath);
                        var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 30, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
                        if (thumbnail == null)
                        {
                            ItemEmptyImgVis = Visibility.Visible;
                        }
                        else
                        {
                            await ItemImage.SetSourceAsync(thumbnail.CloneStream());
                            ItemEmptyImgVis = Visibility.Collapsed;
                        }
                        ItemFolderImgVis = Visibility.Collapsed;
                        ItemFileIconVis = Visibility.Visible;
                        recentItemsCollection.Add(new RecentItem() { RecentPath = ItemPath, Name = ItemName, Type = ItemType, FolderImg = ItemFolderImgVis, EmptyImgVis = ItemEmptyImgVis, FileImg = ItemImage, FileIconVis = ItemFileIconVis });
                    }
                }
                catch (FileNotFoundException)
                {
                    mostRecentlyUsed.Remove(mruToken);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip item until consent is provided
                }
                catch (COMException ex)
                {
                    mostRecentlyUsed.Remove(mruToken);
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }

            if (recentItemsCollection.Count == 0)
            {
                Empty.Visibility = Visibility.Visible;
            }
        }

        private async void RecentsView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var path = (e.ClickedItem as RecentItem).RecentPath;
            try
            {
                await Interaction.InvokeWin32Component(path);
            }
            catch (UnauthorizedAccessException)
            {
                await App.ConsentDialogDisplay.ShowAsync();
            }
            catch (ArgumentException)
            {
                if (new DirectoryInfo(path).Root.ToString().Contains(@"C:\"))
                {
                    App.CurrentInstance.ContentFrame.Navigate(AppSettings.GetLayoutType(), path);
                }
                else
                {
                    foreach (DriveItem drive in AppSettings.DrivesManager.Drives)
                    {
                        if (drive.Path.ToString() == new DirectoryInfo(path).Root.ToString())
                        {
                            App.CurrentInstance.ContentFrame.Navigate(AppSettings.GetLayoutType(), path);
                            return;
                        }
                    }
                }
            }
            catch (COMException)
            {
                await DialogDisplayHelper.ShowDialog(
                    ResourceController.GetTranslation("DriveUnpluggedDialog/Title"), 
                    ResourceController.GetTranslation("DriveUnpluggedDialog/Text"));
            }
        }

        private async void RemoveOneFrequentItem(object sender, RoutedEventArgs e)
        {
            // Get the sender frameworkelement

            if (sender is MenuFlyoutItem fe)
            {
                // Grab it's datacontext ViewModel and remove it from the list.

                if (fe.DataContext is RecentItem vm)
                {
                    if (await DialogDisplayHelper.ShowDialog("Remove item from Recents List", "Do you wish to remove " + vm.Name + " from the list?", "Yes", "No"))
                    {
                        // remove it from the visible collection
                        recentItemsCollection.Remove(vm);

                        // Now clear it also from the recent list cache permanently.
                        // No token stored in the viewmodel, so need to find it the old fashioned way.
                        var mru = Windows.Storage.AccessCache.StorageApplicationPermissions.MostRecentlyUsedList;

                        foreach (var element in mru.Entries)
                        {
                            var f = await mru.GetItemAsync(element.Token);
                            if (f.Path.Equals(vm.RecentPath))
                            {
                                mru.Remove(element.Token);
                                if (recentItemsCollection.Count == 0)
                                {
                                    Empty.Visibility = Visibility.Visible;
                                }
                                break;
                            }
                        }
                    }
                }
            }
        }

        private void MenuFlyoutItem_Click(object sender, RoutedEventArgs e)
        {
            recentItemsCollection.Clear();
            RecentsView.ItemsSource = null;
            var mru = Windows.Storage.AccessCache.StorageApplicationPermissions.MostRecentlyUsedList;
            mru.Clear();
            Empty.Visibility = Visibility.Visible;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            string NavigationPath = ""; // path to navigate
            string ClickedCard = (sender as Button).Tag.ToString();

            switch (ClickedCard)
            {
                case "Downloads":
                    NavigationPath = AppSettings.DownloadsPath;
                    break;

                case "Documents":
                    NavigationPath = AppSettings.DocumentsPath;
                    break;

                case "Pictures":
                    NavigationPath = AppSettings.PicturesPath;
                    break;

                case "Music":
                    NavigationPath = AppSettings.MusicPath;
                    break;

                case "Videos":
                    NavigationPath = AppSettings.VideosPath;
                    break;

                case "RecycleBin":
                    NavigationPath = AppSettings.RecycleBinPath;
                    break;
            }

            App.CurrentInstance.ContentFrame.Navigate(AppSettings.GetLayoutType(), NavigationPath);

            App.CurrentInstance.InstanceViewModel.IsPageTypeNotHome = true; // show controls that were hidden on the home page
        }
    }

    public class RecentItem
    {
        public BitmapImage FileImg { get; set; }
        public string RecentPath { get; set; }
        public string Name { get; set; }
        public bool IsFile { get => Type == StorageItemTypes.File; }
        public StorageItemTypes Type { get; set; }
        public Visibility FolderImg { get; set; }
        public Visibility EmptyImgVis { get; set; }
        public Visibility FileIconVis { get; set; }
    }

    public class EmptyRecentsText : INotifyPropertyChanged
    {
        private Visibility visibility;

        public Visibility Visibility
        {
            get
            {
                return visibility;
            }
            set
            {
                if (value != visibility)
                {
                    visibility = value;
                    NotifyPropertyChanged("Visibility");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}