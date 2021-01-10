﻿using Files.SettingsInterfaces;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Files.ViewModels.Bundles
{
    /// <summary>
    /// Bundle's contents view model
    /// </summary>
    public class BundleContainerViewModel : ObservableObject, IDisposable
    {
        #region Singleton

        private IWidgetsSettings WidgetsSettings => App.WidgetsSettings;

        #endregion

        #region Private Members

        private IShellPage associatedInstance;

        #endregion

        #region Events

        public Action<BundleContainerViewModel> NotifyItemRemoved { get; set; }

        #endregion

        #region Public Properties

        /// <summary>
        /// A list of Bundle's contents
        /// </summary>
        public ObservableCollection<BundleItemViewModel> Contents { get; private set; } = new ObservableCollection<BundleItemViewModel>();

        private string bundleName = "DefaultBundle";
        public string BundleName
        {
            get => bundleName;
            set => SetProperty(ref bundleName, value);
        }

        private Visibility noBundleContentsTextVisibility;
        public Visibility NoBundleContentsTextVisibility
        {
            get => noBundleContentsTextVisibility;
            set => SetProperty(ref noBundleContentsTextVisibility, value);
        }

        private string bundleRenameText = string.Empty;
        public string BundleRenameText
        {
            get => bundleRenameText;
            set => SetProperty(ref bundleRenameText, value);
        }

        private Visibility bundleRenameVisibility = Visibility.Collapsed;
        public Visibility BundleRenameVisibility
        {
            get => bundleRenameVisibility;
            set => SetProperty(ref bundleRenameVisibility, value);
        }

        #endregion

        #region Commands

        public ICommand RemoveBundleCommand { get; set; }

        public ICommand RenameBundleCommand { get; set; }

        public ICommand RenameBundleConfirmCommand { get; set; }

        public ICommand RenameTextKeyDownCommand { get; set; }

        public ICommand DragOverCommand { get; set; }

        public ICommand DropCommand { get; set; }

        #endregion

        #region Constructor

        public BundleContainerViewModel(IShellPage associatedInstance)
        {
            this.associatedInstance = associatedInstance;
            this.BundleRenameText = BundleName;

            // Create commands
            RemoveBundleCommand = new RelayCommand(RemoveBundle);
            RenameBundleCommand = new RelayCommand(RenameBundle);
            RenameBundleConfirmCommand = new RelayCommand(RenameBundleConfirm);
            RenameTextKeyDownCommand = new RelayCommand<KeyRoutedEventArgs>(RenameTextKeyDown);
            DragOverCommand = new RelayCommand<DragEventArgs>(DragOver);
            DropCommand = new RelayCommand<DragEventArgs>(Drop);
        }

        #endregion

        #region Command Implementation

        private void RemoveBundle()
        {
            if (WidgetsSettings.SavedBundles.ContainsKey(BundleName))
            {
                Dictionary<string, List<string>> allBundles = WidgetsSettings.SavedBundles; // We need to do it this way for Set() to be called
                allBundles.Remove(BundleName);
                WidgetsSettings.SavedBundles = allBundles;
                NotifyItemRemoved(this);
            }
        }

        private void RenameBundle()
        {
            if (BundleRenameVisibility == Visibility.Visible)
                BundleRenameVisibility = Visibility.Collapsed;
            else
                BundleRenameVisibility = Visibility.Visible;
        }

        private void RenameBundleConfirm()
        {
            if (CanRenameBundle(BundleRenameText))
            {
                if (WidgetsSettings.SavedBundles.ContainsKey(BundleName))
                {
                    Dictionary<string, List<string>> allBundles = WidgetsSettings.SavedBundles; // We need to do it this way for Set() to be called
                    Dictionary<string, List<string>> newBundles = new Dictionary<string, List<string>>();

                    foreach (var item in allBundles)
                    {
                        if (item.Key == BundleName) // Item matches to-rename name
                        {
                            newBundles.Add(BundleRenameText, item.Value);

                            // We need to remember to change BundleItemViewModel.OriginBundleName!
                            foreach (var bundleItem in Contents)
                            {
                                bundleItem.OriginBundleName = BundleRenameText;
                            }
                        }
                        else // Ignore, and add existing values
                        {
                            newBundles.Add(item.Key, item.Value);
                        }
                    }

                    WidgetsSettings.SavedBundles = newBundles;
                    BundleName = BundleRenameText;
                }
            }

            CloseRename();
        }

        private void RenameTextKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                RenameBundleConfirm();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                CloseRename();
                e.Handled = true;
            }
        }

        private void DragOver(DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Move;
                e.Handled = true;
            }
        }

        private async void Drop(DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                bool itemsAdded = false;
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();

                foreach (IStorageItem item in items)
                {
                    if (Contents.Count < Constants.Widgets.Bundles.MaxAmountOfItemsPerBundle)
                    {
                        if (!Contents.Any((i) => i.Path == item.Path)) // Don't add existing items!
                        {
                            AddBundleItem(new BundleItemViewModel(associatedInstance, item.Path, item.IsOfType(StorageItemTypes.Folder) ? Filesystem.FilesystemItemType.Directory : Filesystem.FilesystemItemType.File)
                            {
                                OriginBundleName = BundleName,
                                NotifyItemRemoved = NotifyItemRemovedHandle
                            });
                            itemsAdded = true;
                        }
                    }
                }
                e.Handled = true;

                if (itemsAdded)
                {
                    SaveBundle();
                    // Log here?
                }
            }
        }

        #endregion

        #region Handlers

        /// <summary>
        /// This function gets called when an item is removed to update the collection
        /// </summary>
        /// <param name="item"></param>
        private void NotifyItemRemovedHandle(BundleItemViewModel item)
        {
            Contents.Remove(item);
            item?.Dispose();

            if (Contents.Count == 0)
            {
                NoBundleContentsTextVisibility = Visibility.Visible;
            }
        }

        #endregion

        #region Private Helpers

        private bool SaveBundle()
        {
            if (WidgetsSettings.SavedBundles.ContainsKey(BundleName))
            {
                Dictionary<string, List<string>> allBundles = WidgetsSettings.SavedBundles; // We need to do it this way for Set() to be called
                allBundles[BundleName] = Contents.Select((item) => item.Path).ToList();
                WidgetsSettings.SavedBundles = allBundles;

                return true;
            }

            return false;
        }

        private void CloseRename()
        {
            BundleRenameVisibility = Visibility.Collapsed;
            BundleRenameText = BundleName;
        }

        #endregion

        #region Public Helpers

        public BundleContainerViewModel AddBundleItem(BundleItemViewModel bundleItem)
        {
            if (bundleItem != null)
            {
                Contents.Add(bundleItem);
                NoBundleContentsTextVisibility = Visibility.Collapsed;
            }

            return this;
        }

        public BundleContainerViewModel SetBundleItems(List<BundleItemViewModel> items)
        {
            Contents = new ObservableCollection<BundleItemViewModel>(items);

            if (Contents.Count > 0)
            {
                NoBundleContentsTextVisibility = Visibility.Collapsed;
            }

            return this;
        }

        public bool CanRenameBundle(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (!WidgetsSettings.SavedBundles.Any((item) => item.Key == name))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            foreach (var item in Contents)
            {
                item.NotifyItemRemoved -= NotifyItemRemovedHandle;
                item?.Dispose();
            }

            BundleName = null;
            BundleRenameText = null;

            RemoveBundleCommand = null;
            RenameBundleCommand = null;
            RenameBundleConfirmCommand = null;
            RenameTextKeyDownCommand = null;
            DragOverCommand = null;
            DropCommand = null;

            associatedInstance = null;
            Contents = null;
        }

        #endregion
    }
}
