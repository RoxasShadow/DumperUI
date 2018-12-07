using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace DumperUI
{
    public partial class MainWindow : Window
    {
        private String latestSelectedFolder = null;
        private String logFile;
        private SynchronizationContext context;
        private FileSystemWatcher watcher;
        private Process dumpingProcess;

        public MainWindow()
        {
            InitializeComponent();

            InitializeProfileList();

            this.url.Focus();

            this.Title = AppVersion();
            CreateLogFile();
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            DeleteLogFile();
        }

        private void InitializeProfileList() {
            Process p = CreateDumperInstance("-l");
            if (!p.Start())
            {
                MessageBox.Show("Unable to run dumper", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            String[] profiles = p.StandardOutput
                .ReadToEnd()
                .Split(new[] { '\r', '\n' })
                .Skip(1)
                .Select(profile => new string(profile
                    .ToCharArray()
                    .Where(c => !Char.IsWhiteSpace(c))
                    .ToArray()
                ))
                .Where(profile => profile.Length > 0)
                .ToArray();

            this.profilesList.Items.Add("Auto");

            foreach (String profile in profiles) {
                this.profilesList.Items.Add(profile);
            }

            p.Dispose();
        }

        public async void StartDumpingBtn_Click(object sender, RoutedEventArgs e)
        {
            String url = this.url.Text;
            if(url.Length == 0)
            {
                MessageBox.Show("URL is missing", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            else if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                MessageBox.Show("URL seems to be malformed", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CommonOpenFileDialog dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                InitialDirectory = latestSelectedFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
            {
                return;
            }

            String parentDir = Directory.GetParent(dialog.FileName).FullName;
            if (Directory.Exists(parentDir)) {
                this.latestSelectedFolder = parentDir;
            }

            CreateLogFile();

            String cmd = string.Join(" ", BuildCmd(dialog));
            this.logBox.Text = "$> dumper " + cmd + "\n";

            InitializeFileWatcher();

            await Task.Factory.StartNew(() => StartDumping(cmd), TaskCreationOptions.LongRunning);

            this.logBox.Focus();
        }

        private void InitializeFileWatcher()
        {
            watcher = new FileSystemWatcher
            {
                Path = Path.GetDirectoryName(logFile),
                Filter = Path.GetFileName(logFile),
                NotifyFilter = NotifyFilters.LastWrite
            };
            context = SynchronizationContext.Current;
            watcher.Changed += new FileSystemEventHandler(LogUpdated);
            watcher.EnableRaisingEvents = true;
        }

        private string BuildCmd(CommonFileDialog dialog)
        {
            List<string> cmdBuilder = new List<string>(new string[]
            {
                "--output", logFile,
                "--url", string.Format("\"{0}\"", this.url.Text),
                "--path", string.Format("\"{0}\"", dialog.FileName)
            });
            if (this.pageStart.Value > 1)
            {
                cmdBuilder.Add("--from");
                cmdBuilder.Add(this.pageStart.Value.ToString());
            }
            if (this.pageEnd.Value > 1)
            {
                if(this.pageStart.Value == 1)
                {
                    cmdBuilder.Add("--from");
                    cmdBuilder.Add("1");
                }

                cmdBuilder.Add("--to");
                cmdBuilder.Add(this.pageEnd.Value.ToString());
            }
            if (this.profilesList.SelectedIndex > 0)
            {
                cmdBuilder.Add("--profile");
                cmdBuilder.Add((string)this.profilesList.SelectedValue);
            }
            if (this.threadsNum.Value > 1)
            {
                cmdBuilder.Add("--threads");
                cmdBuilder.Add("1:" + threadsNum.Value);
            }
            return string.Join(" ", cmdBuilder);
        }

        private void StartDumping(String cmd)
        {
            startDumpingBtn.Dispatcher.Invoke(new Action(() =>
            {
                startDumpingBtn.IsEnabled = false;
                startDumpingBtn.Content = "Now dumping...";
            }));

            dumpingProcess = CreateDumperInstance(cmd, false);
            dumpingProcess.EnableRaisingEvents = true;
            dumpingProcess.Exited += new EventHandler(DumpHasFinished);
            dumpingProcess.Start();
        }

        private void DumpHasFinished(object sender, EventArgs e)
        {
            watcher.Changed -= new FileSystemEventHandler(LogUpdated);

            MessageBox.Show("Dump ended", "Success", MessageBoxButton.OK, MessageBoxImage.Information);

            startDumpingBtn.Dispatcher.Invoke(new Action(() =>
            {
                startDumpingBtn.IsEnabled = true;
                startDumpingBtn.Content = "Dump";
            }));

            dumpingProcess.Dispose();

            this.url.Focus();
        }

        private int lastReadLogLine = 0;
        private int failedReadLogLineAttempts = 0;

        private void LogUpdated(object source, FileSystemEventArgs e)
        {
            try
            {
                string[] lines = File.ReadLines(logFile).Skip(lastReadLogLine).ToArray();
                Console.WriteLine(failedReadLogLineAttempts);
                if (lines.Length == lastReadLogLine)
                {
                    if (failedReadLogLineAttempts == 5)
                    {
                        context.Post(val => dumpingProcess.Close(), source);
                        return;
                    }
                    Thread.Sleep(500);
                    failedReadLogLineAttempts++;
                }
                lastReadLogLine = lines.Length;
                context.Post(val =>
                {
                    this.logBox.AppendText(string.Join("\n\n", lines));
                    this.logBox.ScrollToEnd();
                    failedReadLogLineAttempts = 0;
                }, source);
            }
            catch (IOException) { LogUpdated(source, e); }
        }

        private Process CreateDumperInstance(String cmd, bool redirectOutput = true)
        {
            Process p = new Process();
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = redirectOutput;
            p.StartInfo.FileName = "cmd.exe";
            p.StartInfo.Arguments = "/c dumper " + cmd + " 2>&1";
            return p;
        }

        private String AppVersion ()
        {
            string productVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
            string productName = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductName;
            return productName + " " + productVersion.Substring(0, productVersion.Length - 2);
        }

        private void DeleteLogFile ()
        {
            if (logFile != null && File.Exists(logFile))
            {
                File.Delete(logFile);
            }
        }

        private void CreateLogFile ()
        {
            DeleteLogFile();
            logFile = Path.GetTempPath() + "dumper-" + new Random().Next(900) + ".log"; // Guid.NewGuid().ToString()
            File.Create(logFile);
        }
    }
}
