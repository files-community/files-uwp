﻿using Files.Enums;
using Files.Helpers;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Microsoft.Toolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp.UI;
using Newtonsoft.Json;
using System;
using Windows.Storage;

namespace Files.View_Models
{
    public class FolderSettingsViewModel : ObservableObject
    {
        private static readonly ApplicationDataContainer localSettings = ApplicationData.Current.LocalSettings;

        private IShellPage associatedInstance;

        public FolderSettingsViewModel(IShellPage associatedInstance)
        {
            this.associatedInstance = associatedInstance;
            this.LayoutPreference = new LayoutPreferences();
        }

        public LayoutModes LayoutMode
        {
            get => LayoutPreference.LayoutMode;
            set
            {
                if (SetProperty(ref LayoutPreference.LayoutMode, value, nameof(LayoutMode)))
                {
                    UpdateLayoutPreferencesForPath(associatedInstance.FilesystemViewModel.WorkingDirectory, LayoutPreference);
                }
            }
        }

        private bool isLayoutModeChanging;

        public bool IsLayoutModeChanging
        {
            get => isLayoutModeChanging;
            set => SetProperty(ref isLayoutModeChanging, value);
        }

        public Type GetLayoutType(string folderPath)
        {
            var oldLayoutMode = LayoutPreference.LayoutMode;
            LayoutPreference = GetLayoutPreferencesForPath(folderPath);
            if (oldLayoutMode != LayoutPreference.LayoutMode)
            {
                IsLayoutModeChanging = true;
            }

            Type type = null;
            switch (LayoutMode)
            {
                case LayoutModes.DetailsView:
                    type = typeof(GenericFileBrowser);
                    break;

                case LayoutModes.TilesView:
                    type = typeof(GridViewBrowser);
                    break;

                case LayoutModes.GridView:
                    type = typeof(GridViewBrowser);
                    break;

                default:
                    type = typeof(GenericFileBrowser);
                    break;
            }
            return type;
        }

        public event EventHandler LayoutModeChangeRequested;
        public event EventHandler GridViewSizeChangeRequested;

        public RelayCommand ToggleLayoutModeGridViewLarge => new RelayCommand(() =>
        {
            LayoutMode = LayoutModes.GridView; // Grid View

            GridViewSize = Constants.Browser.GridViewBrowser.GridViewSizeLarge; // Size

            LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
        });

        public RelayCommand ToggleLayoutModeGridViewMedium => new RelayCommand(() =>
        {
            LayoutMode = LayoutModes.GridView; // Grid View

            GridViewSize = Constants.Browser.GridViewBrowser.GridViewSizeMedium; // Size

            LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
        });

        public RelayCommand ToggleLayoutModeGridViewSmall => new RelayCommand(() =>
        {
            LayoutMode = LayoutModes.GridView; // Grid View

            GridViewSize = Constants.Browser.GridViewBrowser.GridViewSizeSmall; // Size

            LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
        });

        public RelayCommand ToggleLayoutModeTiles => new RelayCommand(() =>
        {
            LayoutMode = LayoutModes.TilesView; // Tiles View

            LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
        });

        public RelayCommand ToggleLayoutModeDetailsView => new RelayCommand(() =>
        {
            LayoutMode = LayoutModes.DetailsView; // Details View

            LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
        });

