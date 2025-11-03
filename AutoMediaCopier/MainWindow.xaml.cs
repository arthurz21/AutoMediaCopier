using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VideoTransferApp
{
    public partial class MainWindow : Window
    {
        private HashSet<string> videoExtensions;
        private HashSet<string> photoExtensions;

        // Transfer tracking variables
        private Stopwatch currentFileStopwatch;
        private Stopwatch totalTransferStopwatch;
        private long currentFileBytesTransferred;
        private long totalBytesTransferred;
        private long totalBytesToTransfer;
        private int currentFileIndex;
        private int totalFilesToTransfer;
        private long currentFileSize;

        // UI update timer
        private DispatcherTimer uiUpdateTimer;

        public MainWindow()
        {
            InitializeComponent();
            LoadMediaExtensions();
            currentFileStopwatch = new Stopwatch();
            totalTransferStopwatch = new Stopwatch();

            // Setup UI update timer - updates every 250ms
            uiUpdateTimer = new DispatcherTimer();
            uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(250);
            uiUpdateTimer.Tick += UpdateTransferUI;
        }

        private void LoadMediaExtensions()
        {
            videoExtensions = new HashSet<string>(
                VideoExtensionsTextBox.Text.Split(',')
                    .Select(ext => ext.Trim().ToLower()),
                StringComparer.OrdinalIgnoreCase
            );

            photoExtensions = new HashSet<string>(
                PhotoExtensionsTextBox.Text.Split(',')
                    .Select(ext => ext.Trim().ToLower()),
                StringComparer.OrdinalIgnoreCase
            );
        }

        private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
        {
            LogMessage("Checking for existing removable drives...");

            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Removable)
                .ToList();

            if (drives.Count == 0)
            {
                LogMessage("No removable drives detected.");
                MessageBox.Show("No removable drives found. Please insert your SD card and USB stick.",
                    "No Drives Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (drives.Count == 1)
            {
                LogMessage($"Only 1 removable drive detected: {drives[0].Name}");
                MessageBox.Show("Only one removable drive found. Please insert both your SD card and USB stick.",
                    "Need Both Drives", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (drives.Count >= 2)
            {
                LogMessage($"Found {drives.Count} removable drives. Processing...");
                await ProcessDevices(drives);
            }
        }


        private async Task ProcessDevices(List<DriveInfo> drives)
        {
            try
            {
                // Find source (SD card with media) and destination (USB stick)
                DriveInfo sourceDrive = null;
                DriveInfo destDrive = null;

                foreach (var drive in drives)
                {
                    var mediaFiles = GetAllMediaFiles(drive.RootDirectory.FullName);
                    if (mediaFiles.Any())
                    {
                        sourceDrive = drive;
                        break;
                    }
                }

                if (sourceDrive == null)
                {
                    LogMessage("No media files found on any removable drive.");
                    return;
                }

                // Use the other drive as destination
                destDrive = drives.FirstOrDefault(d => d.Name != sourceDrive.Name);

                if (destDrive == null)
                {
                    LogMessage("Could not identify destination drive.");
                    return;
                }

                LogMessage($"Source: {sourceDrive.Name} ({sourceDrive.VolumeLabel})");
                LogMessage($"Destination: {destDrive.Name} ({destDrive.VolumeLabel})");

                await TransferLatestMedia(sourceDrive, destDrive);
            }
            catch (Exception ex)
            {
                LogMessage($"Error processing devices: {ex.Message}");
            }
        }

        private List<FileInfo> GetAllMediaFiles(string path)
        {
            var mediaFiles = new List<FileInfo>();

            try
            {
                var directory = new DirectoryInfo(path);
                var allFiles = directory.GetFiles("*.*", SearchOption.AllDirectories);

                foreach (var file in allFiles)
                {
                    bool isVideo = TransferVideosCheckBox.IsChecked == true &&
                                   videoExtensions.Contains(file.Extension.ToLower());
                    bool isPhoto = TransferPhotosCheckBox.IsChecked == true &&
                                   photoExtensions.Contains(file.Extension.ToLower());

                    if (isVideo || isPhoto)
                    {
                        mediaFiles.Add(file);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error scanning {path}: {ex.Message}");
            }

            return mediaFiles;
        }

        private List<FileInfo> GetMediaFilesTimeBased(string path, int timeWindowMinutes)
        {
            var allMediaFiles = GetAllMediaFiles(path);

            if (!allMediaFiles.Any())
                return new List<FileInfo>();

            // Separate videos and photos
            var videos = allMediaFiles.Where(f => videoExtensions.Contains(f.Extension.ToLower())).ToList();
            var photos = allMediaFiles.Where(f => photoExtensions.Contains(f.Extension.ToLower())).ToList();

            var filesToTransfer = new List<FileInfo>();

            // Process videos: find latest video, then get all videos within time window
            if (videos.Any() && TransferVideosCheckBox.IsChecked == true)
            {
                var latestVideo = videos.OrderByDescending(f => f.LastWriteTime).First();
                DateTime videoCutoffTime = latestVideo.LastWriteTime.AddMinutes(-timeWindowMinutes);

                var recentVideos = videos.Where(v => v.LastWriteTime >= videoCutoffTime).ToList();
                filesToTransfer.AddRange(recentVideos);

                LogMessage($"Latest video: {latestVideo.Name} at {latestVideo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                LogMessage($"Video cutoff time: {videoCutoffTime:yyyy-MM-dd HH:mm:ss} (last {timeWindowMinutes} minutes)");
                LogMessage($"Found {recentVideos.Count} video(s) within time window.");
            }

            // Process photos: find latest photo, then get all photos within time window
            if (photos.Any() && TransferPhotosCheckBox.IsChecked == true)
            {
                var latestPhoto = photos.OrderByDescending(f => f.LastWriteTime).First();
                DateTime photoCutoffTime = latestPhoto.LastWriteTime.AddMinutes(-timeWindowMinutes);

                var recentPhotos = photos.Where(p => p.LastWriteTime >= photoCutoffTime).ToList();
                filesToTransfer.AddRange(recentPhotos);

                LogMessage($"Latest photo: {latestPhoto.Name} at {latestPhoto.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                LogMessage($"Photo cutoff time: {photoCutoffTime:yyyy-MM-dd HH:mm:ss} (last {timeWindowMinutes} minutes)");
                LogMessage($"Found {recentPhotos.Count} photo(s) within time window.");
            }

            return filesToTransfer;
        }

        private List<FileInfo> GetMediaFilesFixedCount(string path, int maxFiles)
        {
            var allMediaFiles = GetAllMediaFiles(path);
            return allMediaFiles.OrderByDescending(f => f.LastWriteTime).Take(maxFiles).ToList();
        }

        private Dictionary<string, long> BuildExistingFilesIndex(DriveInfo destination)
        {
            var existingFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string transferredMediaPath = Path.Combine(destination.RootDirectory.FullName, "TransferredMedia");

                if (Directory.Exists(transferredMediaPath))
                {
                    var directory = new DirectoryInfo(transferredMediaPath);
                    var allFiles = directory.GetFiles("*.*", SearchOption.AllDirectories);

                    foreach (var file in allFiles)
                    {
                        // Use filename + size as unique identifier
                        string key = $"{file.Name}_{file.Length}";
                        if (!existingFiles.ContainsKey(key))
                        {
                            existingFiles[key] = file.Length;
                        }
                    }

                    LogMessage($"Found {existingFiles.Count} existing file(s) on destination.");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Error indexing existing files: {ex.Message}");
            }

            return existingFiles;
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m {timeSpan.Seconds}s";
            }
            else if (timeSpan.TotalMinutes >= 1)
            {
                return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";
            }
            else
            {
                return $"{timeSpan.Seconds}s";
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void UpdateTransferUI(object sender, EventArgs e)
        {
            // This runs every 250ms during transfer to update the UI smoothly

            // Update current file progress
            if (currentFileSize > 0 && currentFileStopwatch.IsRunning)
            {
                int progress = (int)((double)currentFileBytesTransferred / currentFileSize * 100);
                TransferProgressBar.Value = progress;
                ProgressPercentTextBlock.Text = $"{progress}%";

                // Calculate current file time remaining
                if (currentFileStopwatch.Elapsed.TotalSeconds > 0.5)
                {
                    double bytesPerSecond = currentFileBytesTransferred / currentFileStopwatch.Elapsed.TotalSeconds;
                    if (bytesPerSecond > 0)
                    {
                        long remainingBytes = currentFileSize - currentFileBytesTransferred;
                        double secondsRemaining = remainingBytes / bytesPerSecond;

                        CurrentFileStatusTextBlock.Text = $"{FormatBytes(currentFileBytesTransferred)} / {FormatBytes(currentFileSize)} @ {FormatBytes((long)bytesPerSecond)}/s";
                        CurrentFileTimeTextBlock.Text = $"~{FormatTimeSpan(TimeSpan.FromSeconds(secondsRemaining))} remaining";
                    }
                }
            }

            // Update total transfer time remaining
            if (totalTransferStopwatch.IsRunning && totalTransferStopwatch.Elapsed.TotalSeconds > 1)
            {
                long totalTransferredSoFar = totalBytesTransferred + currentFileBytesTransferred;
                double overallBytesPerSecond = totalTransferredSoFar / totalTransferStopwatch.Elapsed.TotalSeconds;

                if (overallBytesPerSecond > 0)
                {
                    long totalRemainingBytes = totalBytesToTransfer - totalTransferredSoFar;
                    double totalSecondsRemaining = totalRemainingBytes / overallBytesPerSecond;

                    TotalTimeRemainingTextBlock.Text = $"Total: ~{FormatTimeSpan(TimeSpan.FromSeconds(totalSecondsRemaining))} remaining @ {FormatBytes((long)overallBytesPerSecond)}/s";
                }
            }
        }

        private async Task TransferLatestMedia(DriveInfo source, DriveInfo destination)
        {
            try
            {
                List<FileInfo> mediaFiles;

                // Determine which mode to use
                if (ModeTimeBasedRadio.IsChecked == true)
                {
                    // Time-based mode
                    int timeWindowMinutes = 40;
                    if (!int.TryParse(TimeWindowMinutesTextBox.Text, out timeWindowMinutes) || timeWindowMinutes < 1)
                    {
                        timeWindowMinutes = 40;
                        TimeWindowMinutesTextBox.Text = "40";
                    }

                    LogMessage($"Using time-based mode: Last {timeWindowMinutes} minutes from latest file");
                    mediaFiles = GetMediaFilesTimeBased(source.RootDirectory.FullName, timeWindowMinutes);
                }
                else
                {
                    // Fixed count mode
                    int maxFiles = 10;
                    if (!int.TryParse(MaxFilesTextBox.Text, out maxFiles) || maxFiles < 1)
                    {
                        maxFiles = 10;
                        MaxFilesTextBox.Text = "10";
                    }

                    LogMessage($"Using fixed count mode: Latest {maxFiles} files");
                    mediaFiles = GetMediaFilesFixedCount(source.RootDirectory.FullName, maxFiles);
                }

                if (!mediaFiles.Any())
                {
                    LogMessage("No media files found to transfer.");
                    return;
                }

                // Categorize files
                int videoCount = mediaFiles.Count(f => videoExtensions.Contains(f.Extension.ToLower()));
                int photoCount = mediaFiles.Count(f => photoExtensions.Contains(f.Extension.ToLower()));

                LogMessage($"Found {mediaFiles.Count} media file(s) to transfer: {videoCount} video(s), {photoCount} photo(s).");

                // Build index of existing files on destination
                var existingFiles = BuildExistingFilesIndex(destination);

                // Filter out files that already exist
                var filesToTransfer = new List<FileInfo>();
                var skippedFiles = new List<string>();

                foreach (var file in mediaFiles)
                {
                    string fileKey = $"{file.Name}_{file.Length}";

                    if (existingFiles.ContainsKey(fileKey))
                    {
                        skippedFiles.Add(file.Name);
                        LogMessage($"Skipping (already exists): {file.Name}");
                    }
                    else
                    {
                        filesToTransfer.Add(file);
                    }
                }

                if (!filesToTransfer.Any())
                {
                    LogMessage("All files already exist on destination. Nothing to transfer.");
                    MessageBox.Show("All media files already exist on the USB stick.\n\nNo new files to transfer.",
                        "Already Transferred", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Categorize files to transfer
                int videosToTransfer = filesToTransfer.Count(f => videoExtensions.Contains(f.Extension.ToLower()));
                int photosToTransfer = filesToTransfer.Count(f => photoExtensions.Contains(f.Extension.ToLower()));

                LogMessage($"Transferring {filesToTransfer.Count} new file(s): {videosToTransfer} video(s), {photosToTransfer} photo(s). Skipped {skippedFiles.Count} duplicate(s).");

                // Initialize transfer tracking
                totalFilesToTransfer = filesToTransfer.Count;
                totalBytesToTransfer = filesToTransfer.Sum(f => f.Length);
                totalBytesTransferred = 0;
                currentFileIndex = 0;
                totalTransferStopwatch.Restart();

                // Start UI update timer
                uiUpdateTimer.Start();

                string destFolder = Path.Combine(destination.RootDirectory.FullName,
                    "TransferredMedia", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(destFolder);

                // Create subfolders for organization
                string videosFolder = Path.Combine(destFolder, "Videos");
                string photosFolder = Path.Combine(destFolder, "Photos");

                if (videosToTransfer > 0)
                    Directory.CreateDirectory(videosFolder);
                if (photosToTransfer > 0)
                    Directory.CreateDirectory(photosFolder);

                // Sort files by timestamp for organized transfer
                filesToTransfer = filesToTransfer.OrderBy(f => f.LastWriteTime).ToList();

                for (int i = 0; i < filesToTransfer.Count; i++)
                {
                    var file = filesToTransfer[i];

                    // Determine destination subfolder
                    string destSubFolder = photoExtensions.Contains(file.Extension.ToLower()) ? photosFolder : videosFolder;
                    string destPath = Path.Combine(destSubFolder, file.Name);

                    currentFileIndex = i + 1;
                    currentFileSize = file.Length;

                    string fileType = photoExtensions.Contains(file.Extension.ToLower()) ? "Photo" : "Video";
                    LogMessage($"Transferring ({currentFileIndex}/{totalFilesToTransfer}) [{fileType}]: {file.Name} ({FormatBytes(file.Length)}) - {file.LastWriteTime:HH:mm:ss}");
                    CurrentFileTextBlock.Text = $"Copying: {file.Name}";
                    OverallProgressTextBlock.Text = $"Overall: {currentFileIndex} of {totalFilesToTransfer} files";

                    await CopyFileWithProgress(file.FullName, destPath, file.Length);

                    totalBytesTransferred += file.Length;
                    LogMessage($"Completed: {file.Name}");
                }

                // Stop UI update timer
                uiUpdateTimer.Stop();
                totalTransferStopwatch.Stop();

                // Force final update to show 100%
                TransferProgressBar.Value = 100;
                ProgressPercentTextBlock.Text = "100%";
                CurrentFileStatusTextBlock.Text = "";
                CurrentFileTimeTextBlock.Text = "";
                TotalTimeRemainingTextBlock.Text = "";

                // Give user a moment to see completion
                await Task.Delay(500);  // Half second pause at 100%

                TransferProgressBar.Value = 0;
                CurrentFileTextBlock.Text = "Transfer completed!";
                CurrentFileStatusTextBlock.Text = "";
                CurrentFileTimeTextBlock.Text = "";
                TotalTimeRemainingTextBlock.Text = "";
                OverallProgressTextBlock.Text = $"Overall: {totalFilesToTransfer} of {totalFilesToTransfer} files";

                // Show time range of transferred files
                string timeRangeInfo = "";
                if (filesToTransfer.Any())
                {
                    var oldestFile = filesToTransfer.OrderBy(f => f.LastWriteTime).First();
                    var newestFile = filesToTransfer.OrderByDescending(f => f.LastWriteTime).First();
                    timeRangeInfo = $"Time range: {oldestFile.LastWriteTime:HH:mm:ss} to {newestFile.LastWriteTime:HH:mm:ss}\n";
                }

                string summary = $"Transfer complete!\n\n" +
                                $"Files transferred: {filesToTransfer.Count}\n" +
                                $"  - Videos: {videosToTransfer}\n" +
                                $"  - Photos: {photosToTransfer}\n" +
                                $"Files skipped (duplicates): {skippedFiles.Count}\n" +
                                $"{timeRangeInfo}" +
                                $"Total size: {FormatBytes(totalBytesToTransfer)}\n" +
                                $"Total time: {FormatTimeSpan(totalTransferStopwatch.Elapsed)}\n" +
                                $"Average speed: {FormatBytes((long)(totalBytesToTransfer / totalTransferStopwatch.Elapsed.TotalSeconds))}/s\n\n" +
                                $"Files saved to:\n{destFolder}";

                LogMessage($"Transfer completed in {FormatTimeSpan(totalTransferStopwatch.Elapsed)}. {filesToTransfer.Count} new file(s) transferred, {skippedFiles.Count} duplicate(s) skipped.");
                MessageBox.Show(summary, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                uiUpdateTimer.Stop();
                LogMessage($"Error during transfer: {ex.Message}");
                MessageBox.Show($"Transfer failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task CopyFileWithProgress(string sourcePath, string destPath, long fileSize)
        {
            const int bufferSize = 1024 * 1024; // 1 MB buffer
            currentFileBytesTransferred = 0;
            currentFileStopwatch.Restart();

            using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read))
            using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            {
                byte[] buffer = new byte[bufferSize];
                int bytesRead;

                while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await destStream.WriteAsync(buffer, 0, bytesRead);
                    currentFileBytesTransferred += bytesRead;
                }
            }

            currentFileStopwatch.Stop();
        }

        private void LogMessage(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            LogTextBox.AppendText($"[{timestamp}] {message}\n");
            LogTextBox.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            uiUpdateTimer.Stop();
            base.OnClosed(e);
        }
    }
}