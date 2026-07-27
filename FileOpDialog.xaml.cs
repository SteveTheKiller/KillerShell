using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using KillerShell.Services;

namespace KillerShell
{
    // Progress + conflict UI for Services/FileOps.
    //
    // The window OWNS the operation: you hand it the work, it runs it on a background thread and
    // returns the result when it closes. That is what keeps the conflict prompt honest - the
    // worker genuinely blocks on the answer, so nothing is copied while the question is on
    // screen, and "apply to the rest" is a real decision rather than a retroactive one.
    //
    // Threading: FileOps runs on a task. Progress is posted (fire and forget, so a slow repaint
    // never throttles the copy); a conflict is Invoked (blocking, because the worker cannot
    // proceed without the answer) and the worker then waits on an event the buttons set.
    public partial class FileOpDialog : Window
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        // Set once "do this for the rest" is ticked; every later collision answers from here
        // without troubling the user again.
        private ConflictChoice? _applyAll;

        private ConflictChoice _answer = ConflictChoice.Cancel;
        private readonly ManualResetEventSlim _answered = new ManualResetEventSlim(false);

        // True once the worker has finished and asked us to close, so OnClosing can tell a real
        // completion apart from the user shutting the window mid-copy.
        private bool _finished;

        public FileOpResult Result { get; private set; } = new FileOpResult();

        private FileOpDialog(string opLabelKey)
        {
            InitializeComponent();
            Loaded += (_, _) => Anim.FadeIn(RootBorder);
            SourceInitialized += (_, _) => MainWindow.ApplyThemeBorder(this);

            OpLabel.Text = TryFindResource(opLabelKey) as string ?? string.Empty;
        }

        // ── Entry points ─────────────────────────────────────────

        /// <summary>Copy or move into a folder. Blocks until the operation ends or is canceled.</summary>
        public static FileOpResult CopyOrMove(Window owner, IEnumerable<string> sources,
                                              string targetDir, bool move)
        {
            var list = new List<string>(sources);
            var dlg = new FileOpDialog(move ? "Str_Fo_Moving" : "Str_Fo_Copying") { Owner = owner };

            dlg.Start(() => FileOps.CopyOrMove(list, targetDir, move,
                                               dlg.AskConflict, dlg.ReportProgress, dlg._cts.Token));
            return dlg.Result;
        }

        /// <summary>Permanent delete. Recycling does not come through here - it is one shell
        /// call with no progress of ours to show (Services/FileOps.cs Recycle).</summary>
        public static FileOpResult Delete(Window owner, IEnumerable<string> paths)
        {
            var list = new List<string>(paths);
            var dlg = new FileOpDialog("Str_Fo_Deleting") { Owner = owner };

            dlg.Start(() => FileOps.Delete(list, dlg.ReportProgress, dlg._cts.Token));
            return dlg.Result;
        }

        /// <summary>
        /// Runs <paramref name="work"/> on a task and shows the dialog until it finishes.
        /// </summary>
        private void Start(Func<FileOpResult> work)
        {
            // Started from Loaded rather than here: a fast operation would otherwise finish and
            // call Close() before ShowDialog() had run, and ShowDialog on an already-closed
            // window throws. Deferring means the order is always show, then work, then close.
            Loaded += (_, _) => RunWork(work);
            ShowDialog();
        }

        private void RunWork(Func<FileOpResult> work)
        {
            Task.Run(() =>
            {
                FileOpResult r;
                try { r = work(); }
                catch (OperationCanceledException) { r = new FileOpResult { Canceled = true }; }
                catch (Exception ex)
                {
                    r = new FileOpResult();
                    r.Failed.Add((string.Empty, ex.Message));
                }

                Dispatcher.Invoke(() =>
                {
                    Result    = r;
                    _finished = true;
                    Close();
                });
            });
        }

        // ── Progress (worker thread) ─────────────────────────────

