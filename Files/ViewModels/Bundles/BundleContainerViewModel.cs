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

		private IJsonSettings JsonSettings => associatedInstance?.InstanceViewModel.JsonSettings;

		#endregion

		#region Private Members

		private IShellPage associatedInstance;

		#endregion

		#region Events

		public Action<BundleContainerViewModel> NotifyItemRemoved { get; set; }

		public Action<BundleContainerViewModel> NotifyItemRenamed { get; set; }

		#endregion

		#region Public Properties

		/// <summary>
		/// A list of Bundle's contents
		/// </summary>
		public ObservableCollection<BundleItemViewModel> Contents { get; private set; } = new ObservableCollection<BundleItemViewModel>();

		private string pBundleName = "DefaultBundle";
		public string BundleName
		{
			get => pBundleName;
			set => SetProperty(ref pBundleName, value);
		}

		private Visibility pNoBundleContentsTextVisibility;
		public Visibility NoBundleContentsTextVisibility
		{
			get => pNoBundleContentsTextVisibility;
			set => SetProperty(ref pNoBundleContentsTextVisibility, value);
		}

		private string pBundleRenameText = string.Empty;
		public string BundleRenameText
		{
			get => pBundleRenameText;
			set => SetProperty(ref pBundleRenameText, value);
		}

		private Visibility _BundleRenameVisibility = Visibility.Collapsed;
		public Visibility BundleRenameVisibility
		{
			get => _BundleRenameVisibility;
			set => SetProperty(ref _BundleRenameVisibility, value);
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
			if (JsonSettings.SavedBundles.ContainsKey(BundleName))
			{
				Dictionary<string, List<string>> allBundles = JsonSettings.SavedBundles; // We need to do it this way for Set() to be called
				allBundles.Remove(BundleName);
				JsonSettings.SavedBundles = allBundles;
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
				if (JsonSettings.SavedBundles.ContainsKey(BundleName))
				{
					Dictionary<string, List<string>> allBundles = JsonSettings.SavedBundles; // We need to do it this way for Set() to be called
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

					JsonSettings.SavedBundles = newBundles;
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
				bool itemAdded = false;
				IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();

				foreach (IStorageItem item in items)
				{
					if (items.Count <= Constants.Widgets.Bundles.MaxAmountOfItemsPerBundle)
					{
						if (!Contents.Any((i) => i.Path == item.Path)) // Don't add existing items!
						{
							AddBundleItem(new BundleItemViewModel(associatedInstance, item.Path, item.IsOfType(StorageItemTypes.Folder) ? Filesystem.FilesystemItemType.Directory : Filesystem.FilesystemItemType.File)
							{
								OriginBundleName = BundleName,
								NotifyItemRemoved = NotifyItemRemovedHandle
							});
							itemAdded = true;
						}
					}
				}
				e.Handled = true;

				if (itemAdded)
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

			if (Contents.Count == 0)
			{
				NoBundleContentsTextVisibility = Visibility.Visible;
			}
		}

		#endregion

		#region Private Helpers

		private bool SaveBundle()
		{
			if (JsonSettings.SavedBundles.ContainsKey(BundleName))
			{
				Dictionary<string, List<string>> allBundles = JsonSettings.SavedBundles; // We need to do it this way for Set() to be called
				allBundles[BundleName] = Contents.Select((item) => item.Path).ToList();
				JsonSettings.SavedBundles = allBundles;

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

			if (!JsonSettings.SavedBundles.Any((item) => item.Key == name))
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
			associatedInstance?.Dispose();

			associatedInstance = null;
			Contents = null;
		}

		#endregion
	}
}
