using System.Text;
using System.Windows;
using MediaDownloadManager.Core;
using Microsoft.Web.WebView2.Core;

namespace MediaDownloadManager;

public partial class LoginWindow : Window
{
    private readonly string _startUrl;

    public LoginWindow(string startUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F")
    {
        _startUrl = startUrl;
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, CookieStore.ProfileDir);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Login window failed: " + ex.Message);
            MessageBox.Show(this,
                "Cannot start the embedded browser (WebView2 runtime missing?).\n" + ex.Message,
                Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is null) return;
        DoneButton.IsEnabled = false;
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(null);
            var count = CookieStore.Save(cookies);
            if (!CookieStore.HasLoginCookie(cookies))
            {
                var go = MessageBox.Show(this,
                    "No YouTube/Google sign-in cookie found yet. Saved " + count + " cookies anyway.\nClose anyway?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (go != MessageBoxResult.Yes) { DoneButton.IsEnabled = true; return; }
            }
            ErrorLog.Write($"Saved {count} cookies to {CookieStore.FilePath}");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Save cookies failed: " + ex.Message);
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            DoneButton.IsEnabled = true;
        }
    }
}
