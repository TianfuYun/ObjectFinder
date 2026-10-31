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

        public SearchView()
        {
            InitializeComponent();
            Loaded += delegate { FocusSearchBox(); };
            UpdateResults();
        }

        /// <summary>Raised when the user presses Enter or double-clicks a result.</summary>
        public event Action<DbObject> ObjectChosen;

        public void SetObjects(IEnumerable<DbObject> objects)
        {
            _searcher = new ObjectSearcher(objects);
            UpdateResults();
        }

        public void FocusSearchBox()
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }

        public void SetStatus(string text)
        {
            StatusText.Text = text;
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
                StatusText.Text = _searcher.Count + " objects. Start typing to search.";
                return;
            }

            List<ResultRow> rows = _searcher.Search(query, 50)
                .Select(r => new ResultRow(r.Item))
                .ToList();

            ResultsList.ItemsSource = rows;
            if (rows.Count > 0) ResultsList.SelectedIndex = 0;
            StatusText.Text = rows.Count == 0 ? "No matches." : rows.Count + " results";
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
            if (row != null && ObjectChosen != null)
                ObjectChosen(row.Object);
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

    internal static class SampleData
    {
        public static IReadOnlyList<DbObject> Create()
        {
            return new[]
            {
                new DbObject("dbo", "spTranslate", DbObjectType.StoredProcedure),
                new DbObject("dbo", "spTranslateAll", DbObjectType.StoredProcedure),
                new DbObject("dbo", "spGetOrders", DbObjectType.StoredProcedure),
                new DbObject("dbo", "sp_PrintMessage", DbObjectType.StoredProcedure),
                new DbObject("dbo", "vwOrders", DbObjectType.View),
                new DbObject("dbo", "vwOrderLinesWithPrices", DbObjectType.View),
                new DbObject("dbo", "Orders", DbObjectType.Table),
                new DbObject("dbo", "OrderLines", DbObjectType.Table),
                new DbObject("dbo", "Products", DbObjectType.Table),
                new DbObject("dbo", "fnGetPrice", DbObjectType.ScalarFunction),
                new DbObject("dbo", "fnSplitString", DbObjectType.TableFunction),
                new DbObject("sales", "Invoices", DbObjectType.Table),
                new DbObject("sales", "vwSalesByMonth", DbObjectType.View),
                new DbObject("dbo", "SynProducts", DbObjectType.Synonym),
            };
        }
    }
}
