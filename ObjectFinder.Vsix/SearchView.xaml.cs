using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using ObjectFinder.Core;

namespace ObjectFinder.Vsix
{
    public partial class SearchView : UserControl
    {
        private ObjectSearcher _searcher = new ObjectSearcher(new DbObject[0]);
        private string _source = "Select a database in Object Explorer, then press F5.";
        private ObjectExplorerSelection _selection;
        private CancellationTokenSource _reloadTokenSource;
        private readonly SemaphoreSlim _reloadGate = new SemaphoreSlim(1, 1);
        private Window _hostWindow;

        public SearchView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnIsVisibleChanged;
            AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnViewPreviewKeyDown), true);
            UpdateResults();
        }

        /// <summary>Raised when the user presses Enter or double-clicks a result.</summary>
        public event Action<DbObject> ObjectChosen;

        /// <summary>Raised when the user presses Escape.</summary>
        public event Action CloseRequested;

        public void FocusSearchBox()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }

        public void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HookHostWindowKeyHandling();
            FocusSearchBox();
            _ = ReloadAsync(true);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelPendingReload();
            Mouse.OverrideCursor = null;

            if (_hostWindow != null)
            {
                _hostWindow.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnHostPreviewKeyDown));
                _hostWindow = null;
            }
        }

        private void CancelPendingReload()
        {
            CancellationTokenSource current = Interlocked.Exchange(ref _reloadTokenSource, null);
            if (current != null)
            {
                current.Cancel();
                current.Dispose();
            }
        }

        private void HookHostWindowKeyHandling()
        {
            if (_hostWindow != null)
                return;

            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
                _hostWindow.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnHostPreviewKeyDown), true);
        }

        private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsVisible || !IsEscapeKey(e))
                return;

            RequestClose();
            e.Handled = true;
        }

        /// <summary>
        /// Re-reads the object list for the database selected in Object Explorer.
        /// With keepCurrentOnFailure, a failed refresh (for example a server node is selected) leaves the
        /// previous list in place instead of replacing it with an error message.
        /// </summary>
        public async Task ReloadAsync(bool keepCurrentOnFailure)
        {
            CancellationTokenSource currentReload = new CancellationTokenSource();
            CancellationTokenSource previousReload = Interlocked.Exchange(ref _reloadTokenSource, currentReload);
            if (previousReload != null)
            {
                previousReload.Cancel();
                previousReload.Dispose();
            }

            CancellationToken cancellationToken = currentReload.Token;

            await _reloadGate.WaitAsync(cancellationToken);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                ObjectExplorerSelection selection = ObjectExplorerBridge.GetSelection();
                List<DbObject> objects = await Task.Run(
                    delegate { return ObjectExplorerBridge.LoadObjects(selection); },
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    return;

                _searcher = new ObjectSearcher(objects);
                _selection = selection;
                _source = selection.DatabaseName + " on " + selection.ServerName + ": " + objects.Count + " objects";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (keepCurrentOnFailure && _searcher.Count > 0)
                    return;

                _searcher = new ObjectSearcher(new DbObject[0]);
                _selection = null;
                _source = ex.Message;
            }
            finally
            {
                _reloadGate.Release();

                if (ReferenceEquals(currentReload, _reloadTokenSource))
                {
                    _reloadTokenSource = null;
                    currentReload.Dispose();
                }
            }

            UpdateResults();
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
                _ = ReloadAsync(true);
        }

        private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsEscapeKey(e))
                return;

            RequestClose();
            e.Handled = true;
        }

        private static bool IsEscapeKey(KeyEventArgs e)
        {
            return (e.Key == Key.Escape || e.SystemKey == Key.Escape)
                && Keyboard.Modifiers == ModifierKeys.None;
        }

        private void RequestClose()
        {
            Action handler = CloseRequested;
            if (handler != null)
                handler();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateResults();
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            switch (e.Key)
            {
                case Key.Down: MoveSelection(1); e.Handled = true; break;
                case Key.Up: MoveSelection(-1); e.Handled = true; break;
                case Key.Enter: ChooseSelected(); e.Handled = true; break;
                case Key.Escape:
                    if (Keyboard.Modifiers == ModifierKeys.None)
                    {
                        RequestClose();
                        e.Handled = true;
                    }
                    break;
                case Key.F5:
                    e.Handled = true;
                    _ = ReloadAsync(false);
                    break;
            }
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ChooseSelected();
        }

        private void NameLink_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            e.Handled = true;
            FrameworkContentElement link = sender as FrameworkContentElement;
            ResultRow row = link == null ? null : link.DataContext as ResultRow;
            if (row == null)
                return;

            ResultsList.SelectedItem = row;
            ChooseRow(row);
        }

        private void AlterLink_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            e.Handled = true;
            OpenScriptFromLink(sender as FrameworkContentElement, true);
        }

        private void CreateLink_Click(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            e.Handled = true;
            OpenScriptFromLink(sender as FrameworkContentElement, false);
        }

        private void OpenScriptFromLink(FrameworkContentElement link, bool alter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ResultRow row = link == null ? null : link.DataContext as ResultRow;
            if (row == null)
                return;

            OpenScript(row.Object, alter);
        }

        private void OpenScript(DbObject target, bool alter)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_selection == null)
            {
                StatusText.Text = "Nothing is loaded. Select a database in Object Explorer and press F5.";
                return;
            }

            string verb = alter ? "ALTER" : "CREATE";
            StatusText.Text = "Opening " + verb + " script for " + target.FullName + "...";
            Mouse.OverrideCursor = Cursors.Wait;

