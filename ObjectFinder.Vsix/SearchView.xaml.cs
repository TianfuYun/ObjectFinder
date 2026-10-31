using System;
using System.Collections.Generic;
using System.Linq;
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

        public SearchView()
        {
            InitializeComponent();
            Loaded += delegate { FocusSearchBox(); Reload(true); };
            IsVisibleChanged += OnIsVisibleChanged;
            UpdateResults();
        }

        /// <summary>Raised when the user presses Enter or double-clicks a result.</summary>
        public event Action<DbObject> ObjectChosen;

        public void FocusSearchBox()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }

        public void SetStatus(string text)
        {
            StatusText.Text = text;
        }

        /// <summary>
        /// Re-reads the object list for the database selected in Object Explorer.
        /// With keepCurrentOnFailure, a failed refresh (for example a server node is selected) leaves the
        /// previous list in place instead of replacing it with an error message.
        /// </summary>
        public void Reload(bool keepCurrentOnFailure)
        {
            try
            {
                ObjectExplorerSelection selection = ObjectExplorerBridge.GetSelection();
                List<DbObject> objects = ObjectExplorerBridge.LoadObjects(selection);

                _searcher = new ObjectSearcher(objects);
                _selection = selection;
                _source = selection.DatabaseName + " on " + selection.ServerName + ": " + objects.Count + " objects";
            }
            catch (Exception ex)
            {
                if (keepCurrentOnFailure && _searcher.Count > 0)
                    return;

                _searcher = new ObjectSearcher(new DbObject[0]);
                _selection = null;
                _source = ex.Message;
            }

            UpdateResults();
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
                Reload(true);
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
                case Key.F5: Reload(false); e.Handled = true; break;
            }
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ChooseSelected();
        }

        private void UpdateResults()
        {
            string query = SearchBox.Text;

            if (query.Trim() == "!diag")
            {
                ObjectExplorerDiagnostics.Run();
                StatusText.Text = "Diagnostics written to " + DiagLog.FilePath;
                return;
            }

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

            try
            {
                ObjectExplorerBridge.Reveal(_selection, row.Object);
                StatusText.Text = "Selected " + row.Object.FullName + " in Object Explorer";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
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
