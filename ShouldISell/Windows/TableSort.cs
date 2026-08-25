using Dalamud.Bindings.ImGui;

namespace ShouldISell.Windows;

internal static class TableSort
{
    public static List<T> Apply<T>(IEnumerable<T> source, ImGuiTableSortSpecsPtr sortSpecs, params Func<T, object?>[] columns)
    {
        var rows = source.ToList();
        if (sortSpecs.IsNull || sortSpecs.SpecsCount <= 0)
            return rows;

        var spec = sortSpecs.Specs;
        var columnIndex = (int)spec.ColumnIndex;
        if (columnIndex < 0 || columnIndex >= columns.Length)
            return rows;

        var selector = columns[columnIndex];
        return spec.SortDirection == ImGuiSortDirection.Descending
            ? rows.OrderByDescending(selector, SortValueComparer.Instance).ToList()
            : rows.OrderBy(selector, SortValueComparer.Instance).ToList();
    }

    private sealed class SortValueComparer : IComparer<object?>
    {
        public static readonly SortValueComparer Instance = new();

        public int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;
            if (left is string leftText && right is string rightText)
                return StringComparer.CurrentCultureIgnoreCase.Compare(leftText, rightText);
            return left is IComparable comparable
                ? comparable.CompareTo(right)
                : StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString());
        }
    }
}
