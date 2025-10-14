﻿using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using UnBox3D.Models;
using UnBox3D.Utils;
using UnBox3D.Rendering;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UnBox3D.Views;
using UnBox3D.Controls;
using UnBox3D.Rendering.OpenGL;
using UnBox3D.Commands;
using OpenTK.Mathematics;
using PdfSharpCore.Pdf;

namespace UnBox3D.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        #region Fields & Properties

        private readonly ILogger _logger;
        private readonly ISettingsManager _settingsManager;
        private readonly ISceneManager _sceneManager;
        private readonly IGLControlHost _glControlHost;
        private readonly ModelImporter _modelImporter;
        private readonly MouseController _mouseController;
        private readonly ICamera _camera;
        private readonly IFileSystem _fileSystem;
        private readonly BlenderIntegration _blenderIntegration;
        private readonly IBlenderInstaller _blenderInstaller;
        private readonly ModelExporter _modelExporter;
        private readonly ICommandHistory _commandHistory;
        private string _importedFilePath; // Global filepath that should be referenced when simplifying
        private List<IAppMesh> _latestImportedModel; // This is so we can keep track of the original model when playing around with small mesh thresholds.

        [ObservableProperty]
        private IAppMesh selectedMesh;

        [ObservableProperty]
        private bool hierarchyVisible = true;

        [ObservableProperty]
        private float pageWidth = 25.0f;

        [ObservableProperty]
        private float pageHeight = 25.0f;

        [ObservableProperty]
        private float simplificationRatio = 50f; // represents percentage (10–100)

        [ObservableProperty]
        private float smallMeshThreshold = 0f;

        public ObservableCollection<MeshSummary> Meshes { get; } = new();


        #endregion

        #region Constructor

        public MainViewModel(ILogger logger, ISettingsManager settingsManager, ISceneManager sceneManager,
            IFileSystem fileSystem, BlenderIntegration blenderIntegration,
            IBlenderInstaller blenderInstaller, ModelExporter modelExporter,
            MouseController mouseController, IGLControlHost glControlHost, ICamera camera, ICommandHistory commandHistory)
        {
            _logger = logger;
            _settingsManager = settingsManager;
            _sceneManager = sceneManager;
            _fileSystem = fileSystem;
            _blenderIntegration = blenderIntegration;
            _blenderInstaller = blenderInstaller;
            _modelImporter = new ModelImporter(_settingsManager);
            _modelExporter = modelExporter;
            _mouseController = mouseController;
            _glControlHost = glControlHost;
            _camera = camera;
            _commandHistory = commandHistory;
        }

        #endregion

        #region Model Import Methods

        [RelayCommand]
        private void ImportObjModel()
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "3D Models (*.obj;)|*.obj;"
            };

            // Show the dialog and check if the result is true
            bool? result = openFileDialog.ShowDialog();
            if (result == true)
            {
                string filePath = openFileDialog.FileName;
                _importedFilePath = EnsureImportDirectory(filePath);
                List<IAppMesh> importedMeshes = _modelImporter.ImportModel(_importedFilePath);

                _latestImportedModel = importedMeshes; // Purpose: Remember the model that was imported so that the user can freely mess with something like size thresholds and go back.

                foreach (var mesh in importedMeshes)
                {
                    _sceneManager.AddMesh(mesh);
                    Meshes.Add(new MeshSummary(mesh));
                }

                if (_modelImporter.WasScaled)
                {
                    var exportPath = _modelExporter.ExportToObj(_sceneManager.GetMeshes().ToList());
                    if (exportPath != null)
                    {
                        _importedFilePath = exportPath;
                    }

                }
            }
        }

        // Don't call this function directly.
        // Reference _importedFilePath if you want access to the ImportedModels directory.
        private string EnsureImportDirectory(string filePath)
        {
            string importDirectory = _fileSystem.CombinePaths(AppDomain.CurrentDomain.BaseDirectory, "ImportedModels");

            if (!_fileSystem.DoesDirectoryExists(importDirectory))
            {
                _fileSystem.CreateDirectory(importDirectory);
            }

            string destinationPath = _fileSystem.CombinePaths(importDirectory, Path.GetFileName(filePath));
            try
            {
                File.Copy(filePath, destinationPath, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("Failed to copy file to ImportedModels directory.", ex);
            }


            return destinationPath;
        }

        #endregion

        #region Unfolding Process Methods

        private async Task ProcessUnfolding(string inputModelPath)
        {
            Debug.WriteLine("Input model is coming from: " + inputModelPath);

            var installWindow = new LoadingWindow
            {
                StatusHint = "Installing Blender...",
                Owner = System.Windows.Application.Current.MainWindow,
                IsProgressIndeterminate = false
            };
            installWindow.Show();

            var installProgress = new Progress<double>(value =>
            {
                installWindow.UpdateProgress(value * 100);
                installWindow.UpdateStatus($"Installing Blender... {Math.Round(value * 100)}%");
            });

            await _blenderInstaller.CheckAndInstallBlender(installProgress);
            installWindow.Close();

            if (!_blenderInstaller.IsBlenderInstalled())
            {
                await ShowWpfMessageBoxAsync(
                    "Blender is required to unfold models but was not found. Please install Blender before proceeding.",
                    "Missing Dependency", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            else
            {
                var loadingWindow = new LoadingWindow
                {
                    StatusHint = "This may take several minutes depending on model complexity",
                    Owner = System.Windows.Application.Current.MainWindow,
                    IsProgressIndeterminate = false
                };
                loadingWindow.Show();

                if (_fileSystem == null || _blenderIntegration == null)
                {
                    loadingWindow.Close();
                    await ShowWpfMessageBoxAsync("Internal error: dependencies not initialized.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (PageWidth == 0 || PageHeight == 0)
                {
                    loadingWindow.Close();
                    await ShowWpfMessageBoxAsync("Page Dimensions cannot be 0.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                using SaveFileDialog saveFileDialog = new()
                {
                    Title = "Save your unfolded file",
                    Filter = "SVG Files|*.svg|PDF Files|*.pdf",
                    FileName = "MyUnfoldedFile"
                };

                if (saveFileDialog.ShowDialog() != DialogResult.OK)
                {
                    loadingWindow.Close();
                    return;
                }

                string filePath = saveFileDialog.FileName;
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                string? userSelectedPath = Path.GetDirectoryName(filePath);

                if (string.IsNullOrEmpty(userSelectedPath))
                {
                    loadingWindow.Close();
                    await ShowWpfMessageBoxAsync("Unable to determine the selected directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string newFileName = Path.GetFileNameWithoutExtension(filePath);
                string format = ext == ".pdf" ? "PDF" : "SVG";

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string outputDirectory = _fileSystem.CombinePaths(baseDir, "UnfoldedOutputs");
                string scriptPath = _fileSystem.CombinePaths(baseDir, "Scripts", "unfolding_script.py");

                if (!_fileSystem.DoesDirectoryExists(outputDirectory))
                {
                    _fileSystem.CreateDirectory(outputDirectory);
                }

                CleanupUnfoldedFolder(outputDirectory);

                double incrementWidth = PageWidth;
                double incrementHeight = PageHeight;
                bool success = false;
                string errorMessage = "";
                int iteration = 0;

                loadingWindow.UpdateStatus("Preparing Blender environment...");
                await DispatcherHelper.DoEvents();

                while (!success)
                {
                    iteration++;
                    loadingWindow.UpdateStatus($"Processing with Blender (Attempt {iteration})...");
                    loadingWindow.UpdateProgress((double)iteration / 100 * 50);
                    await DispatcherHelper.DoEvents();

                    success = await Task.Run(() => _blenderIntegration.RunBlenderScript(
                        inputModelPath, outputDirectory, scriptPath,
                        newFileName, incrementWidth, incrementHeight, "SVG", out errorMessage));

                    if (!success)
                    {
                        if (errorMessage.Contains("continue"))
                        {
                            incrementWidth++;
                            incrementHeight++;
                            loadingWindow.UpdateStatus($"Retrying with new dimensions: {incrementWidth} x {incrementHeight}");
                            await DispatcherHelper.DoEvents();
                        }
                        else
                        {
                            loadingWindow.Close();

                            await loadingWindow.Dispatcher.InvokeAsync(() =>
                            {
                                System.Windows.Application.Current.MainWindow?.Activate();
                                System.Windows.MessageBox.Show(
                                    errorMessage,
                                    "Error Processing File",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error,
                                    MessageBoxResult.OK,
                                    System.Windows.MessageBoxOptions.DefaultDesktopOnly);
                            });

                            return;
                        }
                    }
                }

                loadingWindow.UpdateStatus("Processing unfolded panels...");
                await DispatcherHelper.DoEvents();

                string[] svgPanelFiles = Directory.GetFiles(outputDirectory, "*.svg");
                int totalPanels = svgPanelFiles.Length;

                for (int i = 0; i < totalPanels; i++)
                {
                    string svgFile = svgPanelFiles[i];
                    loadingWindow.UpdateStatus($"Processing panel {i + 1} of {totalPanels}");
                    loadingWindow.UpdateProgress(50 + ((double)i / totalPanels * 30));
                    await DispatcherHelper.DoEvents();

                    await Task.Run(() => SVGEditor.ExportSvgPanels(svgFile, outputDirectory, newFileName, i,
                        PageWidth * 1000f, PageHeight * 1000f));
                }

                loadingWindow.UpdateStatus("Exporting final files...");
                loadingWindow.UpdateProgress(80);
                await DispatcherHelper.DoEvents();

                if (format == "SVG")
                {
                    string[] svgFiles = Directory.GetFiles(outputDirectory, $"{newFileName}*.svg");
                    int fileCount = svgFiles.Length;

                    for (int i = 0; i < fileCount; i++)
                    {
                        string source = svgFiles[i];
                        string suffix = source.Substring(source.IndexOf(newFileName) + newFileName.Length);
                        string destination = Path.Combine(userSelectedPath, newFileName + suffix);

                        loadingWindow.UpdateStatus($"Exporting file {i + 1} of {fileCount}");
                        loadingWindow.UpdateProgress(80 + ((double)i / fileCount * 20));
                        await DispatcherHelper.DoEvents();

                        File.Move(source, destination, overwrite: true); // Optionally wrap in _fileSystem
                    }
                }
                else if (format == "PDF")
                {
                    string pdfFile = Path.Combine(outputDirectory, $"{newFileName}.pdf");

                    string[] svgFiles = Directory.GetFiles(outputDirectory, $"{newFileName}_panel_page*.svg");
                    int fileCount = svgFiles.Length;

                    var pdf = new PdfDocument();
                    bool allSuccessful = true;

                    for (int i = 0; i < fileCount; i++)
                    {
                        string svgFile = svgFiles[i];
                        loadingWindow.UpdateStatus($"Combining file {i + 1} of {fileCount} into PDF...");
                        loadingWindow.UpdateProgress(80 + ((double)i / fileCount * 20));
                        await DispatcherHelper.DoEvents();

                        bool successful = await Task.Run(() => SVGEditor.ExportToPdf(svgFile, pdf));
                        if (!successful)
                        {
                            allSuccessful = false;
                            break;
                        }
                    }

                    if (allSuccessful)
                    {
                        pdf.Save(pdfFile);
                        string destination = Path.Combine(userSelectedPath, $"{newFileName}.pdf");
                        File.Move(pdfFile, destination, overwrite: true);
                    }
                    else
                    {
                        await ShowWpfMessageBoxAsync($"The SVG files are too large to be combined into partitioned panels for PDF. Will instead use the fallback PDF which may result in loss of some panels. Exporting will continue.", "Unable to allocate the required memory", MessageBoxButton.OK, MessageBoxImage.Warning);

                        Debug.WriteLine($"incrW: {incrementWidth} incrH: {incrementHeight}");

                        await Task.Run(() => _blenderIntegration.RunBlenderScript(
                            inputModelPath, outputDirectory, scriptPath,
                            newFileName, incrementWidth, incrementHeight, "PDF", out errorMessage));

                        string destination = Path.Combine(userSelectedPath, $"{newFileName}.pdf");
                        File.Move(pdfFile, destination, overwrite: true);
                    }
                }
                loadingWindow.UpdateStatus("Cleaning up temporary files...");
                loadingWindow.UpdateProgress(100);
                await DispatcherHelper.DoEvents();
                CleanupUnfoldedFolder(outputDirectory);

                loadingWindow.Close();

                await ShowWpfMessageBoxAsync($"{format} file has been exported successfully!",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        private async Task UnfoldMesh(IAppMesh mesh)
        {
            if (mesh == null)
                return;

            // 1. Export the single mesh to a temporary .obj file
            string fileName = $"unfold_temp_{Guid.NewGuid()}.obj";
            string? exportedPath = _modelExporter.ExportToObj(new List<IAppMesh> { mesh }, fileName);

            if (string.IsNullOrWhiteSpace(exportedPath))
            {
                await ShowWpfMessageBoxAsync("Failed to export mesh for unfolding.", "Export Error",
                                             MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 2. Unfold just this mesh using the same logic you already have
            await ProcessUnfolding(exportedPath);
        }


        private void CleanupUnfoldedFolder(string folderPath)
        {
            try
            {
                string[] files = Directory.GetFiles(folderPath);
                foreach (string file in files)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                // Synchronously show an error if cleanup fails.
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Application.Current.MainWindow.Activate();
                    System.Windows.MessageBox.Show(
                        $"An error occurred during cleanup: {ex.Message}",
                        "Cleanup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        MessageBoxResult.OK,
                        System.Windows.MessageBoxOptions.DefaultDesktopOnly);
                });
            }
        }

        #endregion

        #region Relay Commands

        [RelayCommand]
        private async Task ExportUnfoldModel()
        {
            if (string.IsNullOrEmpty(_importedFilePath))
            {
                await ShowWpfMessageBoxAsync("No model imported to unfold.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            await ProcessUnfolding(_importedFilePath);
        }

        [RelayCommand]
        private async void ResetView()
        {
            await ShowWpfMessageBoxAsync("Resetting the view!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private void ToggleHierarchy()
        {
            HierarchyVisible = !HierarchyVisible;
        }

        [RelayCommand]
        private async void About()
        {
            await ShowWpfMessageBoxAsync("UnBox3D - A 3D Model Viewer", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        [RelayCommand]
        private async Task ExportMesh(IAppMesh mesh)
        {
            if (mesh == null)
                return;

            var path = PromptForSaveLocation();
            if (string.IsNullOrWhiteSpace(path))
                return;

            var exported = _modelExporter.ExportToObjAbsolutePath(new List<IAppMesh> { mesh }, path);
            if (exported != null)
            {
                await ShowWpfMessageBoxAsync($"Exported mesh to: {exported}",
                                             "Export Mesh",
                                             MessageBoxButton.OK,
                                             MessageBoxImage.Information);
            }
            else
            {
                await ShowWpfMessageBoxAsync("Failed to export mesh.",
                                             "Export Mesh",
                                             MessageBoxButton.OK,
                                             MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task ClearScene()
        {
            var result = await ShowWpfMessageBoxAsync("Are you sure you want to clear the scene?",
                                                      "Clear Scene",
                                                      MessageBoxButton.YesNo,
                                                      MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                _sceneManager.ClearScene();
                Meshes.Clear();
            }
        }

        [RelayCommand]
        private void DeleteMesh(IAppMesh mesh)
        {
            if (mesh == null)
                return;

            _sceneManager.DeleteMesh(mesh);

            // Dispose of the mesh's unmanaged resources
            if (mesh is IDisposable disposableMesh)
            {
                disposableMesh.Dispose();
            }

            // Remove the corresponding MeshSummary from the UI-bound collection.
            // Assuming Meshes is an ObservableCollection<MeshSummary>
            var summaryToRemove = Meshes.FirstOrDefault(ms => ms.SourceMesh == mesh);
            if (summaryToRemove != null)
            {
                Meshes.Remove(summaryToRemove);
            }
        }

        [RelayCommand]
        private async Task ExportModel()
        {
            // 1. Prompt user for the export path
            string? path = PromptForSaveLocation();
            if (string.IsNullOrEmpty(path))
                return; // user cancelled

            // 2. Export all meshes
            var meshesToExport = _sceneManager.GetMeshes().ToList();
            var savedPath = _modelExporter.ExportToObjAbsolutePath(meshesToExport, path);

            // 3. Notify user of success/failure
            if (savedPath != null)
            {
                await ShowWpfMessageBoxAsync($"Exported all meshes to: {savedPath}",
                                             "Export Meshes",
                                             MessageBoxButton.OK,
                                             MessageBoxImage.Information);
            }
            else
            {
                await ShowWpfMessageBoxAsync("Failed to export meshes.",
                                             "Export Meshes",
                                             MessageBoxButton.OK,
                                             MessageBoxImage.Error);
            }
        }

        #endregion

        #region Mesh Simplification Commands

        [RelayCommand]
        private async void ReplaceSceneWithBoundingBoxes()
        {
            // Value set from UI slider
            float threshold = SmallMeshThreshold;

            // 1. Generate bounding boxes and load them
            List<AppMesh> boxMeshList = _sceneManager.LoadBoundingBoxes();

            // 2. Clear the scene and UI
            _sceneManager.ClearScene();
            Meshes.Clear();

            // 3. Export the generated bounding boxes to a temp .obj file
            string tempFileName = $"bounding_boxes_scene_{Guid.NewGuid()}.obj";
            string? exportedPath = _modelExporter.ExportToObj(boxMeshList.Cast<IAppMesh>().ToList(), tempFileName);

            if (string.IsNullOrWhiteSpace(exportedPath))
            {
                await ShowWpfMessageBoxAsync("Failed to export bounding boxes.", "Export Error",
                                             MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 4. Re-import the exported .obj file to fully reload it into the scene
            var importedMeshes = _modelImporter.ImportModel(exportedPath);
            _importedFilePath = exportedPath;
            foreach (var mesh in importedMeshes)
            {
                _sceneManager.AddMesh(mesh);
                Meshes.Add(new MeshSummary(mesh));
            }
        }

        [RelayCommand]
        private async void ReplaceWithCylinderOption(IAppMesh mesh)
        {
            Vector3 center = _sceneManager.GetMeshCenter(mesh.GetG4Mesh());
            Vector3 meshDimensions = _sceneManager.GetMeshDimensions(mesh.GetG4Mesh());

            bool isXAligned = (meshDimensions.X < meshDimensions.Z);

            float radius = Math.Max(Math.Min(meshDimensions.X, meshDimensions.Z), meshDimensions.Y) / 2;
            float height = isXAligned ? meshDimensions.X : meshDimensions.Z;

            AppMesh cylinder = GeometryGenerator.CreateRotatedCylinder(center, radius, height, 32, Vector3.UnitX);

            var summaryToRemove = Meshes.FirstOrDefault(ms => ms.SourceMesh == mesh);
            if (summaryToRemove != null)
            {
                Meshes.Remove(summaryToRemove);
            }

            _sceneManager.ReplaceMesh(mesh, cylinder);

            Meshes.Add(new MeshSummary(cylinder));

            //await ShowWpfMessageBoxAsync("Replaced Mesh!", "Replace", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // If you want to implement replace with cylinder by clicking
        [RelayCommand]
        private async void ReplaceWithCylinderClick()
        {
            var command = new SetReplaceStateCommand(_glControlHost, _mouseController, _sceneManager, new RayCaster(_glControlHost, _camera), _camera, _commandHistory);
            command.Execute();
            await ShowWpfMessageBoxAsync("Replaced!", "Replace", MessageBoxButton.OK, MessageBoxImage.Information);
        }


        [RelayCommand]
        private async Task SimplifyQEC(IAppMesh mesh)
        {
            await RunPythonSimplificationSingle(mesh, "quadric_edge_collapse");
        }

        [RelayCommand]
        private async Task SimplifyFQD(IAppMesh mesh)
        {
            await RunPythonSimplificationSingle(mesh, "fast_quadric_decimation");
        }

        [RelayCommand]
        private async Task SimplifyVC(IAppMesh mesh)
        {
            await RunPythonSimplificationSingle(mesh, "vertex_clustering");
        }

        [RelayCommand]
        private async Task SimplifyAllQEC()
        {
            await RunPythonSceneSimplification("quadric_edge_collapse");
        }

        [RelayCommand]
        private async Task SimplifyAllFQD()
        {
            await RunPythonSceneSimplification("fast_quadric_decimation");
        }

        [RelayCommand]
        private async Task SimplifyAllVC()
        {
            await RunPythonSceneSimplification("vertex_clustering");
        }

        private async Task RunPythonSceneSimplification(string method)
        {
            if (Meshes.ToList().Count == 0)
            {
                await ShowWpfMessageBoxAsync("No meshes to simplify.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var loadingWindow = new LoadingWindow
            {
                StatusHint = $"Simplifying all meshes… This may take a few moments.",
                Owner = System.Windows.Application.Current.MainWindow,
                IsProgressIndeterminate = true
            };
            loadingWindow.Show();

            if (method == "quadric_edge_collapse")
                loadingWindow.UpdateStatus($"Quadric Edge Collapse Simplification");
            else if (method == "fast_quadric_decimation")
                loadingWindow.UpdateStatus($"Fast Quadric Decimation");
            else if (method == "vertex_clustering")
                loadingWindow.UpdateStatus($"Vertex Clustering Simplification");

            try
            {
                // 1. Export all current meshes to a temp OBJ
                string? exportFile = _modelExporter.ExportToObj(Meshes.Select(m => m.SourceMesh).ToList(), $"scene_to_simplify.obj");
                if (exportFile == null)
                {
                    await ShowWpfMessageBoxAsync("Failed to export current scene.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 2. Prepare paths and arguments
                string simplifiedOutput = Path.Combine(Path.GetTempPath(), $"simplified_scene_{method}.obj");
                string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "simplify.exe");
                float ratio = Math.Clamp(SimplificationRatio, 10, 100) / 100f;

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"\"{exportFile}\" \"{simplifiedOutput}\" \"{method}\" \"{ratio}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync();
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    await ShowWpfMessageBoxAsync($"simplify.exe error:\n{stderr}", "Simplification Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 3. Import simplified mesh
                loadingWindow.UpdateStatus("Importing simplified mesh...");
                var simplifiedMeshes = _modelImporter.ImportModel(simplifiedOutput);
                if (simplifiedMeshes.Count == 0)
                {
                    await ShowWpfMessageBoxAsync("No mesh found in simplified result.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 4. Clear current scene and replace with new simplified mesh
                _sceneManager.ClearScene();
                Meshes.Clear();
                foreach (var simplified in simplifiedMeshes)
                {
                    _sceneManager.AddMesh(simplified);
                    Meshes.Add(new MeshSummary(simplified));
                }
            }
            catch (Exception ex)
            {
                await ShowWpfMessageBoxAsync($"Exception: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                loadingWindow.Close();
            }
        }

        private async Task RunPythonSimplificationSingle(IAppMesh mesh, string method)
        {
            // 1. Export just this single mesh to a temp file
            string tempName = $"temp_singlemesh_{Guid.NewGuid()}.obj";
            var tempFile = _modelExporter.ExportToObj(new List<IAppMesh> { mesh }, tempName);

            if (tempFile == null)
            {
                await ShowWpfMessageBoxAsync("Failed to export single mesh.", "Error",
                                             MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 2. Prepare the output from the simplify.exe
            string baseOutput = Path.Combine(Path.GetTempPath(), $"simplified_{method}.obj");

            // 3. Show a loading window
            var loadingWindow = new LoadingWindow
            {
                StatusHint = $"Simplifying mesh… This may take a while.",
                Owner = System.Windows.Application.Current.MainWindow,
                IsProgressIndeterminate = true
            };
            loadingWindow.Show();

            if (method == "quadric_edge_collapse")
                loadingWindow.UpdateStatus($"Quadric Edge Collapse Simplification");
            else if (method == "fast_quadric_decimation")
                loadingWindow.UpdateStatus($"Fast Quadric Decimation");
            else if (method == "vertex_clustering")
                loadingWindow.UpdateStatus($"Vertex Clustering Simplification");

            try
            {
                var exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "simplify.exe");

                float ratio = Math.Clamp(SimplificationRatio, 10, 100) / 100f;

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"\"{tempFile}\" \"{baseOutput}\" \"{method}\" \"{ratio}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                string stdout = await process.StandardOutput.ReadToEndAsync();
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    await ShowWpfMessageBoxAsync($"simplify.exe error:\n{stderr}",
                                                 "Simplification Failed",
                                                 MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // 5. Re-import the simplified file
                var simplifiedMeshes = _modelImporter.ImportModel(baseOutput);
                if (simplifiedMeshes.Count > 0)
                {
                    var simplified = simplifiedMeshes[0];
                    _sceneManager.ReplaceMesh(mesh, simplified);

                    // 6. Update UI
                    var oldSummary = Meshes.FirstOrDefault(ms => ms.SourceMesh == mesh);
                    if (oldSummary != null)
                    {
                        Meshes.Remove(oldSummary);
                        Meshes.Add(new MeshSummary(simplified));
                    }
                }
            }
            catch (Exception ex)
            {
                await ShowWpfMessageBoxAsync($"Exception: {ex.Message}",
                                             "Error",
                                             MessageBoxButton.OK,
                                             MessageBoxImage.Error);
            }
            finally
            {
                loadingWindow.Close();
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Shows a WPF MessageBox asynchronously using the UI Dispatcher.
        /// </summary>
        private static async Task<MessageBoxResult> ShowWpfMessageBoxAsync(string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Activate main window so the MessageBox appears on top.
                System.Windows.Application.Current.MainWindow.Activate();
                return System.Windows.MessageBox.Show(
                    message,
                    title,
                    button,
                    image,
                    MessageBoxResult.OK,
                    System.Windows.MessageBoxOptions.DefaultDesktopOnly);
            });
        }

        private string? PromptForSaveLocation()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Mesh As .obj",
                Filter = "Wavefront OBJ (*.obj)|*.obj",
                FileName = "export.obj"
            };

            // If user clicks 'Save'
            bool? result = dialog.ShowDialog();
            if (result == true)
            {
                return dialog.FileName;
            }
            return null;
        }
        #endregion

        [RelayCommand]
        private void Exit()
        {
            System.Windows.Application.Current.Shutdown();
        }

        public void ApplyMeshThreshold()
        {
            _sceneManager.RemoveSmallMeshes(_latestImportedModel, SmallMeshThreshold);
            Meshes.Clear();

            var importedMeshes = _sceneManager.GetMeshes();

            foreach (var mesh in importedMeshes)
            {
                Meshes.Add(new MeshSummary(mesh));
            }

        }
    }
}