#pragma warning disable VSTHRD001 // Deliberate: the next call blocks the UI thread for seconds.
            Dispatcher.Invoke(new Action(delegate { }), System.Windows.Threading.DispatcherPriority.Render);
#pragma warning restore VSTHRD001

            bool opened = false;
            try
            {
                ObjectExplorerBridge.OpenScriptInNewQuery(_selection, target, alter);
                opened = true;
                StatusText.Text = verb + " script opened for " + target.FullName + ".";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (opened)
                RequestClose();
        }

        private void UpdateResults()
        {
            string query = SearchBox.Text;

            if (string.IsNullOrWhiteSpace(query))
            {
                ResultsList.ItemsSource = null;
                StatusText.Text = _searcher.Count > 0
                    ? _source + ". Start typing to search, F5 reloads."
                    : _source;
                return;
            }

            List<ResultRow> rows = _searcher.Search(query, 50)
                .Select(r => new ResultRow(r.Item))
                .ToList();

            ResultsList.ItemsSource = rows;
            if (rows.Count > 0) ResultsList.SelectedIndex = 0;
            StatusText.Text = rows.Count == 0 ? "No matches in " + _source : rows.Count + " results";
        }

        private void MoveSelection(int delta)
        {
            int count = ResultsList.Items.Count;
            if (count == 0) return;

            int index = Math.Max(0, Math.Min(count - 1, ResultsList.SelectedIndex + delta));
            ResultsList.SelectedIndex = index;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }

        private void ChooseSelected()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ChooseRow(ResultsList.SelectedItem as ResultRow);
        }

        private void ChooseRow(ResultRow row)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (row == null) return;

            if (ObjectChosen != null)
                ObjectChosen(row.Object);

            if (_selection == null)
            {
                StatusText.Text = "Nothing is loaded. Select a database in Object Explorer and press F5.";
                return;
            }

            StatusText.Text = "Selecting " + row.Object.FullName
                + " in Object Explorer... (large folders can take a few seconds)";
            Mouse.OverrideCursor = Cursors.Wait;

            // Let WPF paint the message before the UI thread blocks inside Object Explorer.
#pragma warning disable VSTHRD001 // Deliberate: the next call blocks the UI thread for seconds.
            Dispatcher.Invoke(new Action(delegate { }), System.Windows.Threading.DispatcherPriority.Render);
#pragma warning restore VSTHRD001

            bool selected = false;
            try
            {
                ObjectExplorerBridge.Reveal(_selection, row.Object);
                selected = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (selected)
            {
                Action handler = CloseRequested;
                if (handler != null)
                    handler();

                ObjectExplorerBridge.FocusObjectExplorerWindow();
            }
        }

        public void Activate()
        {
            _ = ActivateAsync();
        }

        private async Task ActivateAsync()
        {
            await ReloadAsync(true);

#pragma warning disable VSTHRD001 // Deliberate: Input priority runs after the window frame has finished showing.
            _ = Dispatcher.BeginInvoke(
                new Action(FocusSearchBox),
                System.Windows.Threading.DispatcherPriority.Input);
#pragma warning restore VSTHRD001
        }
    }

    internal sealed class ResultRow
    {
        public ResultRow(DbObject item)
        {
            Object = item;
        }

        public DbObject Object { get; }
        public string Name => Object.Name;
        public string Schema => Object.Schema;
        public Visibility AlterLinkVisibility => Object.Type == DbObjectType.Table ? Visibility.Collapsed : Visibility.Visible;

        public string TypeText
        {
            get
            {
                switch (Object.Type)
                {
                    case DbObjectType.Table: return "Table";
                    case DbObjectType.View: return "View";
                    case DbObjectType.StoredProcedure: return "Stored procedure";
                    case DbObjectType.ScalarFunction: return "Scalar function";
                    case DbObjectType.TableFunction: return "Table function";
                    case DbObjectType.Synonym: return "Synonym";
                    default: return "Object";
                }
            }
        }
    }
}