        private void ReportProgress(FileOpProgress p)
        {
            // Snapshot: the worker mutates the same instance as it goes, so posting it directly
            // would let the UI read a half-updated set of numbers.
            string file  = p.CurrentFile;
            int    done  = p.ItemsDone,  total = p.ItemsTotal;
            long   bDone = p.BytesDone, bTotal = p.BytesTotal;

            // BeginInvoke, not Invoke: a copy must never wait on a repaint.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CurrentFile.Text = file;

                // Bytes are the honest measure when there are bytes to count; a delete has none,
                // so it falls back to the item count.
                double ratio = bTotal > 0 ? (double)bDone / bTotal
                             : total  > 0 ? (double)done  / total
                             : 0;
                ratio = Math.Max(0, Math.Min(1, ratio));

                BarFill.Width = new GridLength(ratio, GridUnitType.Star);
                BarRest.Width = new GridLength(1 - ratio, GridUnitType.Star);

                string fmt = TryFindResource("Str_Fo_OfCount") as string ?? "{0} of {1}";
                CountLabel.Text = string.Format(fmt, done.ToString("N0"), total.ToString("N0"))
                                + (bTotal > 0 ? "   " + Human(bDone) + " / " + Human(bTotal) : string.Empty);
            }));
        }

        // ── Conflict (called ON the worker thread) ───────────────

        private ConflictChoice AskConflict(ConflictInfo info)
        {
            if (_applyAll.HasValue) return _applyAll.Value;
            if (_cts.IsCancellationRequested) return ConflictChoice.Cancel;

            _answered.Reset();
            Dispatcher.Invoke(() => ShowConflict(info));

            _answered.Wait();                       // buttons (or Cancel) set this
            Dispatcher.Invoke(ShowProgress);
            return _answer;
        }

        private void ShowConflict(ConflictInfo info)
        {
            string fmt = TryFindResource("Str_Fo_ConflictTitle") as string ?? "{0} already exists here";
            ConflictTitle.Text = string.Format(fmt, Path.GetFileName(info.TargetPath));

            TargetInfo.Text = Describe(info.TargetPath, info.TargetSize, info.TargetModified, info.IsDirectory);
            SourceInfo.Text = Describe(info.SourcePath, info.SourceSize, info.SourceModified, info.IsDirectory);

            ApplyAll.IsChecked = false;

            ProgressView.Visibility    = Visibility.Collapsed;
            ProgressButtons.Visibility = Visibility.Collapsed;
            ConflictView.Visibility    = Visibility.Visible;
            ConflictButtons.Visibility = Visibility.Visible;
        }

        private void ShowProgress()
        {
            ConflictView.Visibility    = Visibility.Collapsed;
            ConflictButtons.Visibility = Visibility.Collapsed;
            ProgressView.Visibility    = Visibility.Visible;
            ProgressButtons.Visibility = Visibility.Visible;
        }

        private void Answer(ConflictChoice choice)
        {
            if (ApplyAll.IsChecked == true) _applyAll = choice;
            _answer = choice;
            _answered.Set();
        }

        private void Replace_Click(object sender, RoutedEventArgs e)  => Answer(ConflictChoice.Replace);
        private void Skip_Click(object sender, RoutedEventArgs e)     => Answer(ConflictChoice.Skip);
        private void KeepBoth_Click(object sender, RoutedEventArgs e) => Answer(ConflictChoice.KeepBoth);

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();

            // If a conflict is on screen the worker is parked on the event, and canceling the
            // token alone would never wake it - it is not inside a copy loop to notice.
            _answer = ConflictChoice.Cancel;
            _answered.Set();
        }

        // ── Window plumbing ──────────────────────────────────────

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Closing mid-operation means cancel, not abandon: the worker still owns open file
            // handles. Hold the window until it winds down and closes us itself.
            if (!_finished)
            {
                e.Cancel = true;
                _cts.Cancel();
                _answer = ConflictChoice.Cancel;
                _answered.Set();
                return;
            }

            base.OnClosing(e);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        // ── Formatting ───────────────────────────────────────────

        private string Describe(string path, long size, DateTime modified, bool isDirectory)
        {
            string line = path;
            if (!isDirectory) line += Environment.NewLine + Human(size);
            if (modified != default)
                line += (isDirectory ? Environment.NewLine : "   ") + modified.ToString("yyyy-MM-dd HH:mm");
            return line;
        }

        private static string Human(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes / 1024.0;
            if (v < 1024) return v.ToString("0.#") + " KB";
            v /= 1024;
            if (v < 1024) return v.ToString("0.#") + " MB";
            v /= 1024;
            return v.ToString("0.##") + " GB";
        }
    }
}
