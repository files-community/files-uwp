using Microsoft.Toolkit.Uwp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Services.Store;
using Windows.UI.Xaml.Controls;

namespace Files.Helpers
{
    internal class AppUpdater
    {
        private StoreContext context;

        public AppUpdater()
        {
        }

        public async void CheckForUpdatesAsync(bool mandatoryOnly = true)
        {
            try
            {
                if (context == null)
                {
                    context = await Task.Run(() => StoreContext.GetDefault());
                }

                var updateList = await context.GetAppAndOptionalStorePackageUpdatesAsync();

                if (mandatoryOnly)
                {
                    updateList = updateList.Where(e => e.Mandatory).ToList();
                }

                if (updateList.Count > 0)
                {
                    if (await DownloadUpdatesConsent())
                    {
                        await DownloadUpdates(updateList);
                    }
                }
            }
            catch (Exception ex)
            {
#if !DEBUG
                App.Logger.Warn(ex, "Could not fetch updates.");
#endif
            }
        }

        private async Task<bool> DownloadUpdatesConsent()
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = "ConsentDialogTitle".GetLocalized(),
                Content = "ConsentDialogContent".GetLocalized(),
                CloseButtonText = "ConsentDialogCloseButtonText".GetLocalized(),
                PrimaryButtonText = "ConsentDialogPrimaryButtonText".GetLocalized()
            };
            ContentDialogResult result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                return true;
            }
            return false;
        }

        private async Task<StorePackageUpdateResult> DownloadUpdates(IReadOnlyList<StorePackageUpdate> updateList)
        {
            if (updateList == null || updateList.Count < 1)
            {
                return null;
            }

            if (context == null)
            {
                context = await Task.Run(() => StoreContext.GetDefault());
            }

            var downloadOperation = context.RequestDownloadAndInstallStorePackageUpdatesAsync(updateList);
            return await downloadOperation.AsTask();
        }
    }
}