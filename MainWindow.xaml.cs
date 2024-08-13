using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using Microsoft.Win32;
using Newtonsoft.Json;
using MessageBox = System.Windows.MessageBox;
using Timer = System.Timers.Timer;

namespace BackupApp
{
    public partial class MainWindow : Window
    {
        private string sourcePath = string.Empty;
        private string destinationPath = string.Empty;
        private string logDir = string.Empty;
        private Timer backupTimer;
        private BackupConfig backupConfig;
        private const string ConfigFilePath = "backup_config.json";
        private const string AppName = "BackupApp";
        private const double WindowHeightCollapsed = 320;
        private const double WindowHeightExpanded = 420;
        private NotifyIcon notifyIcon;

        public MainWindow()
        {
            InitializeComponent();
            this.Height = WindowHeightCollapsed;

            LoadConfig();
            SetupBackupTimer();
            SetupTrayIcon();

            chkStartWithWindows.IsChecked = IsStartupEnabled();
        }

        private void SetupTrayIcon()
        {
            notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "BackupApp"
            };

            notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

            var contextMenu = new System.Windows.Forms.ContextMenu();
            contextMenu.MenuItems.Add("Otwórz", (s, e) => ShowMainWindow());
            contextMenu.MenuItems.Add("Zamknij", (s, e) => CloseApplication());

            notifyIcon.ContextMenu = contextMenu;
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void CloseApplication()
        {
            notifyIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                this.Hide();
            }
            base.OnStateChanged(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            notifyIcon.Dispose();
            base.OnClosed(e);
        }

