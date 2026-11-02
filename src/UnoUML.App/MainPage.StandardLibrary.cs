using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnoUML.App;

public sealed partial class MainPage
{
    string[]? includePaths;

    async Task BrowseStandardLibraryAsync()
    {
        try
        {
            if (includePaths == null)
            {
                Status("Loading standard library index…");
                using var browser = JSHost.GlobalThis.GetPropertyAsJSObject("document")!;
                var baseUri = new Uri(browser.GetPropertyAsString("baseURI")!);
                using var client = new HttpClient();
                var index = await client.GetStringAsync(new Uri(baseUri, "uml/vendor/include-paths.txt"));
                includePaths = index.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            var packages = includePaths.Select(p => p[..p.IndexOf('/')]).Distinct().Prepend("All libraries").ToList();
            var package = new ComboBox { Header = "Library", ItemsSource = packages, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var search = new TextBox { Header = "Search include paths", PlaceholderText = "e.g. c4_context, kubernetes, lambda" };
            var list = new ListView { Height = 215, SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
            var result = Caption("");
            void Filter()
            {
                string query = search.Text.Trim();
                string? prefix = package.SelectedIndex > 0 ? packages[package.SelectedIndex] + "/" : null;
                // The sorted text index is cached only after this picker opens. Bound each UI update.
                var matches = includePaths.Where(path => (prefix == null || path.StartsWith(prefix, StringComparison.Ordinal)) &&
                    path.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(101).ToList();
                bool more = matches.Count == 101;
                if (more) matches.RemoveAt(100);
                list.ItemsSource = matches;
                list.SelectedIndex = matches.Count == 0 ? -1 : 0;
                result.Text = more ? "First 100 matches. Type to narrow the selection." : $"{matches.Count} matches";
            }
            search.TextChanged += (_, _) => Filter();
            package.SelectionChanged += (_, _) => Filter();
            var fields = new StackPanel { Spacing = 7 };
            fields.Children.Add(package); fields.Children.Add(search); fields.Children.Add(result); fields.Children.Add(list);
            Filter();
            Status($"{includePaths.Length:N0} standard library include paths available");
            if (await AskAsync("PlantUML standard library", fields, "Insert include") == ContentDialogResult.Primary && list.SelectedItem is string path)
                Edit(() => doc.Include(path), "This library already appears in the diagram or cannot be inserted here.");
        }
        catch (Exception ex) { Status($"Could not open the standard library. {ex.Message}", true); }
    }
}
