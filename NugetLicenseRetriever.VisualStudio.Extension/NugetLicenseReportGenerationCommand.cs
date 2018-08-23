﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Newtonsoft.Json;
using System.Linq;
using NugetLicenseRetriever.Lib;
using NuGet.Common;
using NuGet.VisualStudio;
using Task = System.Threading.Tasks.Task;

namespace NugetLicenseRetriever.VisualStudio.Extension
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class NugetLicenseReportGenerationCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;
       
        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("e0cc967d-c96e-4866-8a6a-ee881cd83bb3");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="NugetLicenseReportGenerationCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private NugetLicenseReportGenerationCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandId = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        private void UpdateLicenseCache(Dictionary<string, LicenseRow> dictionary, string licenseCacheFileName)
        {
            //json is to large to fit in settings store so store file path in the store then save the json file.
            var json = JsonConvert.SerializeObject(dictionary, Formatting.Indented);
            System.IO.File.WriteAllText(licenseCacheFileName, json);
        }

        private Dictionary<string, LicenseRow> GetLicenseCache(string licenseCacheFileName)
        {
            var fi = new FileInfo(licenseCacheFileName);
            if (fi.Exists)
            {
                var json = System.IO.File.ReadAllText(fi.FullName);
                return JsonConvert.DeserializeObject<Dictionary<string, LicenseRow>>(json);
            }
            
            return new Dictionary<string, LicenseRow>();
        }


        private void UpdateSpdxLicenseCache(SpdxLicenseData spdxLicenseData, string spdxCacheFileName)
        {
            //json is to large to fit in settings store so store file path in the store then save the json file.
            var json = JsonConvert.SerializeObject(spdxLicenseData,Formatting.Indented);
            System.IO.File.WriteAllText(spdxCacheFileName, json);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static NugetLicenseReportGenerationCommand Instance
        {
            get;
            private set;
        }

        private async Task<ShellSettingsManager> GetSettingsManagerAsync()
        {
#pragma warning disable VSTHRD010 
            // False-positive in Threading Analyzers. Bug tracked here https://github.com/Microsoft/vs-threading/issues/230
            var svc = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsSettingsManager)) as IVsSettingsManager;
