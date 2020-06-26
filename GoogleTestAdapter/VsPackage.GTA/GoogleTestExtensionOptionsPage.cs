﻿// This file has been modified by Microsoft on 6/2020.

using GoogleTestAdapter.Settings;
using GoogleTestAdapter.TestAdapter.Settings;
using GoogleTestAdapter.VsPackage.Commands;
using GoogleTestAdapter.VsPackage.Debugging;
using GoogleTestAdapter.VsPackage.Helpers;
using GoogleTestAdapter.VsPackage.OptionsPages;
using GoogleTestAdapter.VsPackage.ReleaseNotes;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.AsyncPackageHelpers;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.Threading;

namespace GoogleTestAdapter.VsPackage
{
    [InstalledProductRegistration("#110", "#112", "1.0", IconResourceID = 400)] // Info on this package for Help/About
    [Guid(PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    [ProvideOptionPage(typeof(GeneralOptionsDialogPage), OptionsCategoryName, "General", 110, 501, true)]
    [ProvideOptionPage(typeof(ParallelizationOptionsDialogPage), OptionsCategoryName, "Parallelization", 110, 502, true)]
    [ProvideOptionPage(typeof(GoogleTestOptionsDialogPage), OptionsCategoryName, "Google Test", 110, 503, true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [AsyncPackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Microsoft.VisualStudio.AsyncPackageHelpers.ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Microsoft.VisualStudio.Shell.ProvideAutoLoad(UIContextGuids.SolutionExists)]
    public partial class GoogleTestExtensionOptionsPage : Package, IAsyncLoadablePackageInitialize, IGoogleTestExtensionOptionsPage, IDisposable
    {
        private const string PackageGuidString = "e7c90fcb-0943-4908-9ae8-3b6a9d22ec9e";
        private const string OptionsCategoryName = "Google Test Adapter";
        private bool _isAsyncLoadSupported;

        protected override void Initialize()
        {
            base.Initialize();

            _isAsyncLoadSupported = this.IsAsyncPackageSupported();
            if (!_isAsyncLoadSupported)
            {
                var componentModel = (IComponentModel)GetGlobalService(typeof(SComponentModel));
                _globalRunSettings = componentModel.GetService<IGlobalRunSettingsInternal>();
                DoInitialize();
            }
        }

        IVsTask IAsyncLoadablePackageInitialize.Initialize(IAsyncServiceProvider serviceProvider, IProfferAsyncService profferService,
            IAsyncProgressCallback progressCallback)
        {
            if (!_isAsyncLoadSupported)
            {
                throw new InvalidOperationException("Async Initialize method should not be called when async load is not supported.");
            }

            return ThreadHelper.JoinableTaskFactory.RunAsync<object>(async () =>
            {
                var componentModel = await serviceProvider.GetServiceAsync<IComponentModel>(typeof(SComponentModel));
                _globalRunSettings = componentModel.GetService<IGlobalRunSettingsInternal>();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                DoInitialize();

                return null;
            }).AsVsTask();
        }

        private void DisplayReleaseNotesIfNecessary()
        {
            var thread = new System.Threading.Thread(DisplayReleaseNotesIfNecessaryProc);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void DisplayReleaseNotesIfNecessaryProc()
        {
            var versionProvider = new VersionProvider(this);

            Version formerlyInstalledVersion = versionProvider.FormerlyInstalledVersion;
            Version currentVersion = versionProvider.CurrentVersion;

            versionProvider.UpdateLastVersion();

            if (!_generalOptions.ShowReleaseNotes
                || (formerlyInstalledVersion != null && formerlyInstalledVersion >= currentVersion))
                return;

            var creator = new ReleaseNotesCreator(formerlyInstalledVersion, currentVersion);
            DisplayReleaseNotes(creator.CreateHtml());
        }

        private void DisplayReleaseNotes(string html)
        {
            string htmlFileBase = Path.GetTempFileName();
            string htmlFile = Path.ChangeExtension(htmlFileBase, "html");
            File.Delete(htmlFileBase);

            File.WriteAllText(htmlFile, html);

            using (var dialog = new ReleaseNotesDialog { HtmlFile = new Uri($"file://{htmlFile}") })
            {
                dialog.ShowReleaseNotesChanged +=
                    (sender, args) => _generalOptions.ShowReleaseNotes = args.ShowReleaseNotes;
                dialog.Closed += (sender, args) => File.Delete(htmlFile);
                dialog.ShowDialog();
            }
        }

        private bool ShowReleaseNotes
        {
            get { return _generalOptions.ShowReleaseNotes; }
        }

        // Shared content

        private readonly string _debuggingNamedPipeId = Guid.NewGuid().ToString();

        private IGlobalRunSettingsInternal _globalRunSettings;

        private GeneralOptionsDialogPage _generalOptions;
        private ParallelizationOptionsDialogPage _parallelizationOptions;
        private GoogleTestOptionsDialogPage _googleTestOptions;

        // ReSharper disable once NotAccessedField.Local
        private DebuggerAttacherServiceHost _debuggerAttacherServiceHost;

        private void DoInitialize()
        {
            InitializeOptions();
            InitializeCommands();
            InitializeDebuggerAttacherService();
            DisplayReleaseNotesIfNecessary();
        }

        private void InitializeOptions()
        {
            _generalOptions = (GeneralOptionsDialogPage)GetDialogPage(typeof(GeneralOptionsDialogPage));
            _parallelizationOptions =
                (ParallelizationOptionsDialogPage)GetDialogPage(typeof(ParallelizationOptionsDialogPage));
            _googleTestOptions = (GoogleTestOptionsDialogPage)GetDialogPage(typeof(GoogleTestOptionsDialogPage));

            _globalRunSettings.RunSettings = GetRunSettingsFromOptionPages();

            _generalOptions.PropertyChanged += OptionsChanged;
            _parallelizationOptions.PropertyChanged += OptionsChanged;
            _googleTestOptions.PropertyChanged += OptionsChanged;
        }

        private void InitializeCommands()
        {
            SwitchCatchExceptionsOptionCommand.Initialize(this);
            SwitchBreakOnFailureOptionCommand.Initialize(this);
            SwitchParallelExecutionOptionCommand.Initialize(this);
            SwitchPrintTestOutputOptionCommand.Initialize(this);
        }

        private void InitializeDebuggerAttacherService()
        {
            var logger = new ActivityLogLogger(this, () => _generalOptions.DebugMode);
            var debuggerAttacher = new VsDebuggerAttacher(this);
            _debuggerAttacherServiceHost = new DebuggerAttacherServiceHost(_debuggingNamedPipeId, debuggerAttacher, logger);
            try
            {
                _debuggerAttacherServiceHost.Open();
            }
            catch (CommunicationException)
            {
                _debuggerAttacherServiceHost.Abort();
                _debuggerAttacherServiceHost = null;
            }
        }

        public IVsActivityLog GetActivityLog()
        {
            return GetService(typeof(SVsActivityLog)) as IVsActivityLog;
        }


        public void Dispose()
        {
            Dispose(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _generalOptions?.Dispose();
                _parallelizationOptions?.Dispose();
                _googleTestOptions?.Dispose();

                try
                {
                    _debuggerAttacherServiceHost?.Close();
                }
                catch (CommunicationException)
                {
                    _debuggerAttacherServiceHost?.Abort();
                }
            }
            base.Dispose(disposing);
        }

        public bool CatchExtensions
        {
            get { return _googleTestOptions.CatchExceptions; }
            set
            {
                _googleTestOptions.CatchExceptions = value;
                RefreshVsUi();
            }
        }

        public bool BreakOnFailure
        {
            get { return _googleTestOptions.BreakOnFailure; }
            set
            {
                _googleTestOptions.BreakOnFailure = value;
                RefreshVsUi();
            }
        }

        public bool PrintTestOutput
        {
            get { return _generalOptions.PrintTestOutput; }
            set
            {
                _generalOptions.PrintTestOutput = value;
                RefreshVsUi();
            }
        }

        public bool ParallelTestExecution
        {
            get { return _parallelizationOptions.EnableParallelTestExecution; }
            set
            {
                _parallelizationOptions.EnableParallelTestExecution = value;
                RefreshVsUi();
            }
        }

        private void RefreshVsUi()
        {
            var vsShell = (IVsUIShell)GetService(typeof(IVsUIShell));
            if (vsShell != null)
            {
                int hr = vsShell.UpdateCommandUI(Convert.ToInt32(false));
                ErrorHandler.ThrowOnFailure(hr);
            }
        }

        private void OptionsChanged(object sender, PropertyChangedEventArgs e)
        {
            _globalRunSettings.RunSettings = GetRunSettingsFromOptionPages();
        }

        private RunSettings GetRunSettingsFromOptionPages()
        {
            return new RunSettings
            {
                PrintTestOutput = _generalOptions.PrintTestOutput,
                TestDiscoveryRegex = _generalOptions.TestDiscoveryRegex,
                TestDiscoveryTimeoutInSeconds = _generalOptions.TestDiscoveryTimeoutInSeconds,
                WorkingDir = _generalOptions.WorkingDir,
                PathExtension = _generalOptions.PathExtension,
                TraitsRegexesBefore = _generalOptions.TraitsRegexesBefore,
                TraitsRegexesAfter = _generalOptions.TraitsRegexesAfter,
                TestNameSeparator = _generalOptions.TestNameSeparator,
                ParseSymbolInformation = _generalOptions.ParseSymbolInformation,
                DebugMode = _generalOptions.DebugMode,
                TimestampOutput = _generalOptions.TimestampOutput,
                ShowReleaseNotes = ShowReleaseNotes,
                AdditionalTestExecutionParam = _generalOptions.AdditionalTestExecutionParams,
                BatchForTestSetup = _generalOptions.BatchForTestSetup,
                BatchForTestTeardown = _generalOptions.BatchForTestTeardown,
                KillProcessesOnCancel = _generalOptions.KillProcessesOnCancel,

                CatchExceptions = _googleTestOptions.CatchExceptions,
                BreakOnFailure = _googleTestOptions.BreakOnFailure,
                RunDisabledTests = _googleTestOptions.RunDisabledTests,
                NrOfTestRepetitions = _googleTestOptions.NrOfTestRepetitions,
                ShuffleTests = _googleTestOptions.ShuffleTests,
                ShuffleTestsSeed = _googleTestOptions.ShuffleTestsSeed,

                ParallelTestExecution = _parallelizationOptions.EnableParallelTestExecution,
                MaxNrOfThreads = _parallelizationOptions.MaxNrOfThreads,

                UseNewTestExecutionFramework = _generalOptions.UseNewTestExecutionFramework2,

                DebuggingNamedPipeId = _debuggingNamedPipeId
            };
        }
    }
}
