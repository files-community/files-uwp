using Files.Common;
using Microsoft.Toolkit.Uwp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Xaml.Controls;

namespace Files.Helpers
{
    public class RecycleBinHelpers
    {
        #region Private Members

        private static readonly Regex recycleBinPathRegex = new Regex(@"\w:\\\$Recycle\.Bin\\.*", RegexOptions.IgnoreCase);

        private Task<NamedPipeAsAppServiceConnection> ServiceConnection => AppServiceConnectionHelper.Instance;

        #endregion Private Members

        public async Task<List<ShellFileItem>> EnumerateRecycleBin()
        {
            var connection = await ServiceConnection;
            if (connection != null)
            {
                ValueSet value = new ValueSet()
                {
                    { "Arguments", "ShellFolder" },
                    { "action", "Enumerate" },
                    { "folder", App.AppSettings.RecycleBinPath }
                };
                var (status, response) = await connection.SendMessageForResponseAsync(value);

                if (status == AppServiceResponseStatus.Success
                    && response.ContainsKey("Enumerate"))
                {
                    List<ShellFileItem> items = JsonConvert.DeserializeObject<List<ShellFileItem>>((string)response["Enumerate"]);
                    return items;
                }
            }

            return new List<ShellFileItem>();
        }

        public async Task<bool> IsRecycleBinItem(IStorageItem item)
        {
            List<ShellFileItem> recycleBinItems = await EnumerateRecycleBin();
            return recycleBinItems.Any((shellItem) => shellItem.RecyclePath == item.Path);
        }

        public async Task<bool> IsRecycleBinItem(string path)
        {
            List<ShellFileItem> recycleBinItems = await EnumerateRecycleBin();
            return recycleBinItems.Any((shellItem) => shellItem.RecyclePath == path);
        }

        public bool IsPathUnderRecycleBin(string path)
        {
            return recycleBinPathRegex.IsMatch(path);
        }

        public static async Task S_EmptyRecycleBin()
        {
            await new RecycleBinHelpers().EmptyRecycleBin();
        }

        public async Task EmptyRecycleBin()
        {
            var ConfirmEmptyBinDialog = new ContentDialog()
            {
                Title = "ConfirmEmptyBinDialogTitle".GetLocalized(),
                Content = "ConfirmEmptyBinDialogContent".GetLocalized(),
                PrimaryButtonText = "ConfirmEmptyBinDialog/PrimaryButtonText".GetLocalized(),
                SecondaryButtonText = "ConfirmEmptyBinDialog/SecondaryButtonText".GetLocalized()
            };

            ContentDialogResult result = await ConfirmEmptyBinDialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var connection = await ServiceConnection;
                if (connection != null)
                {
                    var value = new ValueSet()
                    {
                        { "Arguments", "RecycleBin" },
                        { "action", "Empty" }
                    };

                    // Send request to fulltrust process to empty Recycle Bin
                    await connection.SendMessageAsync(value);
                }
            }
        }
    }
}