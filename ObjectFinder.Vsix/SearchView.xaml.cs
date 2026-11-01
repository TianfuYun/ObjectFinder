using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        public SearchView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            IsVisibleChanged += OnIsVisibleChanged;
            PreviewKeyDown += OnViewPreviewKeyDown;
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
            FocusSearchBox();
            _ = ReloadAsync(true);
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

            await _reloadGate.WaitAsync();
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
            if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None)
                return;

            e.Handled = true;
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
            switch (e.Key)
            {
                case Key.Down: MoveSelection(1); e.Handled = true; break;
                case Key.Up: MoveSelection(-1); e.Handled = true; break;
                case Key.Enter: ChooseSelected(); e.Handled = true; break;
                case Key.F5:
                    e.Handled = true;
                    _ = ReloadAsync(false);
                    break;
            }
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ChooseSelected();
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
            ResultRow row = ResultsList.SelectedItem as ResultRow;
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
