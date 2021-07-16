using Files.Extensions;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Collections;

namespace Files.Helpers
{
    public static class Win32Helpers
    {
        public static async Task InvokeWin32ComponentAsync(string applicationPath, IShellPage associatedInstance, string arguments = null, bool runAsAdmin = false, string workingDirectory = null)
        {
            await InvokeWin32ComponentsAsync(applicationPath.CreateEnumerable(), associatedInstance, arguments, runAsAdmin, workingDirectory);
        }

        public static async Task InvokeWin32ComponentsAsync(IEnumerable<string> applicationPaths, IShellPage associatedInstance, string arguments = null, bool runAsAdmin = false, string workingDirectory = null)
        {
            Debug.WriteLine("Launching EXE in FullTrustProcess");

            if (string.IsNullOrEmpty(workingDirectory))
            {
                workingDirectory = associatedInstance.FilesystemViewModel.WorkingDirectory;
            }

            var connection = await AppServiceConnectionHelper.Instance;
            if (connection != null)
            {
                var value = new ValueSet()
                {
                    { "Arguments", "LaunchApp" },
                    { "WorkingDirectory", string.IsNullOrEmpty(workingDirectory) ? associatedInstance?.FilesystemViewModel?.WorkingDirectory : workingDirectory },
                    { "Application", applicationPaths.FirstOrDefault() },
                    { "ApplicationList", JsonConvert.SerializeObject(applicationPaths) },
                };

                if (runAsAdmin)
                {
                    value.Add("Parameters", "runas");
                }
                else
                {
                    value.Add("Parameters", arguments);
                }

                await connection.SendMessageAsync(value);
            }
        }
    }
}