#pragma warning restore VSTHRD010 

            return new ShellSettingsManager(svc);
        }

        private static async Task<IVsActivityLog> GetActivityLoggerAsync()
        {
            return await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsActivityLog)) as IVsActivityLog;
        }

        private async Task<IVsPackageInstallerServices> GetIVsPackageInstallerServicesAsync()
        {
            var componentModel = this.package.GetServiceAsync(typeof(SComponentModel)).Result as IComponentModel;
            return componentModel?.GetService<IVsPackageInstallerServices>();
        }

        private async Task<EnvDTE.DTE> GetEnvAsync()
        {
            return this.package.GetServiceAsync(typeof(EnvDTE.DTE)).Result as EnvDTE.DTE;
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Verify the current thread is the UI thread - the call to AddCommand in NugetCommand's constructor requires
            // the UI thread.
            ThreadHelper.ThrowIfNotOnUIThread();

            OleMenuCommandService commandService = await package.GetServiceAsync((typeof(IMenuCommandService))) as OleMenuCommandService;
            Instance = new NugetLicenseReportGenerationCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private async void Execute(object sender, EventArgs e)
        {
            await GenerateReportAsync();
        }

        private Task GenerateReportAsync()
        {
            var log = GetActivityLoggerAsync().Result;

            try
            {
                var settingsManager = GetSettingsManagerAsync().Result;
                var userSettingsStore = settingsManager.GetWritableSettingsStore(SettingsScope.UserSettings);
                var logger = NullLogger.Instance;
                var spdxHelper = new SpdxLicenseHelper(logger);
                var env = GetEnvAsync().Result;
                var licenseCache = GetLicenseCache(ProjectSettings.LicenseCacheFileName);
                var installerServices = GetIVsPackageInstallerServicesAsync().Result;
                ReportGeneratorOptions reportOptions;

                if (userSettingsStore.CollectionExists(ProjectSettings.CollectionName) &&
                    userSettingsStore.PropertyExists(ProjectSettings.CollectionName,
                        ProjectSettings.ReportGenerationOptionsDataKey))
                {
                    reportOptions = JsonConvert.DeserializeObject<ReportGeneratorOptions>(
                        userSettingsStore.GetString(ProjectSettings.CollectionName, ProjectSettings.ReportGenerationOptionsDataKey)
                    );

                    reportOptions.Path = ProjectSettings.ReportFileName;
                }
                else
                {
                    reportOptions = new ReportGeneratorOptions
                    {
                        Path = ProjectSettings.ReportFileName,
                        Columns = typeof(LicenseRow).GetProperties().Select(p => p.Name).ToList(),
                        FileType = FileType.Csv
                    };
                }

                var reportGenerator = new ReportGenerator(reportOptions);

                //Remove old report
                reportGenerator.RemoveReport();

                //Get nuget packages
                var helper = new NugetHelper(logger);
                //Todo: add more file types support.
                var nugetPackageProjectDictionary = helper.GetNugetPackageProjectDictionary(installerServices, env?.Solution);

                //First check if any NuGet packages are installed.
                if (nugetPackageProjectDictionary != null && nugetPackageProjectDictionary.Any())
                {
                    //Get spdx licenses
                    SpdxLicenseData spdxLicenseData;

                    var fi = new FileInfo(ProjectSettings.SpdxCacheFileName);
                    if (fi.Exists)
                    {
                        var json = File.ReadAllText(ProjectSettings.SpdxCacheFileName);
                        var cachedSpdxLicenseData = JsonConvert.DeserializeObject<SpdxLicenseData>(json);
                        spdxLicenseData = spdxHelper.GetLicencesAsync(false);

                        if (cachedSpdxLicenseData.Version < spdxLicenseData.Version)
                        {
                            spdxLicenseData = spdxHelper.GetLicencesAsync(true);
                            UpdateSpdxLicenseCache(spdxLicenseData, ProjectSettings.SpdxCacheFileName);
                        }
                    }
                    else
                    {
                        //Query data. 
                        //Todo: this will take a while so do something to alert user.
                        spdxLicenseData = spdxHelper.GetLicencesAsync(true);
                        UpdateSpdxLicenseCache(spdxLicenseData, ProjectSettings.SpdxCacheFileName);
                    }

                    reportGenerator.Generate(nugetPackageProjectDictionary, licenseCache, spdxLicenseData);

                    UpdateLicenseCache(licenseCache, ProjectSettings.LicenseCacheFileName);
                }
                else
                {
                    Debug.WriteLine("No installed Nuget packages.");
                }
            }
            catch (NuGet.Protocol.Core.Types.FatalProtocolException exception)
            {
                Debug.Write(exception.Message);
#pragma warning disable VSTHRD010 // Invoke single-threaded types on Main thread
                log.LogEntry(
#pragma warning restore VSTHRD010 // Invoke single-threaded types on Main thread
                    (UInt32)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                    this.ToString(),
                    exception.Message + " This may be caused by not doing a restore on your Nuget packages."
                );

                throw;
            }
            catch (Exception exception)
            {
                Debug.Write(exception.Message);
#pragma warning disable VSTHRD010 // Invoke single-threaded types on Main thread
                log.LogEntry(
#pragma warning restore VSTHRD010 // Invoke single-threaded types on Main thread
                    (UInt32)__ACTIVITYLOG_ENTRYTYPE.ALE_ERROR,
                    this.ToString(),
                    exception.Message
                );

                throw;
            }

            ThreadHelper.ThrowIfNotOnUIThread();
            string message = "Nuget Package License Report Generated";
            string title = "Nuget License Report";

            // Show a message box to prove we were here
            VsShellUtilities.ShowMessageBox(
                this.package,
                message,
                title,
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            return Task.CompletedTask;
        }
    }
}