        public int GridViewSize
        {
            get => LayoutPreference.GridViewSize;
            set
            {
                if (value < LayoutPreference.GridViewSize) // Size down
                {
                    if (LayoutMode == LayoutModes.TilesView) // Size down from tiles to list
                    {
                        LayoutMode = 0;
                        LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (LayoutMode == LayoutModes.GridView && value < Constants.Browser.GridViewBrowser.GridViewSizeSmall) // Size down from grid to tiles
                    {
                        LayoutMode = LayoutModes.TilesView;
                        LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (LayoutMode != LayoutModes.DetailsView) // Resize grid view
                    {
                        var newValue = (value >= Constants.Browser.GridViewBrowser.GridViewSizeSmall) ? value : Constants.Browser.GridViewBrowser.GridViewSizeSmall; // Set grid size to allow immediate UI update
                        SetProperty(ref LayoutPreference.GridViewSize, newValue, nameof(GridViewSize));

                        if (LayoutMode != LayoutModes.GridView) // Only update layout mode if it isn't already in grid view
                        {
                            LayoutMode = LayoutModes.GridView;
                            LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            UpdateLayoutPreferencesForPath(associatedInstance.FilesystemViewModel.WorkingDirectory, LayoutPreference);
                        }

                        GridViewSizeChangeRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (value > LayoutPreference.GridViewSize) // Size up
                {
                    if (LayoutMode == 0) // Size up from list to tiles
                    {
                        LayoutMode = LayoutModes.TilesView;
                        LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else // Size up from tiles to grid
                    {
                        var newValue = (LayoutMode == LayoutModes.TilesView) ? Constants.Browser.GridViewBrowser.GridViewSizeSmall : (value <= Constants.Browser.GridViewBrowser.GridViewSizeMax) ? value : Constants.Browser.GridViewBrowser.GridViewSizeMax; // Set grid size to allow immediate UI update
                        SetProperty(ref LayoutPreference.GridViewSize, newValue, nameof(GridViewSize));

                        if (LayoutMode != LayoutModes.GridView) // Only update layout mode if it isn't already in grid view
                        {
                            LayoutMode = LayoutModes.GridView;
                            LayoutModeChangeRequested?.Invoke(this, EventArgs.Empty);
                        }
                        else
                        {
                            UpdateLayoutPreferencesForPath(associatedInstance.FilesystemViewModel.WorkingDirectory, LayoutPreference);
                        }

                        if (value < Constants.Browser.GridViewBrowser.GridViewSizeMax) // Don't request a grid resize if it is already at the max size
                        {
                            GridViewSizeChangeRequested?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
            }
        }

        public event EventHandler SortOptionPreferenceUpdated;

        public event EventHandler SortDirectionPreferenceUpdated;

        public SortOption DirectorySortOption
        {
            get => LayoutPreference.DirectorySortOption;
            set
            {
                if (SetProperty(ref LayoutPreference.DirectorySortOption, value, nameof(DirectorySortOption)))
                {
                    UpdateLayoutPreferencesForPath(associatedInstance.FilesystemViewModel.WorkingDirectory, LayoutPreference);
                    SortOptionPreferenceUpdated?.Invoke(this, new EventArgs());
                }
            }
        }

        public SortDirection DirectorySortDirection
        {
            get => LayoutPreference.DirectorySortDirection;
            set
            {
                if (SetProperty(ref LayoutPreference.DirectorySortDirection, value, nameof(DirectorySortDirection)))
                {
                    UpdateLayoutPreferencesForPath(associatedInstance.FilesystemViewModel.WorkingDirectory, LayoutPreference);
                    SortDirectionPreferenceUpdated?.Invoke(this, new EventArgs());
                }
            }
        }

        private static LayoutPreferences GetLayoutPreferencesForPath(string folderPath)
        {
            if (App.AppSettings.AreLayoutPreferencesPerFolder)
            {
                var layoutPrefs = ReadLayoutPreferencesFromAds(folderPath);
                return layoutPrefs ?? ReadLayoutPreferencesFromSettings(folderPath);
            }
            return LayoutPreferences.DefaultLayoutPreferences;
        }

        private static void UpdateLayoutPreferencesForPath(string folderPath, LayoutPreferences prefs)
        {
            if (App.AppSettings.AreLayoutPreferencesPerFolder)
            {
                if (!WriteLayoutPreferencesToAds(folderPath, prefs))
                {
                    WriteLayoutPreferencesToSettings(folderPath, prefs);
                }
            }
            else
            {
                App.AppSettings.DefaultLayoutMode = prefs.LayoutMode;
                App.AppSettings.DefaultGridViewSize = prefs.GridViewSize;
                // Do not save OriginalPath as global sort option (only works in recycle bin)
                App.AppSettings.DefaultDirectorySortOption = prefs.DirectorySortOption == SortOption.OriginalPath ? SortOption.Name : prefs.DirectorySortOption;
                App.AppSettings.DefaultDirectorySortDirection = prefs.DirectorySortDirection;
            }
        }

        private static LayoutPreferences ReadLayoutPreferencesFromAds(string folderPath)
        {
            var str = NativeFileOperationsHelper.ReadStringFromFile($"{folderPath}:files_layoutmode");
            return string.IsNullOrEmpty(str) ? null : JsonConvert.DeserializeObject<LayoutPreferences>(str);
        }

        private static bool WriteLayoutPreferencesToAds(string folderPath, LayoutPreferences prefs)
        {
            if (LayoutPreferences.DefaultLayoutPreferences.Equals(prefs))
            {
                NativeFileOperationsHelper.DeleteFileFromApp($"{folderPath}:files_layoutmode");
                return false;
            }
            return NativeFileOperationsHelper.WriteStringToFile($"{folderPath}:files_layoutmode", JsonConvert.SerializeObject(prefs));
        }

        private static LayoutPreferences ReadLayoutPreferencesFromSettings(string folderPath)
        {
            ApplicationDataContainer dataContainer = localSettings.CreateContainer("LayoutModeContainer", ApplicationDataCreateDisposition.Always);
            var fixPath = folderPath.TrimEnd('\\');
            if (dataContainer.Values.ContainsKey(fixPath))
            {
                var val = (ApplicationDataCompositeValue)dataContainer.Values[fixPath];
                return LayoutPreferences.FromCompositeValue(val);
            }
            else
            {
                return LayoutPreferences.DefaultLayoutPreferences; // Either global setting or smart guess
            }
        }

        private static void WriteLayoutPreferencesToSettings(string folderPath, LayoutPreferences prefs)
        {
            ApplicationDataContainer dataContainer = localSettings.CreateContainer("LayoutModeContainer", ApplicationDataCreateDisposition.Always);
            var fixPath = folderPath.TrimEnd('\\');
            if (!dataContainer.Values.ContainsKey(fixPath))
            {
                if (prefs == LayoutPreferences.DefaultLayoutPreferences)
                {
                    return; // Do not create setting if it's default
                }
            }
            dataContainer.Values[fixPath] = prefs.ToCompositeValue();
        }
        private LayoutPreferences LayoutPreference { get; set; }

        private class LayoutPreferences
        {
            public SortOption DirectorySortOption;
            public SortDirection DirectorySortDirection;
            public LayoutModes LayoutMode;
            public int GridViewSize;

            public static LayoutPreferences DefaultLayoutPreferences => new LayoutPreferences();

            public LayoutPreferences()
            {
                this.LayoutMode = App.AppSettings.DefaultLayoutMode;
                this.GridViewSize = App.AppSettings.DefaultGridViewSize;
                this.DirectorySortOption = App.AppSettings.DefaultDirectorySortOption;
                this.DirectorySortDirection = App.AppSettings.DefaultDirectorySortDirection;
            }

            public static LayoutPreferences FromCompositeValue(ApplicationDataCompositeValue compositeValue)
            {
                return new LayoutPreferences
                {
                    LayoutMode = (LayoutModes)(int)compositeValue[nameof(LayoutMode)],
                    GridViewSize = (int)compositeValue[nameof(GridViewSize)],
                    DirectorySortOption = (SortOption)(int)compositeValue[nameof(DirectorySortOption)],
                    DirectorySortDirection = (SortDirection)(int)compositeValue[nameof(DirectorySortDirection)]
                };
            }

            public ApplicationDataCompositeValue ToCompositeValue()
            {
                return new ApplicationDataCompositeValue()
                {
                    { nameof(LayoutMode), (int)this.LayoutMode },
                    { nameof(GridViewSize), this.GridViewSize },
                    { nameof(DirectorySortOption), (int)this.DirectorySortOption },
                    { nameof(DirectorySortDirection), (int)this.DirectorySortDirection },
                };
            }

            public override bool Equals(object obj)
            {
                if (obj == null)
                {
                    return false;
                }
                if (obj == this)
                {
                    return true;
                }
                if (obj is LayoutPreferences prefs)
                {
                    return (
                        prefs.LayoutMode == this.LayoutMode &&
                        prefs.GridViewSize == this.GridViewSize &&
                        prefs.DirectorySortOption == this.DirectorySortOption &&
                        prefs.DirectorySortDirection == this.DirectorySortDirection);
                }
                return base.Equals(obj);
            }

            public override int GetHashCode()
            {
                return base.GetHashCode();
            }
        }
    }
}