        private bool IsStartupEnabled()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
                return key.GetValue(AppName) != null;
            }
            catch
            {
                return false;
            }
        }

        private void AddToStartup()
        {
            try
            {
                string startupPath = Assembly.GetExecutingAssembly().Location;
                RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key.SetValue(AppName, $"\"{startupPath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to add application to startup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveFromStartup()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key.DeleteValue(AppName, false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to remove application from startup: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnSelectSource_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FolderBrowserDialog();
            var result = dialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                sourcePath = dialog.SelectedPath;
                txtSource.Text = sourcePath;
            }
        }

        private void btnSelectDestination_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FolderBrowserDialog();
            var result = dialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
            {
                destinationPath = dialog.SelectedPath;
                txtDestination.Text = destinationPath;
            }
        }

        private void btnStartBackup_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
            {
                MessageBox.Show("Proszę wybrać zarówno folder źródłowy, jak i docelowy.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!int.TryParse(txtFrequency.Text, out int frequency) || frequency <= 0)
            {
                MessageBox.Show("Proszę wprowadzić poprawną wartość dla częstotliwości.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string selectedUnit = (cbTimeUnit.SelectedItem as ComboBoxItem)?.Content.ToString();
            if (string.IsNullOrWhiteSpace(selectedUnit))
            {
                MessageBox.Show("Proszę wybrać jednostkę czasu.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            TimeSpan interval;
            switch (selectedUnit)
            {
                case "Sekundy":
                    interval = TimeSpan.FromSeconds(frequency);
                    break;
                case "Minuty":
                    interval = TimeSpan.FromMinutes(frequency);
                    break;
                case "Godziny":
                    interval = TimeSpan.FromHours(frequency);
                    break;
                case "Dni":
                    interval = TimeSpan.FromDays(frequency);
                    break;
                default:
                    MessageBox.Show("Nieznana jednostka czasu.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
            }

            logDir = Path.Combine(destinationPath, "logs");
            if (!Directory.Exists(logDir))
            {
                try
                {
                    Directory.CreateDirectory(logDir);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Nie udało się utworzyć folderu logów: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }

            DateTime nextBackupTime = DateTime.Now.Add(interval);
            backupConfig = new BackupConfig
            {
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Frequency = frequency,
                TimeUnit = selectedUnit,
                NextBackupTime = nextBackupTime
            };

            SaveConfig();

            SetupBackupTimer();

            MessageBox.Show($"Automatyczne kopie zapasowe rozpoczęte. Kolejna kopia za {frequency} {selectedUnit.ToLower()}.", "Informacja", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SetupBackupTimer()
        {
            if (backupTimer != null)
            {
                backupTimer.Stop();
                backupTimer.Dispose();
            }

            if (backupConfig == null || backupConfig.NextBackupTime <= DateTime.Now)
            {
                return;
            }

            double interval = (backupConfig.NextBackupTime - DateTime.Now).TotalMilliseconds;

            if (interval <= 0)
            {
                MessageBox.Show("Błąd: Nieprawidłowy czas następnej kopii zapasowej.", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            backupTimer = new Timer(interval);
            backupTimer.Elapsed += async (s, ev) =>
            {
                await Dispatcher.Invoke(async () =>
                {
                    await StartBackupAsync();

                    backupTimer.Stop();
                    backupTimer.Dispose();

                    TimeSpan nextInterval;
                    switch (backupConfig.TimeUnit)
                    {
                        case "Sekundy":
                            nextInterval = TimeSpan.FromSeconds(backupConfig.Frequency);
                            break;
                        case "Minuty":
                            nextInterval = TimeSpan.FromMinutes(backupConfig.Frequency);
                            break;
                        case "Godziny":
                            nextInterval = TimeSpan.FromHours(backupConfig.Frequency);
                            break;
                        case "Dni":
                            nextInterval = TimeSpan.FromDays(backupConfig.Frequency);
                            break;
                        default:
                            nextInterval = TimeSpan.FromDays(1); // Fallback to 1 day
                            break;
                    }
                    backupConfig.NextBackupTime = DateTime.Now.Add(nextInterval);
                    SaveConfig();
                    SetupBackupTimer();
                });
            };

            backupTimer.Start();
            Dispatcher.Invoke(() =>
            {
                UpdateCurrentIntervalLabel();
                btnStopBackup.IsEnabled = true;
            });
        }

        private void UpdateCurrentIntervalLabel()
        {
            if (backupConfig != null)
            {
                Dispatcher.Invoke(() =>
                {
                    lblCurrentInterval.Content = $"Aktualny czasomierz: {backupConfig.Frequency} {backupConfig.TimeUnit.ToLower()}";
                });
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    lblCurrentInterval.Content = "Aktualny czasomierz: --";
                });
            }
        }

        private void LoadConfig()
        {
            if (File.Exists(ConfigFilePath))
            {
                string configJson = File.ReadAllText(ConfigFilePath);
                backupConfig = JsonConvert.DeserializeObject<BackupConfig>(configJson);

                if (backupConfig != null)
                {
                    sourcePath = backupConfig.SourcePath;
                    destinationPath = backupConfig.DestinationPath;
                    txtSource.Text = sourcePath;
                    txtDestination.Text = destinationPath;
                    txtFrequency.Text = backupConfig.Frequency.ToString();
                    SelectTimeUnit(backupConfig.TimeUnit);
                }
            }
        }

        private void SelectTimeUnit(string timeUnit)
        {
            foreach (ComboBoxItem item in cbTimeUnit.Items)
            {
                if (item.Content.ToString() == timeUnit)
                {
                    cbTimeUnit.SelectedItem = item;
                    break;
                }
            }
        }

        private void SaveConfig()
        {
            string configJson = JsonConvert.SerializeObject(backupConfig, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(ConfigFilePath, configJson);
        }

        private async Task StartBackupAsync()
        {
            string datetime = DateTime.Now.ToString("yyyy-MM-dd_HH-mm");
            string logFile = Path.Combine(logDir, $"backup_log_{datetime}.txt");

            Dispatcher.Invoke(() =>
            {
                AppendLog($"[{datetime}] Rozpoczęcie kopii zapasowej...\n");
                progressBar.Value = 0;
                progressBar.Visibility = Visibility.Visible;
                lblProgress.Content = "0% ukończono, szacowany czas: --:--:--";
            });

            try
            {
                long totalBytesCopied = 0;
                long totalSize = GetDirectorySize(new DirectoryInfo(sourcePath));
                Stopwatch stopwatch = Stopwatch.StartNew();

                await Task.Run(() => CopyFilesWithProgress(sourcePath, destinationPath, logFile, ref totalBytesCopied, totalSize, stopwatch));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Wystąpił błąd podczas tworzenia kopii zapasowej: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                    AppendLog($"BŁĄD: {ex.Message}\n");
                    File.AppendAllText(logFile, $"BŁĄD: {ex.Message}\n");
                });
            }

            Dispatcher.Invoke(() =>
            {
                progressBar.Visibility = Visibility.Collapsed;
                lblProgress.Content = "Kopia zapasowa zakończona.";
                AppendLog("-------------------------------------------\n");
            });
        }

        private void btnStopBackup_Click(object sender, RoutedEventArgs e)
        {
            if (backupTimer != null)
            {
                backupTimer.Stop();
                backupTimer.Dispose();
                backupTimer = null;

                btnStopBackup.IsEnabled = false;

                MessageBox.Show("Kopie zapasowe zostały zatrzymane.", "Informacja", MessageBoxButton.OK, MessageBoxImage.Information);
                lblCurrentInterval.Content = "Aktualny czasomierz: --";
            }
        }

        private void CopyFilesWithProgress(string sourceDir, string destDir, string logFile, ref long totalBytesCopied, long totalSize, Stopwatch stopwatch)
        {
            DirectoryInfo dir = new DirectoryInfo(sourceDir);
            DirectoryInfo[] dirs = dir.GetDirectories();

            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string temppath = Path.Combine(destDir, file.Name);
                file.CopyTo(temppath, true);

                totalBytesCopied += file.Length;
                double progress = (double)totalBytesCopied / totalSize * 100;

                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                double bytesPerSecond = totalBytesCopied / elapsedSeconds;
                double remainingBytes = totalSize - totalBytesCopied;
                double estimatedSecondsRemaining = remainingBytes / bytesPerSecond;
                TimeSpan estimatedTimeRemaining = TimeSpan.FromSeconds(estimatedSecondsRemaining);

                Dispatcher.Invoke(() =>
                {
                    progressBar.Value = progress;
                    lblProgress.Content = $"{progress:0.00}% ukończono, szacowany czas: {estimatedTimeRemaining:hh\\:mm\\:ss}";
                    AppendLog($"Kopiowanie: {file.Name}\n");
                    File.AppendAllText(logFile, $"Kopiowanie: {file.Name}\n");
                });
            }

            foreach (DirectoryInfo subdir in dirs)
            {
                string temppath = Path.Combine(destDir, subdir.Name);
                DirectoryCopy(subdir.FullName, temppath, logFile, ref totalBytesCopied, totalSize, stopwatch);
            }
        }

        private void DirectoryCopy(string sourceDir, string destDir, string logFile, ref long totalBytesCopied, long totalSize, Stopwatch stopwatch)
        {
            DirectoryInfo dir = new DirectoryInfo(sourceDir);
            DirectoryInfo[] dirs = dir.GetDirectories();

            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string temppath = Path.Combine(destDir, file.Name);
                file.CopyTo(temppath, true);

                totalBytesCopied += file.Length;
                double progress = (double)totalBytesCopied / totalSize * 100;

                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                double bytesPerSecond = totalBytesCopied / elapsedSeconds;
                double remainingBytes = totalSize - totalBytesCopied;
                double estimatedSecondsRemaining = remainingBytes / bytesPerSecond;
                TimeSpan estimatedTimeRemaining = TimeSpan.FromSeconds(estimatedSecondsRemaining);

                Dispatcher.Invoke(() =>
                {
                    progressBar.Value = progress;
                    lblProgress.Content = $"{progress:0.00}% ukończono, szacowany czas: {estimatedTimeRemaining:hh\\:mm\\:ss}";
                    AppendLog($"Kopiowanie: {file.Name}\n");
                    File.AppendAllText(logFile, $"Kopiowanie: {file.Name}\n");
                });
            }

            foreach (DirectoryInfo subdir in dirs)
            {
                string temppath = Path.Combine(destDir, subdir.Name);
                DirectoryCopy(subdir.FullName, temppath, logFile, ref totalBytesCopied, totalSize, stopwatch);
            }
        }

        private long GetDirectorySize(DirectoryInfo directoryInfo)
        {
            long size = 0;
            foreach (FileInfo file in directoryInfo.GetFiles())
            {
                size += file.Length;
            }

            foreach (DirectoryInfo dir in directoryInfo.GetDirectories())
            {
                size += GetDirectorySize(dir);
            }

            return size;
        }

        private void AppendLog(string message)
        {
            txtLog.AppendText(message);
            scrollViewer.ScrollToEnd();
        }

        private void chkShowLogs_Checked(object sender, RoutedEventArgs e)
        {
            scrollViewer.Visibility = Visibility.Visible;
            this.Height = WindowHeightExpanded;
        }

        private void chkShowLogs_Unchecked(object sender, RoutedEventArgs e)
        {
            scrollViewer.Visibility = Visibility.Collapsed;
            this.Height = WindowHeightCollapsed;
        }

        private void chkStartWithWindows_Checked(object sender, RoutedEventArgs e)
        {
            AddToStartup();
        }

        private void chkStartWithWindows_Unchecked(object sender, RoutedEventArgs e)
        {
            RemoveFromStartup();
        }

        private void txtFrequency_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (txtFrequency.Text == string.Empty)
            {
                placeholderText.Visibility = Visibility.Visible;
            }
            else
            {
                placeholderText.Visibility = Visibility.Collapsed;
            }
        }
    }

    public class BackupConfig
    {
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public int Frequency { get; set; }
        public string TimeUnit { get; set; }
        public DateTime NextBackupTime { get; set; }
    }
}