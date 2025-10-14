using Microsoft.Extensions.DependencyInjection;
using OpenTK.Mathematics;
using System.IO;
using System.Windows;
using UnBox3D.Controls;
using UnBox3D.Controls.States;
using UnBox3D.Models;
using UnBox3D.Rendering;
using UnBox3D.Rendering.OpenGL;
using UnBox3D.Utils;
using UnBox3D.ViewModels;
using UnBox3D.Views;
using UnBox3D.Theming;
using Application = System.Windows.Application;

namespace UnBox3D
{
    public partial class App : Application
    {
        private static ServiceProvider? _serviceProvider;
        public static ServiceProvider Services => _serviceProvider!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _serviceProvider = ConfigureServices();

            // Apply saved theme BEFORE any windows show
            var themeManager = _serviceProvider.GetRequiredService<IThemeManager>();
            themeManager.ApplySavedTheme(false);

            // Show main menu (DI-created)
            var menuWindow = _serviceProvider.GetRequiredService<MainMenuWindow>();
            Current.MainWindow = menuWindow;
            menuWindow.Show();
        }

        private ServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            #region Core Utilities Registration
            services.AddSingleton<IFileSystem, FileSystem>();
            services.AddSingleton<ILogger, Logger>(provider =>
            {
                var fileSystem = provider.GetRequiredService<IFileSystem>();
                return new Logger(
                    fileSystem,
                    logDirectory: @"C:\\ProgramData\\UnBox3D\\Logs",
                    logFileName: "UnBox3D.log"
                );
            });

            services.AddSingleton<ICommandHistory, CommandHistory>();
            services.AddSingleton<IState, DefaultState>(provider =>
            {
                var sceneManager = provider.GetRequiredService<ISceneManager>();
                var glHost = provider.GetRequiredService<IGLControlHost>();
                var camera = provider.GetRequiredService<ICamera>();
                var rayCaster = provider.GetRequiredService<IRayCaster>();
                return new DefaultState(sceneManager, glHost, camera, rayCaster);
            });
            services.AddSingleton<ISettingsManager, SettingsManager>();
            services.AddSingleton<IThemeManager, ThemeManager>();
            #endregion

            #region Rendering Services Registration
            services.AddSingleton<ISceneManager, SceneManager>();
            services.AddSingleton<IRayCaster, RayCaster>();
            services.AddSingleton<ICamera, Camera>(provider =>
            {
                Vector3 defaultPos = new Vector3(0, 0, 0);
                float defaultAspectRatio = 16f / 9f;
                return new Camera(defaultPos, defaultAspectRatio);
            });
            services.AddSingleton<IRenderer, SceneRenderer>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger>();
                var settings = provider.GetRequiredService<ISettingsManager>();
                var sceneManager = provider.GetRequiredService<ISceneManager>();
                return new SceneRenderer(logger, settings, sceneManager);
            });
            services.AddSingleton<GLControlHost>(provider =>
            {
                var sceneManager = provider.GetRequiredService<ISceneManager>();
                var sceneRenderer = provider.GetRequiredService<IRenderer>();
                var settingsManager = provider.GetRequiredService<ISettingsManager>();
                return new GLControlHost(sceneManager, sceneRenderer, settingsManager);
            });
            services.AddSingleton<IGLControlHost>(provider => provider.GetRequiredService<GLControlHost>());
            #endregion

            #region UI and ViewModel Registration
            services.AddSingleton<IBlenderInstaller, BlenderInstaller>(provider =>
            {
                var fileSystem = provider.GetRequiredService<IFileSystem>();
                return new BlenderInstaller(fileSystem);
            });
            services.AddSingleton<ModelExporter>(provider => {
                var settingsManager = provider.GetRequiredService<ISettingsManager>();
                return new ModelExporter(settingsManager);
            });

            services.AddSingleton<MouseController>(provider =>
            {
                var settingsManger = provider.GetRequiredService<ISettingsManager>();
                var camera = provider.GetRequiredService<ICamera>();
                var neutralState = provider.GetRequiredService<IState>();
                var rayCaster = provider.GetRequiredService<IRayCaster>();
                var glControlHost = provider.GetRequiredService<GLControlHost>();
                return new MouseController(settingsManger, camera, neutralState, rayCaster, glControlHost);
            });

            services.AddSingleton<BlenderIntegration>();
            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainViewModel>(provider =>
            {
                var logger = provider.GetRequiredService<ILogger>();
                var settings = provider.GetRequiredService<ISettingsManager>();
                var sceneManager = provider.GetRequiredService<ISceneManager>();
                var fileSystem = provider.GetRequiredService<IFileSystem>();
                var blenderIntegration = provider.GetRequiredService<BlenderIntegration>();
                var blenderInstaller = provider.GetRequiredService<IBlenderInstaller>();
                var modelExporter = provider.GetRequiredService<ModelExporter>();
                var mouseController = provider.GetRequiredService<MouseController>();
                var camera = provider.GetRequiredService<ICamera>();
                var glControlHost = provider.GetRequiredService<IGLControlHost>();
                var commandHistory = provider.GetRequiredService<ICommandHistory>();
                return new MainViewModel(logger, settings, sceneManager, fileSystem, blenderIntegration, blenderInstaller, modelExporter, mouseController, glControlHost, camera, commandHistory);
            });

            // DI for SettingsWindow: call Initialize before returning the instance
            services.AddTransient<SettingsWindow>(provider =>
            {
                var w = new SettingsWindow();
                var logger = provider.GetRequiredService<ILogger>();
                var settingsMgr = provider.GetRequiredService<ISettingsManager>();
                w.Initialize(logger, settingsMgr);
                return w;
            });

            // Register MainMenuWindow and pass the ServiceProvider to it
            services.AddSingleton<MainMenuWindow>(provider => new MainMenuWindow(provider));
            #endregion

            return services.BuildServiceProvider();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            var settingsManager = _serviceProvider?.GetRequiredService<ISettingsManager>();
            if (settingsManager != null)
            {
                bool cleanupOnExit = settingsManager.GetSetting<bool>(
                    new AppSettings().GetKey(),
                    AppSettings.CleanupExportOnExit
                );

                if (cleanupOnExit)
                {
                    string? exportDir = settingsManager.GetSetting<string>(
                        new AppSettings().GetKey(),
                        AppSettings.ExportDirectory
                    );

                    if (string.IsNullOrWhiteSpace(exportDir) || !Directory.Exists(exportDir))
                    {
                        exportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Export");
                    }

                    try
                    {
                        foreach (var file in Directory.GetFiles(exportDir, "*.obj"))
                        {
                            File.Delete(file);
                        }
                        foreach (var file in Directory.GetFiles(exportDir, "*.mtl"))
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to clean up export directory: {ex.Message}");
                    }
                }
            }
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}
